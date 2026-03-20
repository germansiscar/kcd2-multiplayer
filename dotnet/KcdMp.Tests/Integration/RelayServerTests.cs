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
}
