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

    public static byte[] StateProjection(
        uint projectionId,
        byte domain,
        byte applicability,
        byte[] jsonPayload)
    {
        var buf = new byte[6 + jsonPayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), projectionId);
        buf[4] = domain;
        buf[5] = applicability;
        Buffer.BlockCopy(jsonPayload, 0, buf, 6, jsonPayload.Length);
        return Build(PacketType.StateProjection, buf);
    }

    public static byte[] StateProjectionResult(
        uint projectionId,
        byte domain,
        byte status,
        byte[] detailsPayload)
    {
        var buf = new byte[6 + detailsPayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), projectionId);
        buf[4] = domain;
        buf[5] = status;
        Buffer.BlockCopy(detailsPayload, 0, buf, 6, detailsPayload.Length);
        return Build(PacketType.StateProjectionResult, buf);
    }

    public static void WriteFloat(byte[] buf, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
}
