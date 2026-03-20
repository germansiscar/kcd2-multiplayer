using Xunit;
using KcdMp.Shared.Protocol;
using System.Text;

namespace KcdMp.Tests.Protocol;

public class PacketWriterTests
{
    [Fact]
    public void Build_CreatesCorrectHeader()
    {
        // Arrange
        var payload = new byte[] { 1, 2, 3 };

        // Act
        var packet = PacketWriter.Build(PacketType.Handshake, payload);

        // Assert
        Assert.Equal((byte)PacketType.Handshake, packet[0]);
        Assert.Equal((ushort)3, PacketReader.ReadUInt16(packet, 1));
        Assert.Equal(payload, packet[3..]);
    }

    [Fact]
    public void Build_EmptyPayload()
    {
        // Arrange
        var payload = new byte[] { };

        // Act
        var packet = PacketWriter.Build(PacketType.Ack, payload);

        // Assert
        Assert.Equal(3, packet.Length);
        Assert.Equal((byte)PacketType.Ack, packet[0]);
        Assert.Equal((ushort)0, PacketReader.ReadUInt16(packet, 1));
    }

    [Fact]
    public void Position_Creates20BytePacket()
    {
        // Arrange
        var x = 1.5f;
        var y = 2.5f;
        var z = 3.5f;
        var rotZ = 4.5f;
        var isRiding = true;

        // Act
        var packet = PacketWriter.Position(x, y, z, rotZ, isRiding);

        // Assert
        Assert.Equal(20, packet.Length);
        Assert.Equal((byte)PacketType.Position, packet[0]);
        Assert.Equal((ushort)17, PacketReader.ReadUInt16(packet, 1));
        var payload = packet[3..];
        Assert.Equal(0x01, payload[16]);
    }

    [Fact]
    public void Position_NotRiding_FlagIsZero()
    {
        // Arrange
        var x = 1.5f;
        var y = 2.5f;
        var z = 3.5f;
        var rotZ = 4.5f;
        var isRiding = false;

        // Act
        var packet = PacketWriter.Position(x, y, z, rotZ, isRiding);

        // Assert
        var payload = packet[3..];
        Assert.Equal(0x00, payload[16]);
    }

    [Fact]
    public void Ghost_Creates21BytePacket()
    {
        // Arrange
        var ghostId = (byte)5;
        var x = 1.5f;
        var y = 2.5f;
        var z = 3.5f;
        var rotZ = 4.5f;
        var flags = (byte)0x42;

        // Act
        var packet = PacketWriter.Ghost(ghostId, x, y, z, rotZ, flags);

        // Assert
        Assert.Equal(21, packet.Length);
        Assert.Equal((byte)PacketType.Ghost, packet[0]);
        Assert.Equal((ushort)18, PacketReader.ReadUInt16(packet, 1));
        var payload = packet[3..];
        Assert.Equal(ghostId, payload[0]);
        Assert.Equal(flags, payload[17]);
    }

    [Fact]
    public void Handshake_EncodesNameAsUtf8()
    {
        // Arrange
        var name = "TestPlayer";
        var nameBytes = Encoding.UTF8.GetBytes(name);

        // Act
        var packet = PacketWriter.Handshake(name);

        // Assert
        Assert.Equal((byte)PacketType.Handshake, packet[0]);
        Assert.Equal((ushort)nameBytes.Length, PacketReader.ReadUInt16(packet, 1));
        var payload = packet[3..];
        Assert.Equal(nameBytes, payload);
    }

    [Fact]
    public void Auth_EncodesPasswordAsUtf8()
    {
        // Arrange
        var password = "SecretPassword";
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        // Act
        var packet = PacketWriter.Auth(password);

        // Assert
        Assert.Equal((byte)PacketType.Auth, packet[0]);
        Assert.Equal((ushort)passwordBytes.Length, PacketReader.ReadUInt16(packet, 1));
        var payload = packet[3..];
        Assert.Equal(passwordBytes, payload);
    }

    [Fact]
    public void AuthResult_Ok()
    {
        // Arrange
        var ok = true;

        // Act
        var packet = PacketWriter.AuthResult(ok);

        // Assert
        var payload = packet[3..];
        Assert.Equal(1, payload[0]);
    }

    [Fact]
    public void AuthResult_Rejected()
    {
        // Arrange
        var ok = false;

        // Act
        var packet = PacketWriter.AuthResult(ok);

        // Assert
        var payload = packet[3..];
        Assert.Equal(0, payload[0]);
    }
}
