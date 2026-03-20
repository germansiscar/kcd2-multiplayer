using System.Buffers.Binary;
using System.Net.Sockets;

namespace KcdMp.Shared.Protocol;

public static class StreamExtensions
{
    public static async Task ReadExactAsync(this NetworkStream stream, byte[] buffer, int count, CancellationToken ct = default)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer, offset, count - offset, ct);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    public static Task ReadExactAsync(this NetworkStream stream, byte[] buffer, CancellationToken ct = default)
        => ReadExactAsync(stream, buffer, buffer.Length, ct);

    public static async Task<Packet> ReadPacketAsync(this NetworkStream stream, CancellationToken ct = default)
    {
        var header = new byte[3];
        await stream.ReadExactAsync(header, ct);

        var type = (PacketType)header[0];
        int payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));

        var payload = new byte[payloadLen];
        if (payloadLen > 0)
            await stream.ReadExactAsync(payload, ct);

        return new Packet(type, payload);
    }
}
