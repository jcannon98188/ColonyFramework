using System.Buffers.Binary;

namespace Colony.Testing;

/// <summary>
/// The reference peers' packet (little-endian): magic <c>LKS1</c>, sender, the sender's cumulative acks for
/// the recipient (next frame Tick and next checkpoint Tick it lacks), then frames (<c>long tick, int length,
/// wire frame</c>) and checkpoint hashes (<c>long tick, ulong hash</c>). Every packet is idempotent, so drops are
/// repaired by resending everything not yet acknowledged and duplicates or late copies are harmless.
/// </summary>
internal static class LockstepPacket
{
    public const uint Magic = 0x3153_4B4C; // "LKS1"

    public readonly record struct Frame(long Tick, byte[] Bytes);

    public readonly record struct Checkpoint(long Tick, ulong Hash);

    public sealed class Decoded
    {
        public int Sender { get; set; }

        public long FrameAck { get; set; }

        public long HashAck { get; set; }

        public List<Frame> Frames { get; } = [];

        public List<Checkpoint> Hashes { get; } = [];
    }

    public sealed class Writer
    {
        private byte[] _buffer = new byte[256];
        private int _length;
        private int _frameCountAt;
        private int _frameCount;
        private int _hashCountAt;
        private int _hashCount;

        public void Begin(int sender, long frameAck, long hashAck)
        {
            _length = 0;
            _frameCount = 0;
            _hashCount = 0;
            _hashCountAt = -1;
            WriteUInt32(Magic);
            WriteInt32(sender);
            WriteInt64(frameAck);
            WriteInt64(hashAck);
            _frameCountAt = _length;
            WriteInt32(0);
        }

        public void AddFrame(long tick, ReadOnlySpan<byte> frame)
        {
            WriteInt64(tick);
            WriteInt32(frame.Length);
            Ensure(frame.Length);
            frame.CopyTo(_buffer.AsSpan(_length));
            _length += frame.Length;
            _frameCount++;
        }

        public void BeginHashes()
        {
            _hashCountAt = _length;
            WriteInt32(0);
        }

        public void AddHash(long tick, ulong hash)
        {
            WriteInt64(tick);
            Ensure(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), hash);
            _length += 8;
            _hashCount++;
        }

        public ReadOnlySpan<byte> End()
        {
            if (_hashCountAt < 0)
            {
                BeginHashes();
            }

            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_frameCountAt), _frameCount);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_hashCountAt), _hashCount);
            return _buffer.AsSpan(0, _length);
        }

        private void WriteUInt32(uint value)
        {
            Ensure(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
            _length += 4;
        }

        private void WriteInt32(int value)
        {
            Ensure(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
            _length += 4;
        }

        private void WriteInt64(long value)
        {
            Ensure(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length), value);
            _length += 8;
        }

        private void Ensure(int bytes)
        {
            if (_length + bytes > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_length + bytes, _buffer.Length * 2));
            }
        }
    }

    /// <summary>Parses <paramref name="packet"/> into <paramref name="into"/>; a malformed packet throws <see cref="FormatException"/>.</summary>
    public static void Decode(ReadOnlySpan<byte> packet, Decoded into)
    {
        into.Frames.Clear();
        into.Hashes.Clear();
        int at = 0;
        if (ReadUInt32(packet, ref at) != Magic)
        {
            throw new FormatException("Not a lockstep packet.");
        }

        into.Sender = ReadInt32(packet, ref at);
        into.FrameAck = ReadInt64(packet, ref at);
        into.HashAck = ReadInt64(packet, ref at);
        int frames = ReadInt32(packet, ref at);
        for (int i = 0; i < frames; i++)
        {
            long tick = ReadInt64(packet, ref at);
            int length = ReadInt32(packet, ref at);
            if (length < 0 || at + length > packet.Length)
            {
                throw new FormatException("Truncated lockstep packet.");
            }

            into.Frames.Add(new Frame(tick, packet.Slice(at, length).ToArray()));
            at += length;
        }

        int hashes = ReadInt32(packet, ref at);
        for (int i = 0; i < hashes; i++)
        {
            long tick = ReadInt64(packet, ref at);
            into.Hashes.Add(new Checkpoint(tick, (ulong)ReadInt64(packet, ref at)));
        }

        if (at != packet.Length)
        {
            throw new FormatException("Trailing bytes after a lockstep packet.");
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, ref int at) => (uint)ReadInt32(span, ref at);

    private static int ReadInt32(ReadOnlySpan<byte> span, ref int at)
    {
        if (at + 4 > span.Length)
        {
            throw new FormatException("Truncated lockstep packet.");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
        at += 4;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> span, ref int at)
    {
        if (at + 8 > span.Length)
        {
            throw new FormatException("Truncated lockstep packet.");
        }

        long value = BinaryPrimitives.ReadInt64LittleEndian(span[at..]);
        at += 8;
        return value;
    }
}
