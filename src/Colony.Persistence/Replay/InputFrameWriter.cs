using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Colony.Persistence;

/// <summary>
/// Builds one source's input frame in the wire form of <see cref="InputFrame"/> (spec section 7.4) without a
/// World: what a lockstep peer broadcasts for each Tick. Sequences are numbered per source and keep counting
/// across frames, so one writer serves one source for a whole session; <see cref="ToFrame"/> returns the bytes
/// and starts the next frame. Feed a frame to a World with <see cref="InputFrame.Submit(World, ReadOnlySpan{byte}, long)"/>.
/// </summary>
public sealed class InputFrameWriter
{
    private readonly WorldSchema _schema;
    private byte[] _buffer = new byte[64];
    private int _length = 4;
    private int _count;

    public InputFrameWriter(WorldSchema schema, InputSource source, uint firstSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        Source = source;
        NextSequence = firstSequence;
    }

    public InputSource Source { get; }

    /// <summary>The sequence the next added command receives.</summary>
    public uint NextSequence { get; private set; }

    /// <summary>Commands in the frame being written.</summary>
    public int Count => _count;

    /// <summary>Appends <paramref name="command"/> (an Input Command registered on the schema) with the next sequence; returns that sequence.</summary>
    public uint Add<T>(in T command) where T : unmanaged
    {
        var type = _schema.InputCommand<T>();
        return Add(type, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in command)));
    }

    /// <summary>Appends a command from its wire bytes (exactly the registered size) with the next sequence; returns that sequence.</summary>
    public uint Add(InputCommandType command, ReadOnlySpan<byte> payload)
    {
        var descriptor = _schema.DescribeInputCommand(command);
        if (payload.Length != descriptor.Size)
        {
            throw new ArgumentException($"Input Command '{descriptor.Name}' is {descriptor.Size} bytes; the payload has {payload.Length}.", nameof(payload));
        }

        uint sequence = NextSequence;
        AddEntry(Source.Value, sequence, command.Id, payload);
        NextSequence = sequence + 1;
        return sequence;
    }

    /// <summary>The frame written so far; the writer starts an empty frame, keeping the sequence count.</summary>
    public byte[] ToFrame()
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer, _count);
        var frame = _buffer.AsSpan(0, _length).ToArray();
        _length = 4;
        _count = 0;
        return frame;
    }

    /// <summary>
    /// An entry with an explicit source, sequence and Input Command id, as <see cref="InputFrameView.Record"/> reads
    /// them: for rewriting a received frame. The writer's own sequence count is not touched.
    /// </summary>
    public void AddEntry(uint source, uint sequence, int command, ReadOnlySpan<byte> payload)
    {
        int needed = _length + 16 + payload.Length;
        if (needed > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
        }

        var span = _buffer.AsSpan(_length);
        BinaryPrimitives.WriteUInt32LittleEndian(span, source);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], sequence);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], command);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], payload.Length);
        payload.CopyTo(span[16..]);
        _length = needed;
        _count++;
    }
}
