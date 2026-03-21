using System.Net.Sockets;
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Integration;

[Collection("Integration")]
public class RelayServerTests : IAsyncLifetime
{
    private KcdMp.Server.RelayServer _server = null!;
    private Task _serverTask = null!;
    private CancellationTokenSource _cts = null!;
    private const int TestPort = 17778;
    private const string TestPassword = "testpass";

    public async Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        _server = new KcdMp.Server.RelayServer(TestPort, password: TestPassword);
        _serverTask = _server.RunAsync(_cts.Token);
        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _serverTask; } catch { }
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var packet = await s2.ReadPacketAsync(cts.Token);
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var packet = await s2.ReadPacketAsync(cts.Token);
        Assert.Equal(PacketType.EventRelay, packet.Type);
        var (srcId, parsedEvt, parsedJson) = PacketReader.ParseEventRelay(packet.Payload);
        Assert.Equal(id1, srcId);
        Assert.Equal(eventType, parsedEvt);
        Assert.Equal(json, parsedJson);
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
}
