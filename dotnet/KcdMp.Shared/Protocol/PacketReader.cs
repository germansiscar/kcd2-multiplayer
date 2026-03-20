using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Shared.Protocol;

public readonly record struct Packet(PacketType Type, byte[] Payload);

public static class PacketReader
{
    public static float ReadFloat(byte[] buf, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(offset)));

    public static long ReadInt64(byte[] buf, int offset)
        => BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset));

    public static ushort ReadUInt16(byte[] buf, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset));

    public static string ReadUtf8(byte[] buf, int offset, int length)
        => Encoding.UTF8.GetString(buf, offset, length);

    public static (byte ghostId, float x, float y, float z, float rotZ, byte flags) ParseGhost(byte[] payload)
    {
        byte ghostId = payload[0];
        float x = ReadFloat(payload, 1);
        float y = ReadFloat(payload, 5);
        float z = ReadFloat(payload, 9);
        float rotZ = ReadFloat(payload, 13);
        byte flags = payload.Length >= 18 ? payload[17] : (byte)0;
        return (ghostId, x, y, z, rotZ, flags);
    }

    public static (float x, float y, float z, float rotZ, byte flags) ParsePosition(byte[] payload)
    {
        float x = ReadFloat(payload, 0);
        float y = ReadFloat(payload, 4);
        float z = ReadFloat(payload, 8);
        float rotZ = ReadFloat(payload, 12);
        byte flags = payload.Length >= 17 ? payload[16] : (byte)0;
        return (x, y, z, rotZ, flags);
    }

    public static (byte ghostId, string name) ParseName(byte[] payload)
        => (payload[0], ReadUtf8(payload, 1, payload.Length - 1));

    public static (bool ok, string message) ParseAuthResult(byte[] payload)
        => (payload[0] != 0, payload.Length > 1 ? ReadUtf8(payload, 1, payload.Length - 1) : "");
}
