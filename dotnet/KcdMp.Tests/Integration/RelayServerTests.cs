using System.Net.Sockets;
using System.Text.Json;
using KcdMp.Server.AccessControl;
using KcdMp.Server.Bans;
using KcdMp.Server.Characters;
using KcdMp.Server.Identity;
using KcdMp.Server.Persistence;
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Integration;

[Collection("Integration")]
public class RelayServerTests : IAsyncLifetime
{
    private KcdMp.Server.RelayServer _server = null!;
    private Task _serverTask = null!;
    private CancellationTokenSource _cts = null!;
    private string _persistenceRoot = "";
    private const int TestPort = 17778;
    private const string TestPassword = "testpass";

    public async Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        _persistenceRoot = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_test_{Guid.NewGuid():N}");
        _server = new KcdMp.Server.RelayServer(
            TestPort,
            password: TestPassword,
            persistenceOptions: new JsonPersistenceOptions { BasePath = _persistenceRoot });
        _serverTask = _server.RunAsync(_cts.Token);
        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _serverTask; } catch { }
        if (Directory.Exists(_persistenceRoot))
            Directory.Delete(_persistenceRoot, recursive: true);
    }

    [Fact]
    public async Task Client_WithCorrectPassword_GetsAck()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", TestPort);
        var stream = tcp.GetStream();

        await stream.WriteAsync(PacketWriter.Auth(TestPassword));
        var authResponse = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.AuthResult, authResponse.Type);
        var (ok, _) = PacketReader.ParseAuthResult(authResponse.Payload);
        Assert.True(ok);

        await stream.WriteAsync(PacketWriter.Handshake("TestPlayer"));
        var ackPacket = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.Ack, ackPacket.Type);
    }

    [Fact]
    public async Task Client_WithWrongPassword_GetsRejected()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", TestPort);
        var stream = tcp.GetStream();

        await stream.WriteAsync(PacketWriter.Auth("wrongpassword"));
        var authResponse = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.AuthResult, authResponse.Type);
        var (ok, _) = PacketReader.ParseAuthResult(authResponse.Payload);
        Assert.False(ok);
    }

    [Fact]
    public async Task Client_NoPassword_ServerWithNoPassword_Connects()
    {
        using var cts2 = new CancellationTokenSource();
        var openServer = new KcdMp.Server.RelayServer(TestPort + 1, password: "");
        var openTask = openServer.RunAsync(cts2.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", TestPort + 1);
            var stream = tcp.GetStream();

            await stream.WriteAsync(PacketWriter.Handshake("OpenPlayer"));
            var ack = await stream.ReadPacketAsync();
            Assert.Equal(PacketType.Ack, ack.Type);
        }
        finally
        {
            cts2.Cancel();
            try { await openTask; } catch { }
        }
    }

    [Fact]
    public async Task Client_SendsStateUpdate_OtherClientReceivesStateSync()
    {
        var (tcp1, s1, id1) = await ConnectClientAsync("Alice");
        var (tcp2, s2, id2) = await ConnectClientAsync("Bob");
        using var _ = tcp1;
        using var __ = tcp2;

        // When second client connects, both get Name packets about each other
        await DrainPacketsAsync(s1, 1); // Alice gets Bob's name
        await DrainPacketsAsync(s2, 1); // Bob gets Alice's name

        // Client 1 sends StateUpdate
        byte stateType = (byte)StateType.CombatState;
        byte[] flags = [0b00000101];
        var statePacket = PacketWriter.StateUpdate(stateType, flags);
        await s1.WriteAsync(statePacket);

        // Client 2 should receive StateSync
        var packet = await ReadPacketByTypeAsync(s2, PacketType.StateSync, TimeSpan.FromSeconds(2));
        Assert.Equal(PacketType.StateSync, packet.Type);
        var (srcId, parsedType, payload) = PacketReader.ParseStateSync(packet.Payload);
        Assert.Equal(id1, srcId);
        Assert.Equal(stateType, parsedType);
        Assert.Equal(flags, payload);
    }

    [Fact]
    public async Task Client_SendsEvent_OtherClientReceivesEventRelay()
    {
        var (tcp1, s1, id1) = await ConnectClientAsync("Alice");
        var (tcp2, s2, id2) = await ConnectClientAsync("Bob");
        using var _ = tcp1;
        using var __ = tcp2;

        // Drain name packets
        await DrainPacketsAsync(s1, 1);
        await DrainPacketsAsync(s2, 1);

        // Client 1 sends Event
        ushort eventType = (ushort)EventType.DamageDealt;
        byte[] json = "{\"amount\":50}"u8.ToArray();
        var eventPacket = PacketWriter.Event(eventType, json);
        await s1.WriteAsync(eventPacket);

        // Client 2 should receive EventRelay
        var packet = await ReadPacketByTypeAsync(s2, PacketType.EventRelay, TimeSpan.FromSeconds(2));
        Assert.Equal(PacketType.EventRelay, packet.Type);
        var (srcId, parsedEvt, parsedJson) = PacketReader.ParseEventRelay(packet.Payload);
        Assert.Equal(id1, srcId);
        Assert.Equal(eventType, parsedEvt);
        Assert.Equal(json, parsedJson);
    }

    [Fact]
    public async Task Client_ReceivesInitialStateProjection_AndCanReportApplyResult()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", TestPort);
        var stream = tcp.GetStream();

        await stream.WriteAsync(PacketWriter.Auth(TestPassword));
        var authResponse = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.AuthResult, authResponse.Type);
        Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

        await stream.WriteAsync(PacketWriter.Handshake("ProjectionPlayer"));
        var ackPacket = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.Ack, ackPacket.Type);

        var projectionPacket = await ReadPacketByTypeAsync(stream, PacketType.StateProjection, TimeSpan.FromSeconds(2));
        var (projectionId, domain, _, _) = PacketReader.ParseStateProjection(projectionPacket.Payload);
        var started = PacketWriter.StateProjectionResult(
            projectionId,
            domain,
            (byte)ProjectionApplyStatus.Started,
            """{"message":"started"}"""u8.ToArray());
        await stream.WriteAsync(started);
        var applied = PacketWriter.StateProjectionResult(
            projectionId,
            domain,
            (byte)ProjectionApplyStatus.Applied,
            """{"message":"applied"}"""u8.ToArray());
        await stream.WriteAsync(applied);

        long pingTs = DateTime.UtcNow.Ticks;
        await stream.WriteAsync(PacketWriter.Ping(pingTs));
        var pong = await ReadPacketByTypeAsync(stream, PacketType.Pong, TimeSpan.FromSeconds(2));
        Assert.Equal(PacketType.Pong, pong.Type);
    }

    [Fact]
    public async Task Client_WithSameIdentityFallbackName_IsRejectedWhenAlreadyConnected()
    {
        var (tcp1, _, _) = await ConnectClientAsync("SameName");
        using var _ = tcp1;

        using var tcp2 = new TcpClient();
        await tcp2.ConnectAsync("127.0.0.1", TestPort);
        var stream2 = tcp2.GetStream();

        await stream2.WriteAsync(PacketWriter.Auth(TestPassword));
        var authResponse = await stream2.ReadPacketAsync();
        Assert.Equal(PacketType.AuthResult, authResponse.Type);
        Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

        await stream2.WriteAsync(PacketWriter.Handshake("SameName"));
        var rejected = await stream2.ReadPacketAsync();

        Assert.Equal(PacketType.AuthResult, rejected.Type);
        Assert.False(PacketReader.ParseAuthResult(rejected.Payload).ok);
    }

    [Fact]
    public async Task Client_HandshakeJson_BindsSessionToRequestedCharacter()
    {
        const int port = TestPort + 2;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_bind_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var identities = new PlayerIdentityService(store, new NullServerObservabilitySink());
        var characters = new CharacterProfileService(store, identities, new NullServerObservabilitySink());
        var identity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Henry", "token_henry", null));
        var character = await characters.CreateAsync(identity.Identity!.InternalId, new CharacterCreateRequest("Henry", "Skalitz", "knight"));

        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceStore: store,
            identityService: identities,
            characterBindingOptions: new CharacterSessionBindingOptions { RequireCharacterOnConnect = true });
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();

            await stream.WriteAsync(PacketWriter.Auth(TestPassword));
            var authResponse = await stream.ReadPacketAsync();
            Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

            var handshake = $$"""{"displayName":"Henry","persistentToken":"token_henry","characterId":"{{character.Character!.InternalId}}"}""";
            await stream.WriteAsync(PacketWriter.Handshake(handshake));
            var ackPacket = await stream.ReadPacketAsync();
            Assert.Equal(PacketType.Ack, ackPacket.Type);

            await Task.Delay(100);
            var activeSession = server.GetActiveSessions().Single();
            Assert.Equal(identity.Identity.InternalId, activeSession.IdentityId);
            Assert.Equal(character.Character.InternalId, activeSession.CharacterId);
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_WhenCharacterRequired_AndNoneExists_IsRejected()
    {
        const int port = TestPort + 3;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_bind_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var identities = new PlayerIdentityService(store, new NullServerObservabilitySink());

        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceStore: store,
            identityService: identities,
            characterBindingOptions: new CharacterSessionBindingOptions { RequireCharacterOnConnect = true });
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();

            await stream.WriteAsync(PacketWriter.Auth(TestPassword));
            var authResponse = await stream.ReadPacketAsync();
            Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

            var handshake = """{"displayName":"NoChar","persistentToken":"token_no_char"}""";
            await stream.WriteAsync(PacketWriter.Handshake(handshake));
            var rejected = await stream.ReadPacketAsync();

            Assert.Equal(PacketType.AuthResult, rejected.Type);
            Assert.False(PacketReader.ParseAuthResult(rejected.Payload).ok);
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_NewIdentityInWhitelistMode_IsRejectedAndPersistedAsPending()
    {
        const int port = TestPort + 4;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_access_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var identities = new PlayerIdentityService(store, new NullServerObservabilitySink());
        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceStore: store,
            identityService: identities,
            accessControlOptions: new ServerAccessControlOptions
            {
                DefaultAccessMode = ServerAccessMode.Whitelist,
            });
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();

            await stream.WriteAsync(PacketWriter.Auth(TestPassword));
            var authResponse = await stream.ReadPacketAsync();
            Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

            await stream.WriteAsync(PacketWriter.Handshake("WhitelistNewPlayer"));
            var rejected = await stream.ReadPacketAsync();

            Assert.Equal(PacketType.AuthResult, rejected.Type);
            var (ok, _) = PacketReader.ParseAuthResult(rejected.Payload);
            Assert.False(ok);

            var allIdentities = await identities.ListAllAsync();
            var created = Assert.Single(allIdentities);
            Assert.Equal(PlayerIdentityStatus.Pending, created.Status);
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_PendingIdentityInOpenMode_IsAllowed()
    {
        const int port = TestPort + 5;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_access_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var identities = new PlayerIdentityService(store, new NullServerObservabilitySink());
        var created = await identities.ResolveOrCreateAsync(
            new PlayerIdentityClaim("PendingPlayer", "pending_token", null),
            ServerAccessMode.Whitelist);
        Assert.NotNull(created.Identity);
        Assert.Equal(PlayerIdentityStatus.Pending, created.Identity!.Status);

        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceStore: store,
            identityService: identities,
            accessControlOptions: new ServerAccessControlOptions
            {
                DefaultAccessMode = ServerAccessMode.Open,
            });
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();

            await stream.WriteAsync(PacketWriter.Auth(TestPassword));
            var authResponse = await stream.ReadPacketAsync();
            Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

            var handshake = """{"displayName":"PendingPlayer","persistentToken":"pending_token"}""";
            await stream.WriteAsync(PacketWriter.Handshake(handshake));
            var packet = await stream.ReadPacketAsync();
            Assert.Equal(PacketType.Ack, packet.Type);
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_WithActiveBan_IsRejectedOnHandshake()
    {
        const int port = TestPort + 6;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_ban_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var identities = new PlayerIdentityService(store, new NullServerObservabilitySink());
        var bans = new IdentityBanService(store, new NullServerObservabilitySink());
        var created = await identities.ResolveOrCreateAsync(
            new PlayerIdentityClaim("BannedPlayer", "token_banned", null),
            ServerAccessMode.Open);
        Assert.NotNull(created.Identity);
        await bans.ApplyBanAsync(new IdentityBanApplyRequest(
            created.Identity!.InternalId,
            IdentityBanType.Permanent,
            null,
            "Serious misconduct",
            "admin_1",
            Summary: "You are banned from this server."));

        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceStore: store,
            identityService: identities,
            identityBanService: bans);
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();

            await stream.WriteAsync(PacketWriter.Auth(TestPassword));
            var authResponse = await stream.ReadPacketAsync();
            Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

            var handshake = """{"displayName":"BannedPlayer","persistentToken":"token_banned"}""";
            await stream.WriteAsync(PacketWriter.Handshake(handshake));
            var rejected = await stream.ReadPacketAsync();
            var (ok, reason) = PacketReader.ParseAuthResult(rejected.Payload);

            Assert.False(ok);
            Assert.Equal("You are banned from this server.", reason);
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyIdentityBanAsync_WhenIdentityConnected_KicksSessionImmediately()
    {
        const int port = TestPort + 7;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_ban_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceOptions: new JsonPersistenceOptions { BasePath = root });
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();

            await stream.WriteAsync(PacketWriter.Auth(TestPassword));
            var authResponse = await stream.ReadPacketAsync();
            Assert.True(PacketReader.ParseAuthResult(authResponse.Payload).ok);

            var handshake = """{"displayName":"LivePlayer","persistentToken":"token_live"}""";
            await stream.WriteAsync(PacketWriter.Handshake(handshake));
            var ack = await stream.ReadPacketAsync();
            Assert.Equal(PacketType.Ack, ack.Type);

            await Task.Delay(100);
            var activeSession = Assert.Single(server.GetActiveSessions());
            Assert.False(string.IsNullOrWhiteSpace(activeSession.IdentityId));

            var apply = await server.ApplyIdentityBanAsync(new IdentityBanApplyRequest(
                activeSession.IdentityId!,
                IdentityBanType.Permanent,
                null,
                "Ban while connected",
                "admin_live",
                Summary: "Disconnected by moderation."));
            Assert.True(apply.Applied);

            var adminJson = await ReadAdministrativeInvalidationProjectionAsync(stream, TimeSpan.FromSeconds(2));
            Assert.Contains("\"isBanned\":true", adminJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"accessDenied\":true", adminJson, StringComparison.OrdinalIgnoreCase);

            await Task.Delay(250);
            Assert.Empty(server.GetActiveSessions());
            Assert.Contains(
                server.GetClosedSessions(),
                s => s.SessionId == activeSession.SessionId
                     && s.CloseReason == KcdMp.Server.Sessions.ServerSessionCloseReason.AuthenticationRejected);
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PresenceProjection_ContainsBothActiveCharacters_WhenTwoSessionsAreReady()
    {
        const int port = TestPort + 8;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_presence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var identities = new PlayerIdentityService(store, new NullServerObservabilitySink());
        var characters = new CharacterProfileService(store, identities, new NullServerObservabilitySink());

        var aliceIdentity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Alice", "token_alice", null), ServerAccessMode.Open);
        var bobIdentity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Bob", "token_bob", null), ServerAccessMode.Open);
        var aliceCharacter = await characters.CreateAsync(aliceIdentity.Identity!.InternalId, new CharacterCreateRequest("Alice", "Skalitz", "knight"));
        var bobCharacter = await characters.CreateAsync(bobIdentity.Identity!.InternalId, new CharacterCreateRequest("Bob", "Talmberg", "archer"));

        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceStore: store,
            identityService: identities,
            characterProfileService: characters,
            characterBindingOptions: new CharacterSessionBindingOptions { RequireCharacterOnConnect = true });
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcpAlice = new TcpClient();
            await tcpAlice.ConnectAsync("127.0.0.1", port);
            var streamAlice = tcpAlice.GetStream();
            await streamAlice.WriteAsync(PacketWriter.Auth(TestPassword));
            Assert.True(PacketReader.ParseAuthResult((await streamAlice.ReadPacketAsync()).Payload).ok);
            var aliceHandshake = $$"""{"displayName":"Alice","persistentToken":"token_alice","characterId":"{{aliceCharacter.Character!.InternalId}}"}""";
            await streamAlice.WriteAsync(PacketWriter.Handshake(aliceHandshake));
            Assert.Equal(PacketType.Ack, (await streamAlice.ReadPacketAsync()).Type);

            using var tcpBob = new TcpClient();
            await tcpBob.ConnectAsync("127.0.0.1", port);
            var streamBob = tcpBob.GetStream();
            await streamBob.WriteAsync(PacketWriter.Auth(TestPassword));
            Assert.True(PacketReader.ParseAuthResult((await streamBob.ReadPacketAsync()).Payload).ok);
            var bobHandshake = $$"""{"displayName":"Bob","persistentToken":"token_bob","characterId":"{{bobCharacter.Character!.InternalId}}"}""";
            await streamBob.WriteAsync(PacketWriter.Handshake(bobHandshake));
            Assert.Equal(PacketType.Ack, (await streamBob.ReadPacketAsync()).Type);

            var bobPresence = await WaitForPresenceProjectionAsync(
                streamBob,
                timeout: TimeSpan.FromSeconds(5),
                predicate: rootNode =>
                {
                    if (!rootNode.TryGetProperty("presences", out var presences) || presences.ValueKind != JsonValueKind.Array)
                        return false;

                    var characterIds = presences
                        .EnumerateArray()
                        .Where(x => x.TryGetProperty("characterId", out _))
                        .Select(x => x.GetProperty("characterId").GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet(StringComparer.Ordinal);
                    return characterIds.Contains(aliceCharacter.Character.InternalId)
                           && characterIds.Contains(bobCharacter.Character.InternalId);
                });

            Assert.True(bobPresence.TryGetProperty("revision", out var revisionNode));
            Assert.True(revisionNode.GetInt64() > 0);
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PresenceProjection_RemovesDisconnectedCharacter_FromRemainingClient()
    {
        const int port = TestPort + 9;
        var root = Path.Combine(Path.GetTempPath(), $"kcdmp_relay_presence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();

        var store = new JsonFilePersistenceStore(new JsonPersistenceOptions { BasePath = root });
        var identities = new PlayerIdentityService(store, new NullServerObservabilitySink());
        var characters = new CharacterProfileService(store, identities, new NullServerObservabilitySink());

        var aliceIdentity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Alice", "token_alice", null), ServerAccessMode.Open);
        var bobIdentity = await identities.ResolveOrCreateAsync(new PlayerIdentityClaim("Bob", "token_bob", null), ServerAccessMode.Open);
        var aliceCharacter = await characters.CreateAsync(aliceIdentity.Identity!.InternalId, new CharacterCreateRequest("Alice", "Skalitz", "knight"));
        var bobCharacter = await characters.CreateAsync(bobIdentity.Identity!.InternalId, new CharacterCreateRequest("Bob", "Talmberg", "archer"));

        var server = new KcdMp.Server.RelayServer(
            port,
            password: TestPassword,
            persistenceStore: store,
            identityService: identities,
            characterProfileService: characters,
            characterBindingOptions: new CharacterSessionBindingOptions { RequireCharacterOnConnect = true });
        var task = server.RunAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var tcpAlice = new TcpClient();
            await tcpAlice.ConnectAsync("127.0.0.1", port);
            var streamAlice = tcpAlice.GetStream();
            await streamAlice.WriteAsync(PacketWriter.Auth(TestPassword));
            Assert.True(PacketReader.ParseAuthResult((await streamAlice.ReadPacketAsync()).Payload).ok);
            var aliceHandshake = $$"""{"displayName":"Alice","persistentToken":"token_alice","characterId":"{{aliceCharacter.Character!.InternalId}}"}""";
            await streamAlice.WriteAsync(PacketWriter.Handshake(aliceHandshake));
            Assert.Equal(PacketType.Ack, (await streamAlice.ReadPacketAsync()).Type);

            var tcpBob = new TcpClient();
            await tcpBob.ConnectAsync("127.0.0.1", port);
            var streamBob = tcpBob.GetStream();
            await streamBob.WriteAsync(PacketWriter.Auth(TestPassword));
            Assert.True(PacketReader.ParseAuthResult((await streamBob.ReadPacketAsync()).Payload).ok);
            var bobHandshake = $$"""{"displayName":"Bob","persistentToken":"token_bob","characterId":"{{bobCharacter.Character!.InternalId}}"}""";
            await streamBob.WriteAsync(PacketWriter.Handshake(bobHandshake));
            Assert.Equal(PacketType.Ack, (await streamBob.ReadPacketAsync()).Type);

            await WaitForPresenceProjectionAsync(
                streamAlice,
                timeout: TimeSpan.FromSeconds(5),
                predicate: rootNode =>
                {
                    if (!rootNode.TryGetProperty("presences", out var presences) || presences.ValueKind != JsonValueKind.Array)
                        return false;

                    return presences.EnumerateArray().Any(x =>
                        x.TryGetProperty("characterId", out var charNode)
                        && charNode.GetString() == bobCharacter.Character.InternalId);
                });

            tcpBob.Dispose();

            var updated = await WaitForPresenceProjectionAsync(
                streamAlice,
                timeout: TimeSpan.FromSeconds(5),
                predicate: rootNode =>
                {
                    if (!rootNode.TryGetProperty("presences", out var presences) || presences.ValueKind != JsonValueKind.Array)
                        return false;

                    var characterIds = presences
                        .EnumerateArray()
                        .Where(x => x.TryGetProperty("characterId", out _))
                        .Select(x => x.GetProperty("characterId").GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet(StringComparer.Ordinal);
                    return characterIds.Contains(aliceCharacter.Character.InternalId)
                           && !characterIds.Contains(bobCharacter.Character.InternalId);
                });

            Assert.True(updated.TryGetProperty("reason", out var reasonNode));
            Assert.Equal("session_closed", reasonNode.GetString());
        }
        finally
        {
            cts.Cancel();
            try { await task; } catch { }
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<(TcpClient tcp, NetworkStream stream, byte id)> ConnectClientAsync(string name)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", TestPort);
        var stream = tcp.GetStream();

        await stream.WriteAsync(PacketWriter.Auth(TestPassword));
        var authResponse = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.AuthResult, authResponse.Type);

        await stream.WriteAsync(PacketWriter.Handshake(name));
        var ackPacket = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.Ack, ackPacket.Type);
        byte id = ackPacket.Payload[0];

        return (tcp, stream, id);
    }

    private async Task DrainPacketsAsync(NetworkStream stream, int count)
    {
        for (int i = 0; i < count; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await stream.ReadPacketAsync(cts.Token);
        }
    }

    private static async Task<Packet> ReadPacketByTypeAsync(NetworkStream stream, PacketType expectedType, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            var packet = await stream.ReadPacketAsync(cts.Token);
            if (packet.Type == expectedType)
                return packet;
        }

        throw new TimeoutException($"Packet type {expectedType} not received within timeout.");
    }

    private static async Task<string> ReadAdministrativeInvalidationProjectionAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            var packet = await stream.ReadPacketAsync(cts.Token);
            if (packet.Type != PacketType.StateProjection)
                continue;

            var (_, domainRaw, _, payload) = PacketReader.ParseStateProjection(packet.Payload);
            if (domainRaw != (byte)ProjectionDomain.Administrative)
                continue;

            var json = System.Text.Encoding.UTF8.GetString(payload);
            if (json.Contains("\"isBanned\":true", StringComparison.OrdinalIgnoreCase))
                return json;
        }

        throw new TimeoutException("Administrative invalidation projection not received within timeout.");
    }

    private static async Task<JsonElement> WaitForPresenceProjectionAsync(
        NetworkStream stream,
        TimeSpan timeout,
        Func<JsonElement, bool> predicate)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            var packet = await stream.ReadPacketAsync(cts.Token);
            if (packet.Type != PacketType.StateProjection)
                continue;

            var (_, domainRaw, _, payload) = PacketReader.ParseStateProjection(packet.Payload);
            if (domainRaw != (byte)ProjectionDomain.Presence)
                continue;

            using var doc = JsonDocument.Parse(payload);
            if (predicate(doc.RootElement))
                return doc.RootElement.Clone();
        }

        throw new TimeoutException("Presence projection matching predicate not received within timeout.");
    }

    private sealed class NullServerObservabilitySink : KcdMp.Server.Observability.IServerObservabilitySink
    {
        public void Emit(KcdMp.Server.Observability.ServerObservableEvent observableEvent)
        {
        }
    }
}
