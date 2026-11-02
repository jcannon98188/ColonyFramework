using System.Buffers.Binary;

namespace Colony.Persistence;

/// <summary>
/// The wire form of one Tick's canonical input list (spec sections 7.2 and 7.4): little-endian, a 32-bit
/// count, then per command the source, its sequence, the Input Command id and the payload (32-bit length,
/// then exactly the registered size). It is what the replay log stores per non-empty Tick and what lockstep
/// peers exchange with the Tick number; <see cref="Submit(World, ReadOnlySpan{byte}, long)"/> feeds a frame
/// back into a World. The Tick itself travels beside the frame.
/// </summary>
public static class InputFrame
{
    /// <summary>Encodes the current canonical list of <paramref name="world"/> (the Tick that last started).</summary>
    public static byte[] Encode(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Encode(world.Inputs.Current);
    }

    public static byte[] Encode(in CanonicalInputs list)
    {
        var w = new ByteWriter(16 + (list.Count * 32));
        Write(w, in list);
        return w.ToArray();
    }

    internal static void Write(ByteWriter w, in CanonicalInputs list)
    {
        w.WriteInt32(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var record = list[i];
            var payload = list.Payload(i);
            w.WriteUInt32(record.Source.Value);
            w.WriteUInt32(record.Sequence);
            w.WriteInt32(record.Command.Id);
            w.WriteInt32(payload.Length);
            w.WriteBytes(payload);
        }
    }

    /// <summary>
    /// Submits every command of <paramref name="frame"/> to <paramref name="world"/> for <paramref name="effectiveTick"/>,
    /// Input Command ids taken as the World schema's own. Returns the number of commands submitted.
    /// </summary>
    public static int Submit(World world, ReadOnlySpan<byte> frame, long effectiveTick)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Submit(world, frame, effectiveTick, ReadOnlySpan<int>.Empty);
    }

    /// <summary>Submits a frame whose Input Command ids are remapped through <paramref name="typeMap"/> (saved id to schema id); empty means identity.</summary>
    internal static int Submit(World world, ReadOnlySpan<byte> frame, long effectiveTick, ReadOnlySpan<int> typeMap)
        => Submit(world, frame.ToArray(), effectiveTick, typeMap);

    /// <summary>Submits a frame already held as an array (no copy).</summary>
    internal static int Submit(World world, byte[] frame, long effectiveTick, ReadOnlySpan<int> typeMap)
    {
        var view = new InputFrameView(frame);
        for (int i = 0; i < view.Count; i++)
        {
            var (source, sequence, type) = view.Record(i);
            if (!typeMap.IsEmpty)
            {
                if ((uint)type >= (uint)typeMap.Length)
                {
                    throw new SaveFormatException($"The input frame names Input Command {type}, which the replay header does not list.");
                }

                type = typeMap[type];
            }

            if ((uint)type >= (uint)world.Schema.InputCommandCount)
            {
                throw new SaveFormatException($"The input frame names Input Command {type}, which the schema does not register.");
            }

            world.Inputs.Submit(new InputSource(source), sequence, new InputCommandType(type), view.Payload(i), effectiveTick);
        }

        return view.Count;
    }
}

/// <summary>A decoded input frame: its records and payloads by index, read without a World.</summary>
public readonly struct InputFrameView : IEquatable<InputFrameView>
{
    private readonly byte[] _frame;
    private readonly int[] _offsets;

    /// <summary>Parses and bounds-checks <paramref name="frame"/>; a malformed frame throws <see cref="SaveFormatException"/>.</summary>
    public InputFrameView(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
        var r = new ByteReader(frame, "input frame");
        int count = r.ReadCount(16);
        _offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            _offsets[i] = r.Position;
            r.ReadUInt32();
            r.ReadUInt32();
            r.ReadInt32();
            int length = r.ReadInt32();
            r.ReadBytes(length);
        }

        r.ExpectEnd();
    }

    public int Count => _offsets.Length;

    /// <summary>Source, sequence and Input Command id (as recorded) of entry <paramref name="index"/>.</summary>
    public (uint Source, uint Sequence, int Command) Record(int index)
    {
        var span = _frame.AsSpan(_offsets[index]);
        return (BinaryPrimitives.ReadUInt32LittleEndian(span), BinaryPrimitives.ReadUInt32LittleEndian(span[4..]), BinaryPrimitives.ReadInt32LittleEndian(span[8..]));
    }

    public ReadOnlySpan<byte> Payload(int index)
    {
        var span = _frame.AsSpan(_offsets[index]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        return span.Slice(16, length);
    }

    /// <summary>The frame bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _frame;

    public bool Equals(InputFrameView other) => _frame.AsSpan().SequenceEqual(other._frame);

    public override bool Equals(object? obj) => obj is InputFrameView other && Equals(other);

    public override int GetHashCode() => (int)Hashing.Hash64.Compute(_frame, 0);

    public static bool operator ==(InputFrameView left, InputFrameView right) => left.Equals(right);

    public static bool operator !=(InputFrameView left, InputFrameView right) => !left.Equals(right);
}
