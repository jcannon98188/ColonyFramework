using System.Runtime.InteropServices;

namespace Colony.Fields;

/// <summary>
/// The typed view of one Field (spec section 5.2): tile reads and writes, whole-chunk spans for kernels,
/// and the uniform-chunk fast path. Every write through it stamps the chunk's change version with
/// <see cref="FieldSet.CurrentVersion"/>; storing the uniform value into a uniform chunk is a no-op.
/// Get one from <see cref="FieldSet.Accessor{T}"/>; it is a ref struct, so keep it in a local.
/// </summary>
public readonly ref struct FieldAccessor<T>
    where T : unmanaged
{
    private readonly FieldSet _set;
    private readonly FieldStorage _storage;
    private readonly int _field;

    internal FieldAccessor(FieldSet set, int field, FieldStorage storage)
    {
        _set = set;
        _field = field;
        _storage = storage;
    }

    public WorldSize Size => _set.Size;

    public int ChunkCount => _storage.ChunkCount;

    /// <summary>The cell at a tile; throws outside the World.</summary>
    public T this[int x, int y, int z]
    {
        get
        {
            int ordinal = _set.OrdinalOfTile(x, y, z);
            return Get(ordinal, FieldChunk.CellIndex(x & FieldChunk.Mask, y & FieldChunk.Mask, z & FieldChunk.Mask));
        }

        set
        {
            int ordinal = _set.OrdinalOfTile(x, y, z);
            Set(ordinal, FieldChunk.CellIndex(x & FieldChunk.Mask, y & FieldChunk.Mask, z & FieldChunk.Mask), value);
        }
    }

    public T this[TilePos tile]
    {
        get => this[tile.X, tile.Y, tile.Z];
        set => this[tile.X, tile.Y, tile.Z] = value;
    }

    /// <summary>The cell at a storage index of a chunk, uniform or dense.</summary>
    public T Get(int ordinal, int cellIndex)
    {
        if (_storage.IsUniform(ordinal))
        {
            return MemoryMarshal.Read<T>(_storage.UniformValue(ordinal));
        }

        return MemoryMarshal.Cast<byte, T>(_storage.ReadCells(ordinal))[cellIndex];
    }

    /// <summary>Writes one cell, expanding a uniform chunk unless the value already equals the uniform value.</summary>
    public void Set(int ordinal, int cellIndex, T value)
    {
        if (_storage.IsUniform(ordinal) && MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).SequenceEqual(_storage.UniformValue(ordinal)))
        {
            return;
        }

        WriteChunk(ordinal)[cellIndex] = value;
    }

    public bool IsUniform(int ordinal) => _storage.IsUniform(ordinal);

    /// <summary>The value of a uniform chunk; for a dense chunk, undefined (the last uniform value).</summary>
    public T UniformValue(int ordinal) => MemoryMarshal.Read<T>(_storage.UniformValue(ordinal));

    /// <summary>The cells of a dense chunk in storage order; empty when the chunk is uniform (check <see cref="IsUniform"/> first).</summary>
    public ReadOnlySpan<T> ReadChunk(int ordinal) => MemoryMarshal.Cast<byte, T>(_storage.ReadCells(ordinal));

    /// <summary>The cells of a chunk for writing (expanded if uniform); the chunk's change version advances.</summary>
    public Span<T> WriteChunk(int ordinal)
    {
        _set.MarkWritten(_field, ordinal);
        return MemoryMarshal.Cast<byte, T>(_storage.WriteCells(ordinal));
    }

    /// <summary>Sets every cell of a chunk to one value: the chunk becomes uniform and its change version advances.</summary>
    public void FillChunk(int ordinal, T value)
    {
        _set.MarkWritten(_field, ordinal);
        _storage.Fill(ordinal, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));
    }

    /// <summary>Change version of a chunk: 0 if never written, else the <see cref="FieldSet.CurrentVersion"/> of its last write.</summary>
    public uint VersionOf(int ordinal) => _set.VersionsOf(_field)[ordinal];
}
