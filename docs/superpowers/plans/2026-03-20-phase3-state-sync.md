# Phase 3: State Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add damage relay, combat sync, and equipment appearance on top of existing position sync so the ghost NPC feels like a real co-op partner.

**Architecture:** Layered sync — position (10ms, existing), state (200ms, new), events (on-demand, new). Protocol uses reserved packet types 0x07-0x0A. Server stays stateless. Client adds a 200ms state loop and event handlers. Lua mod adds OnHit hook, combat state reading, and ghost faction/animation changes.

**Tech Stack:** C# .NET 8 (xUnit tests), Lua 5.1 (CryEngine mod), TCP binary protocol

**Spec:** `docs/superpowers/specs/2026-03-20-phase3-state-sync-design.md`

---

## File Structure

### New Files

| Path | Responsibility |
|------|----------------|
| `dotnet/KcdMp.Shared/Protocol/StateType.cs` | State type enum (CombatState, Equipment) |
| `dotnet/KcdMp.Shared/Protocol/EventType.cs` | Event type enum (DamageDealt, PlayerDied, etc.) |
| `dotnet/KcdMp.Tests/Protocol/StateEventPacketTests.cs` | Round-trip tests for state/event packets |
| `probes/phase3/*.lua` | Probe scripts for Phase 3.0 |
| `probes/phase3/results.md` | Probe findings |

### Modified Files

| Path | Changes |
|------|---------|
| `dotnet/KcdMp.Shared/Protocol/PacketWriter.cs` | Add StateUpdate, StateSync, Event, EventRelay builders |
| `dotnet/KcdMp.Shared/Protocol/PacketReader.cs` | Add ParseStateUpdate, ParseStateSync, ParseEvent, ParseEventRelay |
| `dotnet/KcdMp.Server/RelayServer.cs` | Add BroadcastState, BroadcastEvent methods |
| `dotnet/KcdMp.Server/ClientSession.cs` | Extend packet loop to route 0x07 and 0x09 |
| `dotnet/KcdMp.Client/GameBridge.cs` | Add StateLoopAsync (200ms), event sending/receiving |
| `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs` | Add state + event relay integration tests |
| `kdcmp/Data/Scripts/Startup/kdcmp.lua` | OnHit hook, combat state, faction, damage handling |

---

## Task 1: Probe Session (Phase 3.0)

**Manual task — run on Windows PC via probe scripts. C# tasks (2-8) can proceed in parallel since they don't depend on probe results. After probes complete, review results before starting Tasks 9-11 (Lua mod changes) — failed probes descope those tasks.**

**Files:**
- Create: `probes/phase3/probe-onhit.lua`
- Create: `probes/phase3/probe-knockout.lua`
- Create: `probes/phase3/probe-faction.lua`
- Create: `probes/phase3/probe-aggro.lua`
- Create: `probes/phase3/probe-invulnerability.lua`
- Create: `probes/phase3/probe-animation.lua`
- Create: `probes/phase3/probe-equipment.lua`
- Create: `probes/phase3/results.md`

- [ ] **Step 1: Create probe scripts**

All probes use the existing `scripts/probe.sh` workflow. Each probe is a Lua snippet.

`probes/phase3/probe-onhit.lua` — Tests if SinglePlayer.Client.OnHit can be hooked from mod code:
```lua
-- Must be loaded via mod startup, NOT console (console hooks don't fire)
-- Add this to kdcmp.lua temporarily, then attack an NPC and check sv_servername
local hitLog = {}
local origOnHit = SinglePlayer and SinglePlayer.Client and SinglePlayer.Client.OnHit
if origOnHit then
    SinglePlayer.Client.OnHit = function(self, hit)
        local info = string.format("target=%s,shooter=%s,dmg=%s",
            tostring(hit.targetId), tostring(hit.shooterId), tostring(hit.damage))
        System.SetCVar("sv_servername", info)
        if origOnHit then origOnHit(self, hit) end
    end
    System.LogAlways("[PROBE] OnHit hook installed")
else
    System.LogAlways("[PROBE] SinglePlayer.Client.OnHit does NOT exist")
end
```

`probes/phase3/probe-knockout.lua`:
```lua
-- Test stamina-only damage for unconsciousness
player.soul:DealDamage(0, 99999, __null, true)
-- Check: did player go unconscious or just lose stamina?
System.SetCVar("sv_servername", string.format("hp=%s,dead=%s",
    tostring(player.actor:GetHealth()), tostring(player.actor:IsDead())))
```

`probes/phase3/probe-faction.lua`:
```lua
-- Read player faction
local faction = "unknown"
pcall(function() faction = tostring(player.Properties.esFaction) end)
local faction2 = "unknown"
pcall(function() faction2 = tostring(player.soul.faction) end)
local faction3 = "unknown"
pcall(function() faction3 = tostring(player.soul:GetFaction()) end)
System.SetCVar("sv_servername", string.format("prop=%s,soul=%s,get=%s",
    faction, faction2, faction3))
```

