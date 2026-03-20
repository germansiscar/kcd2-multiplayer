using Xunit;
using KcdMp.Shared.Protocol;
using System.Text;

namespace KcdMp.Tests.Protocol;

public class PacketReaderTests
{
    [Fact]
    public void ParsePosition_RoundTrips()
    {
        // Arrange
        var x = 1.5f;
        var y = 2.5f;
        var z = 3.5f;
        var rotZ = 4.5f;
        var isRiding = true;

        // Act
        var packet = PacketWriter.Position(x, y, z, rotZ, isRiding);
        var payload = packet[3..];
        var (parsedX, parsedY, parsedZ, parsedRotZ, flags) = PacketReader.ParsePosition(payload);

        // Assert
        Assert.Equal(x, parsedX);
        Assert.Equal(y, parsedY);
        Assert.Equal(z, parsedZ);
        Assert.Equal(rotZ, parsedRotZ);
        Assert.Equal((byte)0x01, flags);
    }

    [Fact]
    public void ParseGhost_RoundTrips()
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
        var payload = packet[3..];
        var (parsedGhostId, parsedX, parsedY, parsedZ, parsedRotZ, parsedFlags) = PacketReader.ParseGhost(payload);

        // Assert
        Assert.Equal(ghostId, parsedGhostId);
        Assert.Equal(x, parsedX);
        Assert.Equal(y, parsedY);
        Assert.Equal(z, parsedZ);
        Assert.Equal(rotZ, parsedRotZ);
        Assert.Equal(flags, parsedFlags);
    }

    [Fact]
    public void ParseName_RoundTrips()
    {
        // Arrange
        var ghostId = (byte)3;
        var name = "TestPlayer";

        // Act
        var packet = PacketWriter.NamePacket(ghostId, name);
        var payload = packet[3..];
        var (parsedGhostId, parsedName) = PacketReader.ParseName(payload);

        // Assert
        Assert.Equal(ghostId, parsedGhostId);
        Assert.Equal(name, parsedName);
    }

    [Fact]
    public void ParseAuthResult_Ok()
    {
        // Arrange
        var ok = true;
        var message = "Authentication successful";

        // Act
        var packet = PacketWriter.AuthResult(ok, message);
        var payload = packet[3..];
        var (parsedOk, parsedMessage) = PacketReader.ParseAuthResult(payload);

        // Assert
        Assert.True(parsedOk);
        Assert.Equal(message, parsedMessage);
    }

    [Fact]
    public void ParseAuthResult_Rejected()
    {
        // Arrange
        var ok = false;
        var message = "Invalid credentials";

        // Act
        var packet = PacketWriter.AuthResult(ok, message);
        var payload = packet[3..];
        var (parsedOk, parsedMessage) = PacketReader.ParseAuthResult(payload);

        // Assert
        Assert.False(parsedOk);
        Assert.Equal(message, parsedMessage);
    }

    [Fact]
    public void ParsePosition_V1_16BytePayload_FlagsDefault()
    {
        // Arrange - simulate V1 packet with 16-byte payload (no flags byte)
        var payload = new byte[16];
        var x = 1.5f;
        var y = 2.5f;
        var z = 3.5f;
        var rotZ = 4.5f;

        PacketWriter.WriteFloat(payload, 0, x);
        PacketWriter.WriteFloat(payload, 4, y);
        PacketWriter.WriteFloat(payload, 8, z);
        PacketWriter.WriteFloat(payload, 12, rotZ);

        // Act
        var (parsedX, parsedY, parsedZ, parsedRotZ, flags) = PacketReader.ParsePosition(payload);

        // Assert
        Assert.Equal(x, parsedX);
        Assert.Equal(y, parsedY);
        Assert.Equal(z, parsedZ);
        Assert.Equal(rotZ, parsedRotZ);
        Assert.Equal((byte)0, flags);
    }
}
