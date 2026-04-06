using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using KcdMp.Shared.Protocol;

namespace KcdMp.Client;

/// <summary>
/// Bridges a local KCD2 game instance with the central relay server.
///
/// Responsibilities:
///   1. Wait for the game to have a save loaded (GameTime > 0).
///   2. Connect to the relay server via TCP and send Handshake.
///   3. Push local player position every tick (only when changed).
///   4. Receive Ghost packets from the relay server and update the local
///      game's ghost NPCs via the game debug REST API.
///
/// Smoothness optimisation:
///   - Position read = 1 HTTP call (GET PlayerSoul) per TickMs.
///   - Rotation + riding + combat state are read in a SEPARATE background loop every
///     RotStateIntervalMs (80 ms). Cached values are used by the position loop.
///     This cuts per-tick latency from ~50 ms to ~15 ms.
///   - Combat state changes trigger StateUpdate packets on the background loop.
/// </summary>
public partial class GameBridge(
    string serverHost,
    int serverPort,
    string password,
    string name,
    string gameApiBase,
    string? persistentToken = null,
    string? steamId = null,
    string? characterId = null,
    Serilog.ILogger? logger = null)
{
    private const int TickMs           = 10;
    private const int RotStateIntervalMs = 80;
    private const float PosThreshold  = 0.05f;
    private const float RotThreshold  = 0.02f;

    private readonly ILogger _logger = logger ?? Log.Logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    // Last pushed position (for change detection)
    private float _lastX, _lastY, _lastZ, _lastRotZ;
    private bool _hasPushed;

    // Cached rotation + riding state updated by background loop (volatile = visible across threads)
    private volatile float _cachedRotZ = 0f;
    private volatile bool  _cachedIsRiding = false;

    // Last sent combat state (for delta detection)
    private volatile byte _lastCombatFlags = 0;
    private volatile string _lastAnimName = "";

    private const int DamageCheckMs = 100;
    private volatile bool _isDead = false;

    // Serializes access to sv_servername CVar (both StateLoop and DamageEventLoop use it)
    private readonly SemaphoreSlim _cvarLock = new(1, 1);

    // Ping: maps sent timestamp (ticks) → Stopwatch timestamp at send time
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _pingsSent = new();

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await WaitForGameAsync(ct);
            if (ct.IsCancellationRequested) break;

            try
            {
                await ConnectAndRunAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Error(ex, "[!] Unexpected error");
            }

            if (ct.IsCancellationRequested) break;
            _logger.Information("Reconnecting in 3 s...");
            await Task.Delay(3000, ct).ContinueWith(_ => { });
        }
    }

    // -------------------------------------------------------------------------
    // Phase 1 – wait for a save to be loaded
    // -------------------------------------------------------------------------

    private async Task WaitForGameAsync(CancellationToken ct = default)
    {
        _logger.Information("Waiting for game to load a save...");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var xml = await _http.GetStringAsync($"{gameApiBase}/api/rpg/Calendar?depth=1");
                var m = GameTimeRegex().Match(xml);
                if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) && t > 0)
                {
                    _logger.Information("Game ready!");
                    return;
                }
            }
            catch { /* game not running yet */ }

            await Task.Delay(3000, ct).ContinueWith(_ => { });
        }
    }

    // -------------------------------------------------------------------------
    // Phase 2 – connected to relay server
    // -------------------------------------------------------------------------

    private async Task ConnectAndRunAsync(CancellationToken appCt = default)
    {
        using var tcp = new TcpClient();
        _isDead = false;

        _logger.Information("Connecting to relay server {ServerHost}:{ServerPort}...", serverHost, serverPort);
        try
        {
            await tcp.ConnectAsync(serverHost, serverPort);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[!] Cannot connect");
            return;
        }

        var stream = tcp.GetStream();

        // --- Auth (if password set) ---
        if (!string.IsNullOrEmpty(password))
        {
            await stream.WriteAsync(PacketWriter.Auth(password));
            var authResult = await stream.ReadPacketAsync();
            var (ok, message) = PacketReader.ParseAuthResult(authResult.Payload);
            if (!ok)
            {
                _logger.Error("[!] Auth failed: {Message}", message);
                return;
            }
            _logger.Information("Authenticated.");
        }

        // --- Handshake ---
        await stream.WriteAsync(PacketWriter.Handshake(BuildHandshakePayload()));

        // --- Ack (S→C  0xFF [id:1]) ---
        var ackPacket = await stream.ReadPacketAsync();
        byte myId = ackPacket.Payload[0];
        _logger.Information("Connected! Assigned id={MyId}", myId);

        _hasPushed = false;

        // Kick off the Lua interp tick immediately so KCD2MP.isRiding gets updated
        // even before the first ghost is spawned (e.g. player already on horse at connect time).
        try { await ExecLuaAsync("if KCD2MP_StartInterp then KCD2MP_StartInterp() end"); }
        catch { /* ignore if mod not loaded yet */ }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);

        // Start background tasks
        var receiveTask  = ReceiveLoopAsync(stream, cts.Token);
        var stateTask    = StateLoopAsync(stream, cts.Token);
        var pingTask     = PingLoopAsync(stream, cts.Token);
        var damageTask   = DamageEventLoopAsync(stream, cts.Token);

        // --- Position push loop ---
        try
        {
            int tickCount = 0;
            long totalReadMs = 0;

            while (tcp.Connected)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pos = await ReadPositionAsync();
                sw.Stop();
                totalReadMs += sw.ElapsedMilliseconds;
                tickCount++;

                if (pos.HasValue)
                {
                    var (x, y, z) = pos.Value;
                    float rotZ    = _cachedRotZ;
                    bool  riding  = _cachedIsRiding;

                    if (!_hasPushed || HasChanged(x, y, z, rotZ))
                    {
                        _hasPushed = true;
                        _lastX = x; _lastY = y; _lastZ = z; _lastRotZ = rotZ;
                        await SendPositionAsync(stream, x, y, z, rotZ, riding);
                        _logger.Information("[pos] {X:F1} {Y:F1} {Z:F1}  rot={RotZ:F2}  riding={Riding}  read={ReadMs}ms",
                            x, y, z, rotZ, riding, sw.ElapsedMilliseconds);
                    }
                }

                // Print average read time every 100 ticks
                if (tickCount % 100 == 0)
                    _logger.Information("[stat] avg read={AvgRead}ms over {TickCount} ticks",
                        totalReadMs / tickCount, tickCount);

                await Task.Delay(TickMs);
            }
        }
        finally
        {
            cts.Cancel();
            try { await receiveTask;  } catch { }
            try { await stateTask; } catch { }
            try { await pingTask;     } catch { }
            try { await damageTask;   } catch { }
            _logger.Information("Removing all ghosts...");
            try { await ExecLuaAsync("KCD2MP_RemoveAllGhosts()"); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Background state loop (rotation + riding + combat state every 80ms)
    // -------------------------------------------------------------------------

    private async Task PingLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                long ts = DateTime.UtcNow.Ticks;
                _pingsSent[ts] = System.Diagnostics.Stopwatch.GetTimestamp();
                await stream.WriteAsync(PacketWriter.Ping(ts), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private async Task StateLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Serialize CVar access (DamageEventLoop also uses sv_servername)
                await _cvarLock.WaitAsync(ct);
                try
                {
                    // Combined CVar read: rotation, riding, combat flags, animation
                    await ExecLuaAsync(
                        @"System.SetCVar(""sv_servername"",(function()" +
                        @"local r=player:GetWorldAngles().z;" +
                        @"local ride=KCD2MP and KCD2MP.isRiding and 'r' or 's';" +
                        @"local cm=player.soul:IsInCombatMode() and 1 or 0;" +
                        @"local cd=player.soul:IsInCombatDanger() and 1 or 0;" +
                        @"local sn=KCD2MP and KCD2MP.playerSneaking and 1 or 0;" +
                        @"local anim='';" +
                        @"pcall(function() anim=tostring(player.actor:GetCurrentAnimationState(0) or '') end);" +
                        @"return string.format('%.4f,%s,%d,%d,%d,%s',r,ride,cm,cd,sn,anim)end)())");

                    var xml = await _http.GetStringAsync(
                        $"{gameApiBase}/api/System/Console/GetCvarValue?name=sv_servername");
                    var m = CvarValueRegex().Match(xml);
                    if (m.Success)
                    {
                        var parts = m.Groups[1].Value.Split(',', 6);
                        if (parts.Length >= 5)
                        {
                            // Rotation + riding
                            if (float.TryParse(parts[0], NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out float rot))
                                _cachedRotZ = rot;
                            _cachedIsRiding = parts[1].Trim() == "r";

                            // Combat state flags
                            byte flags = 0;
                            if (parts[2] == "1") flags |= 0x01; // weapon drawn
                            if (parts[3] == "1") flags |= 0x02; // combat danger
                            if (parts[4] == "1") flags |= 0x04; // sneaking
                            if (_cachedIsRiding) flags |= 0x10;  // riding

                            string animName = parts.Length >= 6 ? parts[5] : "";

                            // Only send StateUpdate when combat state changes
                            if (flags != _lastCombatFlags || animName != _lastAnimName)
                            {
                                _lastCombatFlags = flags;
                                _lastAnimName = animName;

                                var animBytes = System.Text.Encoding.UTF8.GetBytes(animName);
                                if (animBytes.Length > 255) animBytes = animBytes[..255];
                                var payload = new byte[1 + 1 + animBytes.Length];
                                payload[0] = flags;
                                payload[1] = (byte)animBytes.Length;
                                Buffer.BlockCopy(animBytes, 0, payload, 2, animBytes.Length);

                                var packet = PacketWriter.StateUpdate(
                                    (byte)StateType.CombatState, payload);
                                await stream.WriteAsync(packet, ct);

                                _logger.Debug("[state] flags={Flags:X2} anim={Anim}",
                                    flags, animName);
                            }
                        }
                    }
                }
                finally { _cvarLock.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.Warning(ex, "[state] Read error"); }

            await Task.Delay(RotStateIntervalMs, ct); // reuse existing 80ms interval
        }
    }

    private async Task DamageEventLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Serialize CVar access (StateLoop also uses sv_servername)
                await _cvarLock.WaitAsync(ct);
                try
                {
                    // Read + clear pending damage events from Lua
                    await ExecLuaAsync(
                        @"System.SetCVar(""sv_servername"",(function()" +
                        @"if not KCD2MP or not KCD2MP.pendingDamageEvents then return 'none' end;" +
                        @"local events=KCD2MP.pendingDamageEvents;" +
                        @"KCD2MP.pendingDamageEvents={};" +
                        @"if #events==0 then return 'none' end;" +
                        @"local parts={};" +
                        @"for i,e in ipairs(events) do " +
                        @"parts[#parts+1]=string.format('%s:%s:%s',e.type or '',e.entityName or '',e.damage or 0) end;" +
                        @"return table.concat(parts,'|')end)())");

                    var xml = await _http.GetStringAsync(
                        $"{gameApiBase}/api/System/Console/GetCvarValue?name=sv_servername");
                    var m = CvarValueRegex().Match(xml);
                    if (m.Success && m.Groups[1].Value != "none")
                    {
                        var events = m.Groups[1].Value.Split('|');
                        foreach (var evt in events)
                        {
                            var parts = evt.Split(':', 3);
                            if (parts.Length < 3) continue;

                            var type = parts[0];
                            var entityName = parts[1];
                            var damage = parts[2];

                            if (!int.TryParse(damage, out var dmgValue)) continue;

                            if (type == "friendly_fire")
                            {
                                var json = System.Text.Encoding.UTF8.GetBytes(
                                    $"{{\"amount\":{dmgValue}}}");
                                await stream.WriteAsync(
                                    PacketWriter.Event((ushort)EventType.DamageDealt, json), ct);
                                _logger.Information("[event] Friendly fire: {Damage} damage", dmgValue);
                            }
                            else if (type == "npc_damage" && !string.IsNullOrEmpty(entityName))
                            {
                                var json = System.Text.Encoding.UTF8.GetBytes(
                                    $"{{\"entity\":\"{EscapeJson(entityName)}\",\"amount\":{dmgValue}}}");
                                await stream.WriteAsync(
                                    PacketWriter.Event((ushort)EventType.NpcDamage, json), ct);
                                _logger.Information("[event] NPC damage: {Entity} took {Damage}",
                                    entityName, dmgValue);
                            }
                        }
                    }
                }
                finally { _cvarLock.Release(); }

                // Death check uses PlayerSoul endpoint, not sv_servername — no CVar conflict
                if (!_isDead)
                {
                    var healthXml = await _http.GetStringAsync(
                        $"{gameApiBase}/api/rpg/SoulList/PlayerSoul?depth=1");
                    if (healthXml.Contains("Health=\"0\"") || healthXml.Contains("IsDead=\"true\""))
                    {
                        _isDead = true;
                        var json = "{}"u8.ToArray();
                        await stream.WriteAsync(
                            PacketWriter.Event((ushort)EventType.PlayerDied, json), ct);
                        _logger.Warning("[event] Player died — notifying partner");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.Warning(ex, "[event] Poll error"); }

            await Task.Delay(DamageCheckMs, ct);
        }
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeLua(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\0", "");

    private string BuildHandshakePayload()
    {
        var trimmedToken = string.IsNullOrWhiteSpace(persistentToken) ? null : persistentToken.Trim();
        var trimmedSteamId = string.IsNullOrWhiteSpace(steamId) ? null : steamId.Trim();
        var trimmedCharacterId = string.IsNullOrWhiteSpace(characterId) ? null : characterId.Trim();

        if (trimmedToken is null && trimmedSteamId is null && trimmedCharacterId is null)
            return name;

        var payload = new Dictionary<string, string?>
        {
            ["displayName"] = name,
            ["persistentToken"] = trimmedToken,
            ["steamId"] = trimmedSteamId,
            ["characterId"] = trimmedCharacterId,
        };
        return JsonSerializer.Serialize(payload);
    }

    // -------------------------------------------------------------------------
    // Receive loop – server pushes Ghost and Name packets to us
    // -------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await stream.ReadPacketAsync(ct);

                switch (packet.Type)
                {
                    case PacketType.Pong when packet.Payload.Length == 8:
                        long ts = PacketReader.ReadInt64(packet.Payload, 0);
                        if (_pingsSent.TryRemove(ts, out long sentAt))
                        {
                            int ms = (int)((System.Diagnostics.Stopwatch.GetTimestamp() - sentAt)
                                           * 1000L / System.Diagnostics.Stopwatch.Frequency);
                            _logger.Information("[ping] {Ms} ms", ms);
                            try { await ExecLuaAsync($"KCD2MP_ShowPing({ms})"); } catch { }
                        }
                        break;
                    case PacketType.Ghost when packet.Payload.Length is 17 or 18:
                        var (ghostId, x, y, z, rotZ, flags) = PacketReader.ParseGhost(packet.Payload);
                        bool isRiding = (flags & 0x01) != 0;
                        await UpdateGhostAsync(ghostId.ToString(), x, y, z, rotZ, isRiding);
                        break;
                    case PacketType.Name when packet.Payload.Length >= 2:
                        var (nameGhostId, gname) = PacketReader.ParseName(packet.Payload);
                        await SetGhostNameAsync(nameGhostId.ToString(), gname);
                        break;
                    case PacketType.Disconnect when packet.Payload.Length == 1:
                        byte dcGhostId = packet.Payload[0];
                        _logger.Information("[disconnect] ghost {GhostId} removed", dcGhostId);
                        try { await ExecLuaAsync($"KCD2MP_RemoveGhost(\"{dcGhostId}\")"); } catch { }
                        break;

                    case PacketType.StateSync when packet.Payload.Length >= 2:
                    {
                        var (srcId, stateType, statePayload) = PacketReader.ParseStateSync(packet.Payload);
                        await HandleStateSyncAsync(srcId, stateType, statePayload);
                        break;
                    }

                    case PacketType.EventRelay when packet.Payload.Length >= 3:
                    {
                        var (srcId, eventType, jsonPayload) = PacketReader.ParseEventRelay(packet.Payload);
                        await HandleEventRelayAsync(srcId, eventType, jsonPayload);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException) { }
    }

    private async Task HandleStateSyncAsync(byte sourceId, byte stateType, byte[] payload)
    {
        try
        {
            if (stateType == (byte)StateType.CombatState && payload.Length >= 2)
            {
                byte flags = payload[0];
                int animLen = payload[1];
                string animName = animLen > 0 && payload.Length >= 2 + animLen
                    ? System.Text.Encoding.UTF8.GetString(payload, 2, animLen)
                    : "";

                var safeAnim = EscapeLua(animName);
                var lua = $"KCD2MP_ApplyCombatState(\"{sourceId}\",{flags},\"{safeAnim}\")";
                await ExecLuaAsync(lua);
                _logger.Debug("[state-in] src={Src} flags={Flags:X2} anim={Anim}",
                    sourceId, flags, animName);
            }
            else if (stateType == (byte)StateType.Equipment)
            {
                var json = System.Text.Encoding.UTF8.GetString(payload);
                var safeJson = EscapeLua(json);
                var lua = $"KCD2MP_ApplyEquipment(\"{sourceId}\",\"{safeJson}\")";
                await ExecLuaAsync(lua);
                _logger.Debug("[state-in] src={Src} equipment={Json}", sourceId, json);
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "[state-in] Error applying state"); }
    }

    private async Task HandleEventRelayAsync(byte sourceId, ushort eventType, byte[] jsonPayload)
    {
        var json = System.Text.Encoding.UTF8.GetString(jsonPayload);
        _logger.Information("[event-in] src={Src} type={Type} json={Json}",
            sourceId, (EventType)eventType, json);

        try
        {
            switch ((EventType)eventType)
            {
                case EventType.DamageDealt:
                {
                    var match = Regex.Match(json, @"""amount""\s*:\s*(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var amount))
                    {
                        await ExecLuaAsync(
                            $"player.soul:DealDamage({amount},0,__null,true);" +
                            $"Game.SendInfoText(\"Your partner hit you! (-{amount} HP)\")");
                    }
                    break;
                }

                case EventType.NpcDamage:
                {
                    var nameMatch = Regex.Match(json, @"""entity""\s*:\s*""([^""]+)""");
                    var amtMatch = Regex.Match(json, @"""amount""\s*:\s*(\d+)");
                    if (nameMatch.Success && amtMatch.Success
                        && !string.IsNullOrEmpty(nameMatch.Groups[1].Value)
                        && int.TryParse(amtMatch.Groups[1].Value, out var npcDmg))
                    {
                        var safeName = EscapeLua(nameMatch.Groups[1].Value);
                        await ExecLuaAsync(
                            $"KCD2MP_HandleNpcDamage(\"{safeName}\",{npcDmg})");
                    }
                    break;
                }

                case EventType.PlayerDied:
                {
                    await ExecLuaAsync(
                        "Game.SendInfoText(\"Your partner has fallen!\");" +
                        "player.soul:DealDamage(99999,99999,__null,true)");
                    _logger.Warning("[event-in] Partner died — triggering mutual game-over");
                    break;
                }

                case EventType.PlayerDowned:
                {
                    await ExecLuaAsync(
                        "Game.SendInfoText(\"Your partner is down! Get to them!\")");
                    break;
                }

                case EventType.Revive:
                {
                    await ExecLuaAsync(
                        "Game.SendInfoText(\"You have been revived!\")");
                    break;
                }
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "[event-in] Error handling event {Type}", (EventType)eventType); }
    }

    // -------------------------------------------------------------------------
    // Game REST API helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads local player position via a single HTTP call (GET PlayerSoul).
    /// Rotation and riding state come from the background StateLoopAsync.
    /// </summary>
    private async Task<(float x, float y, float z)?> ReadPositionAsync()
    {
        try
        {
            var xml = await _http.GetStringAsync($"{gameApiBase}/api/rpg/SoulList/PlayerSoul?depth=1");
            var posMatch = PosRegex().Match(xml);
            if (!posMatch.Success) return null;

            var parts = posMatch.Groups[1].Value.Split(',');
            if (parts.Length < 3) return null;

            float x = float.Parse(parts[0], CultureInfo.InvariantCulture);
            float y = float.Parse(parts[1], CultureInfo.InvariantCulture);
            float z = float.Parse(parts[2], CultureInfo.InvariantCulture);
            return (x, y, z);
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateGhostAsync(string ghostId, float x, float y, float z, float rotZ, bool isRiding)
    {
        string gx   = x.ToString("F2",  CultureInfo.InvariantCulture);
        string gy   = y.ToString("F2",  CultureInfo.InvariantCulture);
        string gz   = z.ToString("F2",  CultureInfo.InvariantCulture);
        string rot  = rotZ.ToString("F4", CultureInfo.InvariantCulture);
        string ride = isRiding ? "true" : "false";

        try
        {
            await ExecLuaAsync($@"KCD2MP_UpdateGhost(""{ghostId}"",{gx},{gy},{gz},{rot},{ride})");
            _logger.Information("[ghost {GhostId}] {X} {Y} {Z} riding={IsRiding}", ghostId, gx, gy, gz, isRiding);
        }
        catch { /* game might have unloaded */ }
    }

    private async Task SetGhostNameAsync(string ghostId, string ghostName)
    {
        // Escape any quotes in name to avoid Lua injection
        var safeName = ghostName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        try
        {
            await ExecLuaAsync($@"KCD2MP_SetGhostName(""{ghostId}"",""{safeName}"")");
            _logger.Information("[name] ghost {GhostId} = {GhostName}", ghostId, ghostName);
        }
        catch { }
    }

    private async Task ExecLuaAsync(string lua)
    {
        var cmd = Uri.EscapeDataString($"#{lua}");
        await _http.GetStringAsync($"{gameApiBase}/api/System/Console/ExecuteString?command={cmd}");
    }

    // -------------------------------------------------------------------------
    // TCP helpers
    // -------------------------------------------------------------------------

    private static async Task SendPositionAsync(NetworkStream stream, float x, float y, float z, float rotZ, bool isRiding)
    {
        await stream.WriteAsync(PacketWriter.Position(x, y, z, rotZ, isRiding));
    }

    private bool HasChanged(float x, float y, float z, float rotZ) =>
        Math.Abs(x - _lastX)       > PosThreshold ||
        Math.Abs(y - _lastY)       > PosThreshold ||
        Math.Abs(z - _lastZ)       > PosThreshold ||
        Math.Abs(rotZ - _lastRotZ) > RotThreshold;

    // -------------------------------------------------------------------------
    // Source-generated regexes
    // -------------------------------------------------------------------------

    [GeneratedRegex(@"GameTime=""([^""]+)""")]
    private static partial Regex GameTimeRegex();

    [GeneratedRegex(@"Position=""([^""]+)""")]
    private static partial Regex PosRegex();

    [GeneratedRegex(@">([^<]*)<")]
    private static partial Regex CvarValueRegex();
}