`probes/phase3/probe-aggro.lua`:
```lua
-- Spawn NPC with player faction near hostiles, check if hostiles attack it
-- Run near bandits in Kuttenburg debug
XGenAIModule.SpawnEntity{
    Name = "probe_aggro_test",
    ClassName = "NPC",
    Pos = player:GetWorldPos(),
    Properties = {
        esFaction = "<FACTION_FROM_PROBE_C>",
        esModularBehaviorTree = "",
    },
}
-- Wait, then check if nearby hostiles target it
-- Try with behavior tree variants if no aggro
```

`probes/phase3/probe-invulnerability.lua`:
```lua
-- Test bInvulnerable on spawned NPC
local e = System.GetEntityByName("probe_aggro_test")
if e then
    pcall(function() e.Properties.Health = { bInvulnerable = true } end)
    e.soul:DealDamage(99999, 99999, __null, true)
    System.SetCVar("sv_servername", string.format("hp=%s,dead=%s",
        tostring(e.actor:GetHealth()), tostring(e.actor:IsDead())))
end
```

`probes/phase3/probe-animation.lua`:
```lua
-- Read combat animation state during combat (run while fighting)
local anim = "none"
pcall(function() anim = tostring(player.actor:GetCurrentAnimationState(0)) end)
local anim1 = "none"
pcall(function() anim1 = tostring(player.actor:GetCurrentAnimationState(1)) end)
System.SetCVar("sv_servername", string.format("layer0=%s,layer1=%s", anim, anim1))
```

`probes/phase3/probe-equipment.lua`:
```lua
-- Try various methods to read equipped items
local results = {}
pcall(function() table.insert(results, "getCurrentItem0=" .. tostring(player.actor:GetCurrentItem(0))) end)
pcall(function() table.insert(results, "getCurrentItem1=" .. tostring(player.actor:GetCurrentItem(1))) end)
pcall(function() table.insert(results, "getEquipped=" .. tostring(player.inventory:GetEquippedItem(0))) end)
-- Enumerate inventory methods
pcall(function()
    for k,v in pairs(getmetatable(player.inventory).__index or {}) do
        if type(v) == "function" then table.insert(results, "inv:" .. k) end
    end
end)
System.SetCVar("sv_servername", table.concat(results, "|"))
```

- [ ] **Step 2: Run probes on Windows PC**

For OnHit probe: temporarily add the hook code to `kdcmp.lua`, deploy, load game, attack an NPC, then read CVar:
```bash
./scripts/deploy.sh mikey@100.106.212.128
GAME_API="http://100.106.212.128:1404" ./scripts/probe.sh 'System.GetCVar("sv_servername")'
```

For other probes, run via probe.sh directly (they work from console):
```bash
GAME_API="http://100.106.212.128:1404" ./scripts/probe.sh "$(cat probes/phase3/probe-knockout.lua)"
```

- [ ] **Step 3: Document results in `probes/phase3/results.md`**

Record each probe result. Update the spec's probe status (lines 35-69 of `docs/superpowers/specs/2026-03-20-phase3-state-sync-design.md`) from UNKNOWN/PLAUSIBLE to VERIFIED/NOT WORKING.

**Decision gate:** After documenting results, determine which Lua tasks (9-11) to proceed with:
- Probe F failed → Skip Task 9 (OnHit hook). Damage relay descoped to death sync only.
- Probe B failed → Skip equipment reading in Task 10. Ghost uses static preset.
- Probes C/D/G failed → Skip Task 11 (faction/invulnerability). Ghost stays Civilian faction.
- Probe E failed → Remove animation sync from Task 10. Combat flags only (no mirroring).
- Probe A failed → Remove revive flow from Task 8. Use mutual game-over only.

- [ ] **Step 4: Commit probe scripts and results**

```bash
git add probes/phase3/
git commit -m "probes: Phase 3.0 probe scripts and results"
```

---

## Task 2: Protocol Enums

**Files:**
- Create: `dotnet/KcdMp.Shared/Protocol/StateType.cs`
- Create: `dotnet/KcdMp.Shared/Protocol/EventType.cs`

- [ ] **Step 1: Create StateType enum**

```csharp
// dotnet/KcdMp.Shared/Protocol/StateType.cs
namespace KcdMp.Shared.Protocol;

public enum StateType : byte
{
    CombatState = 1,
    Equipment   = 2,
}
```

- [ ] **Step 2: Create EventType enum**

```csharp
// dotnet/KcdMp.Shared/Protocol/EventType.cs
namespace KcdMp.Shared.Protocol;

public enum EventType : ushort
{
    DamageDealt  = 1,
    PlayerDied   = 2,
    PlayerDowned = 3,
    Revive       = 4,
    NpcDamage    = 5,
}
```

- [ ] **Step 3: Verify build**

