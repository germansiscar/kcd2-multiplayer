using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Shared.Protocol;

public static class PacketWriter
{
    public static byte[] Build(PacketType type, byte[] payload)
    {
        var packet = new byte[3 + payload.Length];
        packet[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(packet, 3);
        return packet;
    }

    public static byte[] Position(float x, float y, float z, float rotZ, bool isRiding)
    {
        var payload = new byte[17];
        WriteFloat(payload, 0, x);
        WriteFloat(payload, 4, y);
        WriteFloat(payload, 8, z);
        WriteFloat(payload, 12, rotZ);
        payload[16] = isRiding ? (byte)0x01 : (byte)0x00;
        return Build(PacketType.Position, payload);
    }

    public static byte[] Ghost(byte ghostId, float x, float y, float z, float rotZ, byte flags)
    {
        var payload = new byte[18];
        payload[0] = ghostId;
        WriteFloat(payload, 1, x);
        WriteFloat(payload, 5, y);
        WriteFloat(payload, 9, z);
        WriteFloat(payload, 13, rotZ);
        payload[17] = flags;
        return Build(PacketType.Ghost, payload);
    }

    public static byte[] Handshake(string name)
        => Build(PacketType.Handshake, Encoding.UTF8.GetBytes(name));

    public static byte[] Ack(byte id)
        => Build(PacketType.Ack, [id]);

    public static byte[] NamePacket(byte ghostId, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var payload = new byte[1 + nameBytes.Length];
        payload[0] = ghostId;
        nameBytes.CopyTo(payload, 1);
        return Build(PacketType.Name, payload);
    }

    public static byte[] Ping(long timestamp)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, timestamp);
        return Build(PacketType.Ping, payload);
    }

    public static byte[] Pong(byte[] timestampBytes)
        => Build(PacketType.Pong, timestampBytes);

    public static byte[] DisconnectPacket(byte ghostId)
        => Build(PacketType.Disconnect, [ghostId]);

    public static byte[] Auth(string password)
        => Build(PacketType.Auth, Encoding.UTF8.GetBytes(password));

    public static byte[] AuthResult(bool ok, string message = "")
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + msgBytes.Length];
        payload[0] = ok ? (byte)1 : (byte)0;
        msgBytes.CopyTo(payload, 1);
        return Build(PacketType.AuthResult, payload);
    }

    public static void WriteFloat(byte[] buf, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
}