Run: `cd dotnet && dotnet build`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add dotnet/KcdMp.Shared/Protocol/StateType.cs dotnet/KcdMp.Shared/Protocol/EventType.cs
git commit -m "feat: add StateType and EventType protocol enums"
```

---

## Task 3: PacketWriter/Reader — State Packets + Tests

**Files:**
- Modify: `dotnet/KcdMp.Shared/Protocol/PacketWriter.cs`
- Modify: `dotnet/KcdMp.Shared/Protocol/PacketReader.cs`
- Create: `dotnet/KcdMp.Tests/Protocol/StateEventPacketTests.cs`

- [ ] **Step 1: Write failing tests for StateUpdate/StateSync round-trip**

```csharp
// dotnet/KcdMp.Tests/Protocol/StateEventPacketTests.cs
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Protocol;

public class StateEventPacketTests
{
    [Fact]
    public void StateUpdate_RoundTrips()
    {
        byte stateType = (byte)StateType.CombatState;
        byte[] payload = [0b00000101]; // flags: weapon drawn + sneaking

        var packet = PacketWriter.StateUpdate(stateType, payload);

        Assert.Equal((byte)PacketType.StateUpdate, packet[0]);
        var (parsedType, parsedPayload) = PacketReader.ParseStateUpdate(packet[3..]);
        Assert.Equal(stateType, parsedType);
        Assert.Equal(payload, parsedPayload);
    }

    [Fact]
    public void StateSync_RoundTrips()
    {
        byte sourceId = 2;
        byte stateType = (byte)StateType.Equipment;
        byte[] payload = "{\"clothing\":\"abc\"}"u8.ToArray();

        var packet = PacketWriter.StateSync(sourceId, stateType, payload);

        Assert.Equal((byte)PacketType.StateSync, packet[0]);
        var (parsedSrc, parsedType, parsedPayload) = PacketReader.ParseStateSync(packet[3..]);
        Assert.Equal(sourceId, parsedSrc);
        Assert.Equal(stateType, parsedType);
        Assert.Equal(payload, parsedPayload);
    }

    [Fact]
    public void StateUpdate_WithAnimationName_RoundTrips()
    {
        byte stateType = (byte)StateType.CombatState;
        var animName = "combat_sword_idle"u8.ToArray();
        byte[] payload = new byte[1 + 1 + animName.Length];
        payload[0] = 0b00000011; // flags
        payload[1] = (byte)animName.Length;
        Buffer.BlockCopy(animName, 0, payload, 2, animName.Length);

        var packet = PacketWriter.StateUpdate(stateType, payload);
        var (parsedType, parsedPayload) = PacketReader.ParseStateUpdate(packet[3..]);

        Assert.Equal(stateType, parsedType);
        Assert.Equal(payload, parsedPayload);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd dotnet && dotnet test --filter StateEventPacketTests -v n`
Expected: FAIL — `PacketWriter` does not contain `StateUpdate`

- [ ] **Step 3: Implement PacketWriter.StateUpdate and PacketWriter.StateSync**

Add to `dotnet/KcdMp.Shared/Protocol/PacketWriter.cs`:

```csharp
public static byte[] StateUpdate(byte stateType, byte[] payload)
{
    var buf = new byte[1 + payload.Length];
    buf[0] = stateType;
    Buffer.BlockCopy(payload, 0, buf, 1, payload.Length);
    return Build(PacketType.StateUpdate, buf);
}

public static byte[] StateSync(byte sourceId, byte stateType, byte[] payload)
{
    var buf = new byte[2 + payload.Length];
    buf[0] = sourceId;
    buf[1] = stateType;
    Buffer.BlockCopy(payload, 0, buf, 2, payload.Length);
    return Build(PacketType.StateSync, buf);
}
```

- [ ] **Step 4: Implement PacketReader.ParseStateUpdate and ParseStateSync**

Add to `dotnet/KcdMp.Shared/Protocol/PacketReader.cs`:

```csharp
public static (byte stateType, byte[] payload) ParseStateUpdate(byte[] payload)
{
    var data = new byte[payload.Length - 1];
    Buffer.BlockCopy(payload, 1, data, 0, data.Length);
    return (payload[0], data);
}

public static (byte sourceId, byte stateType, byte[] payload) ParseStateSync(byte[] payload)
{
    var data = new byte[payload.Length - 2];
    Buffer.BlockCopy(payload, 2, data, 0, data.Length);
    return (payload[0], payload[1], data);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd dotnet && dotnet test --filter StateEventPacketTests -v n`
Expected: 3 passed

- [ ] **Step 6: Commit**

```bash
git add dotnet/KcdMp.Shared/Protocol/PacketWriter.cs dotnet/KcdMp.Shared/Protocol/PacketReader.cs dotnet/KcdMp.Tests/Protocol/StateEventPacketTests.cs
git commit -m "feat: add StateUpdate/StateSync packet builder and parser"
```

---

## Task 4: PacketWriter/Reader — Event Packets + Tests

**Files:**
- Modify: `dotnet/KcdMp.Shared/Protocol/PacketWriter.cs`
- Modify: `dotnet/KcdMp.Shared/Protocol/PacketReader.cs`
- Modify: `dotnet/KcdMp.Tests/Protocol/StateEventPacketTests.cs`

- [ ] **Step 1: Write failing tests for Event/EventRelay round-trip**

Add to `StateEventPacketTests.cs`:

```csharp
[Fact]
public void Event_RoundTrips()
{
    ushort eventType = (ushort)EventType.DamageDealt;
    byte[] json = "{\"amount\":50}"u8.ToArray();

    var packet = PacketWriter.Event(eventType, json);

    Assert.Equal((byte)PacketType.Event, packet[0]);
    var (parsedType, parsedJson) = PacketReader.ParseEvent(packet[3..]);
    Assert.Equal(eventType, parsedType);
    Assert.Equal(json, parsedJson);
}

[Fact]
public void EventRelay_RoundTrips()
{
    byte sourceId = 1;
    ushort eventType = (ushort)EventType.NpcDamage;
    byte[] json = "{\"entity\":\"krab_man_4\",\"amount\":50}"u8.ToArray();

    var packet = PacketWriter.EventRelay(sourceId, eventType, json);

    Assert.Equal((byte)PacketType.EventRelay, packet[0]);
    var (parsedSrc, parsedType, parsedJson) = PacketReader.ParseEventRelay(packet[3..]);
    Assert.Equal(sourceId, parsedSrc);
    Assert.Equal(eventType, parsedType);
    Assert.Equal(json, parsedJson);
}

[Fact]
public void Event_EmptyPayload_RoundTrips()
{
    ushort eventType = (ushort)EventType.PlayerDied;
    byte[] json = "{}"u8.ToArray();

    var packet = PacketWriter.Event(eventType, json);
    var (parsedType, parsedJson) = PacketReader.ParseEvent(packet[3..]);

    Assert.Equal(eventType, parsedType);
    Assert.Equal(json, parsedJson);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd dotnet && dotnet test --filter StateEventPacketTests -v n`
Expected: 3 new tests FAIL — `PacketWriter` does not contain `Event`

- [ ] **Step 3: Implement PacketWriter.Event and PacketWriter.EventRelay**

Add to `dotnet/KcdMp.Shared/Protocol/PacketWriter.cs`:

```csharp
public static byte[] Event(ushort eventType, byte[] jsonPayload)
{
    var buf = new byte[2 + jsonPayload.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), eventType);
    Buffer.BlockCopy(jsonPayload, 0, buf, 2, jsonPayload.Length);
    return Build(PacketType.Event, buf);
}

public static byte[] EventRelay(byte sourceId, ushort eventType, byte[] jsonPayload)
{
    var buf = new byte[3 + jsonPayload.Length];
    buf[0] = sourceId;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(1, 2), eventType);
    Buffer.BlockCopy(jsonPayload, 0, buf, 3, jsonPayload.Length);
    return Build(PacketType.EventRelay, buf);
}
```

- [ ] **Step 4: Implement PacketReader.ParseEvent and ParseEventRelay**

Add to `dotnet/KcdMp.Shared/Protocol/PacketReader.cs`:

```csharp
public static (ushort eventType, byte[] jsonPayload) ParseEvent(byte[] payload)
{
    var eventType = ReadUInt16(payload, 0);
    var json = new byte[payload.Length - 2];
    Buffer.BlockCopy(payload, 2, json, 0, json.Length);
    return (eventType, json);
}

public static (byte sourceId, ushort eventType, byte[] jsonPayload) ParseEventRelay(byte[] payload)
{
    var eventType = ReadUInt16(payload, 1);
    var json = new byte[payload.Length - 3];
    Buffer.BlockCopy(payload, 3, json, 0, json.Length);
    return (payload[0], eventType, json);
}
```

- [ ] **Step 5: Run all tests**

Run: `cd dotnet && dotnet test -v n`
Expected: All pass (existing + 6 new)

- [ ] **Step 6: Commit**

```bash
git add dotnet/KcdMp.Shared/Protocol/PacketWriter.cs dotnet/KcdMp.Shared/Protocol/PacketReader.cs dotnet/KcdMp.Tests/Protocol/StateEventPacketTests.cs
git commit -m "feat: add Event/EventRelay packet builder and parser"
```

---

## Task 5: Server — State and Event Relay

**Files:**
- Modify: `dotnet/KcdMp.Server/RelayServer.cs`
- Modify: `dotnet/KcdMp.Server/ClientSession.cs`

- [ ] **Step 1: Write failing integration test for state relay**

Add to `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs`:

```csharp
[Fact]
public async Task Client_SendsStateUpdate_OtherClientReceivesStateSync()
{
    using var tcp1 = new TcpClient();
    await tcp1.ConnectAsync("127.0.0.1", Port);
    var s1 = tcp1.GetStream();
    await AuthAndHandshake(s1, "Alice");
    byte id1 = await ReadAckId(s1);

    using var tcp2 = new TcpClient();
    await tcp2.ConnectAsync("127.0.0.1", Port);
    var s2 = tcp2.GetStream();
    await AuthAndHandshake(s2, "Bob");
    byte id2 = await ReadAckId(s2);

    // Drain name packets
    await DrainPackets(s1, 1);
    await DrainPackets(s2, 1);

    // Client 1 sends StateUpdate
    byte stateType = (byte)StateType.CombatState;
    byte[] flags = [0b00000101];
    var statePacket = PacketWriter.StateUpdate(stateType, flags);
    await s1.WriteAsync(statePacket);

    // Client 2 should receive StateSync
    var packet = await s2.ReadPacketAsync();
    Assert.Equal(PacketType.StateSync, packet.Type);
    var (srcId, parsedType, payload) = PacketReader.ParseStateSync(packet.Payload);
    Assert.Equal(id1, srcId);
    Assert.Equal(stateType, parsedType);
    Assert.Equal(flags, payload);
}

[Fact]
public async Task Client_SendsEvent_OtherClientReceivesEventRelay()
{
    using var tcp1 = new TcpClient();
    await tcp1.ConnectAsync("127.0.0.1", Port);
    var s1 = tcp1.GetStream();
    await AuthAndHandshake(s1, "Alice");
    byte id1 = await ReadAckId(s1);

    using var tcp2 = new TcpClient();
    await tcp2.ConnectAsync("127.0.0.1", Port);
    var s2 = tcp2.GetStream();
    await AuthAndHandshake(s2, "Bob");
    await ReadAckId(s2);

    // Drain name packets
    await DrainPackets(s1, 1);
    await DrainPackets(s2, 1);

    // Client 1 sends Event
    ushort eventType = (ushort)EventType.DamageDealt;
    byte[] json = "{\"amount\":50}"u8.ToArray();
    var eventPacket = PacketWriter.Event(eventType, json);
    await s1.WriteAsync(eventPacket);

    // Client 2 should receive EventRelay
    var packet = await s2.ReadPacketAsync();
    Assert.Equal(PacketType.EventRelay, packet.Type);
    var (srcId, parsedEvt, parsedJson) = PacketReader.ParseEventRelay(packet.Payload);
    Assert.Equal(id1, srcId);
    Assert.Equal(eventType, parsedEvt);
    Assert.Equal(json, parsedJson);
}
```

Note: `AuthAndHandshake`, `ReadAckId`, `DrainPackets` are helper methods. If they don't exist yet in the test file, extract them from the existing test patterns (the existing tests do auth + handshake inline).

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd dotnet && dotnet test --filter "StateUpdate_OtherClient|SendsEvent_OtherClient" -v n`
Expected: FAIL — server drops non-Position packets

- [ ] **Step 3: Add BroadcastState and BroadcastEvent to RelayServer**

Add to `dotnet/KcdMp.Server/RelayServer.cs`:

```csharp
public void BroadcastState(ClientSession source, byte stateType, byte[] payload)
{
    List<ClientSession> targets;
    lock (_lock)
        targets = [.. _clients.Where(c => c != source && c.IsReady)];

    var packet = PacketWriter.StateSync(source.Id, stateType, payload);
    foreach (var target in targets)
        target.EnqueueRaw(packet);
}

public void BroadcastEvent(ClientSession source, ushort eventType, byte[] jsonPayload)
{
    List<ClientSession> targets;
    lock (_lock)
        targets = [.. _clients.Where(c => c != source && c.IsReady)];

    var packet = PacketWriter.EventRelay(source.Id, eventType, jsonPayload);
    foreach (var target in targets)
        target.EnqueueRaw(packet);
}
```

- [ ] **Step 4: Extend ClientSession packet loop to route state and event packets**

In `dotnet/KcdMp.Server/ClientSession.cs`, replace the rigid Position-only guard in the main loop. Find the section that looks like:

```csharp
if (packet.Type != PacketType.Position ||
    (packet.Payload.Length != 16 && packet.Payload.Length != 17))
    continue;

var (x, y, z, rotZ, flags) = PacketReader.ParsePosition(packet.Payload);
_server.Broadcast(this, x, y, z, rotZ, flags);
```

Replace with:

```csharp
switch (packet.Type)
{
    case PacketType.Position when packet.Payload.Length is 16 or 17:
        var (x, y, z, rotZ, flags) = PacketReader.ParsePosition(packet.Payload);
        _server.Broadcast(this, x, y, z, rotZ, flags);
        break;

    case PacketType.StateUpdate when packet.Payload.Length >= 1:
        var (stateType, statePayload) = PacketReader.ParseStateUpdate(packet.Payload);
        _server.BroadcastState(this, stateType, statePayload);
        break;

    case PacketType.Event when packet.Payload.Length >= 2:
        var (eventType, jsonPayload) = PacketReader.ParseEvent(packet.Payload);
        _server.BroadcastEvent(this, eventType, jsonPayload);
        break;
}
```

- [ ] **Step 5: Run all tests**

Run: `cd dotnet && dotnet test -v n`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add dotnet/KcdMp.Server/RelayServer.cs dotnet/KcdMp.Server/ClientSession.cs dotnet/KcdMp.Tests/Integration/RelayServerTests.cs
git commit -m "feat: relay StateUpdate and Event packets through server"
```

---

## Task 6: Client — Combined State Loop (replaces RotStateLoopAsync)

**This task replaces `RotStateLoopAsync` with a combined loop that reads rotation + combat state in one CVar call, avoiding the CVar race condition that would occur if both loops wrote to `sv_servername`.**

**Files:**
- Modify: `dotnet/KcdMp.Client/GameBridge.cs`

- [ ] **Step 1: Add state sync constants and fields**

Add alongside existing constants (near line 30 of GameBridge.cs):

```csharp
// Last sent combat state (for delta detection)
private volatile byte _lastCombatFlags = 0;
private volatile string _lastAnimName = "";
```

- [ ] **Step 2: Create StateLoopAsync method (replaces RotStateLoopAsync)**

This combines rotation/riding reads (from RotStateLoopAsync) with new combat state reads into one CVar call:

```csharp
private async Task StateLoopAsync(NetworkStream stream, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
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
                    // Rotation + riding (replaces RotStateLoopAsync)
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
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { _logger.Warning(ex, "[state] Read error"); }

        await Task.Delay(RotStateIntervalMs, ct); // reuse existing 80ms interval
    }
}
```

- [ ] **Step 3: Replace RotStateLoopAsync with StateLoopAsync in ConnectAndRunAsync**

Find where parallel tasks are started:
```csharp
var receiveTask  = ReceiveLoopAsync(stream, cts.Token);
var rotStateTask = RotStateLoopAsync(cts.Token);
var pingTask     = PingLoopAsync(stream, cts.Token);
```

Replace `rotStateTask` line:
```csharp
var receiveTask  = ReceiveLoopAsync(stream, cts.Token);
var stateTask    = StateLoopAsync(stream, cts.Token);
var pingTask     = PingLoopAsync(stream, cts.Token);
```

Update the finally block to match (replace `rotStateTask` with `stateTask`).

- [ ] **Step 4: Delete RotStateLoopAsync method**

Remove the entire `RotStateLoopAsync` method. Its functionality is now in `StateLoopAsync`.

- [ ] **Step 5: Add using for Protocol namespace**

Ensure the file has:
```csharp
using KcdMp.Shared.Protocol;
```

- [ ] **Step 6: Verify build and run all tests**

Run: `cd dotnet && dotnet build && dotnet test -v n`
Expected: Build succeeded, all tests pass

- [ ] **Step 7: Commit**

```bash
git add dotnet/KcdMp.Client/GameBridge.cs
git commit -m "feat: replace RotStateLoop with combined state loop (rotation + combat)"
```

---

## Task 7: Client — Event Sending (Damage + Death)

**Files:**
- Modify: `dotnet/KcdMp.Client/GameBridge.cs`

- [ ] **Step 1: Add event sending fields**

```csharp
private const int DamageCheckMs = 100;
private volatile bool _isDead = false;
```

- [ ] **Step 2: Create DamageEventLoopAsync method**

```csharp
private async Task DamageEventLoopAsync(NetworkStream stream, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
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

                    // Validate damage is numeric before building JSON
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

            // Check for death
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
```

- [ ] **Step 3: Launch DamageEventLoopAsync in ConnectAndRunAsync**

Add alongside other tasks:
```csharp
var damageTask = DamageEventLoopAsync(stream, cts.Token);
```

And in finally block:
```csharp
try { await damageTask; } catch { }
```

Reset death state on reconnect (add at start of ConnectAndRunAsync):
```csharp
_isDead = false;
```

- [ ] **Step 4: Verify build**

Run: `cd dotnet && dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add dotnet/KcdMp.Client/GameBridge.cs
git commit -m "feat: add damage event polling and death detection in client"
```

---

## Task 8: Client — Incoming State/Event Handling

**Files:**
- Modify: `dotnet/KcdMp.Client/GameBridge.cs`

- [ ] **Step 1: Extend ReceiveLoopAsync for StateSync and EventRelay**

Find the `switch` statement in `ReceiveLoopAsync` that handles `PacketType.Ghost` and `PacketType.Name`. Add new cases:

```csharp
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
```

- [ ] **Step 2: Implement HandleStateSyncAsync**

Note: `EscapeLua` sanitizes strings before interpolation into Lua code, preventing injection from malicious packets. Same pattern as existing `SetGhostNameAsync`.

```csharp
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

// Escapes a string for safe interpolation into Lua string literals
private static string EscapeLua(string s) =>
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
```

- [ ] **Step 3: Implement HandleEventRelayAsync**

```csharp
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
                var match = System.Text.RegularExpressions.Regex.Match(json, @"""amount""\s*:\s*(\d+)");
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
                var nameMatch = System.Text.RegularExpressions.Regex.Match(json, @"""entity""\s*:\s*""([^""]+)""");
                var amtMatch = System.Text.RegularExpressions.Regex.Match(json, @"""amount""\s*:\s*(\d+)");
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
```

- [ ] **Step 4: Verify build**

Run: `cd dotnet && dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Run all tests**

Run: `cd dotnet && dotnet test -v n`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add dotnet/KcdMp.Client/GameBridge.cs
git commit -m "feat: handle incoming state sync and event relay in client"
```

---

## Task 9: Lua — OnHit Hook + Damage Event Queue

**Depends on:** Probe F (OnHit hook verification) from Task 1. If Probe F fails, skip this task — damage relay is descoped to death sync only.

**Files:**
- Modify: `kdcmp/Data/Scripts/Startup/kdcmp.lua`

- [ ] **Step 1: Add damage event queue initialization**

Near the top of the file (after `KCD2MP = {}` and other table inits):

```lua
KCD2MP.pendingDamageEvents = {}
KCD2MP.ghostEntityIds = {}  -- maps entity ID -> ghost player ID
```

- [ ] **Step 2: Track ghost entity IDs in KCD2MP_SpawnGhost**

After the ghost entity is successfully spawned and stored in `KCD2MP.ghosts[id]`, add:

```lua
if entity and entity.id then
    KCD2MP.ghostEntityIds[entity.id] = id
end
```

And in `KCD2MP_RemoveGhost`, clean up:
```lua
if ghost.entity and ghost.entity.id then
    KCD2MP.ghostEntityIds[ghost.entity.id] = nil
end
```

- [ ] **Step 3: Install OnHit hook**

Add near the OnAction hook section (around line 2308), after the Player.Client.OnAction hooks:

```lua
-- Hook hit system for damage detection (friendly fire + shared NPC combat)
if SinglePlayer and SinglePlayer.Client then
    local origSPOnHit = SinglePlayer.Client.OnHit
    SinglePlayer.Client.OnHit = function(self, hit)
        pcall(function()
            if not hit or not player then return end
            local isGhostTarget = KCD2MP.ghostEntityIds[hit.targetId]
            local isPlayerShooter = (hit.shooterId == player.id)

            if isGhostTarget and isPlayerShooter then
                -- Player hitting partner's ghost -> friendly fire
                KCD2MP.pendingDamageEvents[#KCD2MP.pendingDamageEvents + 1] = {
                    type = "friendly_fire",
                    entityName = "",
                    damage = hit.damage or 0,
                }
            elseif isPlayerShooter and not isGhostTarget then
                -- Player hitting an NPC -> shared combat
                local target = System.GetEntity(hit.targetId)
                if target and target.GetName then
                    KCD2MP.pendingDamageEvents[#KCD2MP.pendingDamageEvents + 1] = {
                        type = "npc_damage",
                        entityName = target:GetName() or "",
                        damage = hit.damage or 0,
                    }
                end
            end
            -- NPC hitting ghost: ignored (ghost is invulnerable)
        end)

        if origSPOnHit then pcall(origSPOnHit, self, hit) end
    end
    System.LogAlways("[KCD2-MP] OnHit hook installed")
end
```

- [ ] **Step 4: Add KCD2MP_HandleNpcDamage function**

Add after the existing KCD2MP functions:

```lua
function KCD2MP_HandleNpcDamage(entityName, amount)
    pcall(function()
        local entity = System.GetEntityByName(entityName)
        if entity and entity.soul then
            entity.soul:DealDamage(amount, 0, __null, true)
        end
    end)
end
```

- [ ] **Step 5: Deploy and verify OnHit hook fires**

```bash
./scripts/deploy.sh mikey@100.106.212.128
```

Load game, attack an NPC, then check if damage events are queued:
```bash
GAME_API="http://100.106.212.128:1404" ./scripts/probe.sh \
  'System.SetCVar("sv_servername", tostring(#(KCD2MP.pendingDamageEvents or {})))'
```
Expected: Returns count > 0 after hitting an NPC

- [ ] **Step 6: Commit**

```bash
git add kdcmp/Data/Scripts/Startup/kdcmp.lua
git commit -m "feat: add OnHit hook and damage event queue to Lua mod"
```

---

## Task 10: Lua — Combat State + Animation Functions

**Files:**
- Modify: `kdcmp/Data/Scripts/Startup/kdcmp.lua`

- [ ] **Step 1: Add KCD2MP_ApplyCombatState function**

```lua
function KCD2MP_ApplyCombatState(ghostId, flags, animName)
    pcall(function()
        local ghost = KCD2MP.ghosts[ghostId]
        if not ghost or not ghost.entity then return end

        local weaponDrawn = (flags % 2) >= 1         -- bit 0
        local inDanger    = (math.floor(flags/2) % 2) >= 1  -- bit 1
        local sneaking    = (math.floor(flags/4) % 2) >= 1  -- bit 2

        -- Apply weapon drawn/holstered
        if ghost.entity.actor and ghost.entity.actor.HolsterItem then
            pcall(function() ghost.entity.actor:HolsterItem(not weaponDrawn) end)
        end

        -- Apply animation if provided and different from current
        if animName and animName ~= "" and ghost.istate then
            if ghost.istate.lastAnim ~= animName then
                ghost.istate.lastAnim = animName
                pcall(function() ghost.entity:StartAnimation(0, animName) end)
            end
        end
    end)
end
```

- [ ] **Step 2: Add KCD2MP_ApplyEquipment function**

```lua
function KCD2MP_ApplyEquipment(ghostId, jsonStr)
    pcall(function()
        local ghost = KCD2MP.ghosts[ghostId]
        if not ghost or not ghost.entity then return end

        -- Simple JSON parsing for {"clothing":"guid","weapon":"guid"}
        local clothing = jsonStr:match('"clothing"%s*:%s*"([^"]+)"')
        local weapon = jsonStr:match('"weapon"%s*:%s*"([^"]+)"')

        if clothing and ghost.entity.actor and ghost.entity.actor.EquipClothingPreset then
            pcall(function() ghost.entity.actor:EquipClothingPreset(clothing) end)
        end
        if weapon and ghost.entity.actor and ghost.entity.actor.EquipWeaponPreset then
            pcall(function() ghost.entity.actor:EquipWeaponPreset(weapon) end)
        end
    end)
end
```

- [ ] **Step 3: Verify build by deploying**

```bash
./scripts/deploy.sh mikey@100.106.212.128
```

Load game, verify no Lua errors in console.

- [ ] **Step 4: Commit**

```bash
git add kdcmp/Data/Scripts/Startup/kdcmp.lua
git commit -m "feat: add combat state and equipment application to Lua mod"
```

---

## Task 11: Lua — Ghost Spawn Changes (Faction + Invulnerability)

**Depends on:** Probes C, D, G from Task 1.

**Files:**
- Modify: `kdcmp/Data/Scripts/Startup/kdcmp.lua`

- [ ] **Step 1: Update XGenAIModule.SpawnEntity call in KCD2MP_SpawnGhost**

Find the `XGenAIModule.SpawnEntity` call (around line 108). Update `Properties`:

```lua
XGenAIModule.SpawnEntity{
    Name      = name,
    ClassName = "NPC",
    Pos       = {x, y, z},
    Properties = {
        esFaction = "<FACTION_FROM_PROBE_C>",  -- replace with probe result
        esModularBehaviorTree = "<BT_FROM_PROBE_G>",  -- replace with probe result, or "" if aggro works without AI
        Health = { bInvulnerable = true },  -- if Probe D confirms this works
    },
}
```

Replace `<FACTION_FROM_PROBE_C>` and `<BT_FROM_PROBE_G>` with actual values from probe results. If probes failed, keep existing values (`"Civilians"`, `""`).

If Probe D fails (`bInvulnerable` doesn't work), add NPC damage filtering in the OnHit hook instead — the hook in Task 9 already ignores NPC-on-ghost hits (`isGhostTarget and not isPlayerShooter`), so the ghost won't relay NPC damage. But without invulnerability, the ghost's soul health will deplete from NPC attacks and it may die/ragdoll. In that case, respawn the ghost on death detection.

- [ ] **Step 2: Deploy and verify ghost draws NPC aggro**

```bash
./scripts/deploy.sh mikey@100.106.212.128
```

Load game near hostile NPCs. Spawn a ghost. Verify hostiles target the ghost.
If invulnerability works, verify ghost survives NPC attacks.

- [ ] **Step 3: Commit**

```bash
git add kdcmp/Data/Scripts/Startup/kdcmp.lua
git commit -m "feat: update ghost spawn with player faction and invulnerability"
```

---

## Task 12: End-to-End Validation

**Manual task — requires two game instances connected via relay server.**

- [ ] **Step 1: Deploy latest build to Windows PC**

```bash
./scripts/deploy.sh mikey@100.106.212.128
```

- [ ] **Step 2: Run through E2E checklist**

From the spec's manual E2E checklist:

- [ ] Player A attacks NPC -> NPC takes damage in Player B's game
- [ ] Player B attacks Player A's ghost -> Player A takes real damage
- [ ] Player A dies -> both players game-over
- [ ] Ghost draws NPC aggro in combat encounters (if Probe G succeeded)
- [ ] Ghost is invulnerable to NPC damage (if Probe D succeeded)
- [ ] Ghost shows combat animations (if Probe E succeeded)
- [ ] Ghost shows correct equipment (if Probe B succeeded)
- [ ] Combat state flags sync (weapon drawn/sheathed visible on ghost)
- [ ] "Partner took damage" flash notification works
- [ ] Death notification displays correctly
- [ ] NPC killed by relayed damage dies visually in both games

- [ ] **Step 3: Document results and file issues for any failures**

- [ ] **Step 4: Final commit with any fixes**

```bash
git add -A
git commit -m "fix: E2E test fixes for Phase 3 state sync"
```
