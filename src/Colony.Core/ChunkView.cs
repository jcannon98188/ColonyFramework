using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// A safe view of one chunk for chunk-level iteration (spec section 4.1): each column as a
/// <see cref="Span{T}"/> over the live rows, or padded to a multiple of 16 for SIMD. Every column starts
/// on a 64-byte boundary. The view is valid until the next structural change; with debug validation on,
/// every access checks the chunk's structural version and throws when the view is stale.
/// </summary>
public readonly ref struct ChunkView
{
    private readonly Archetype _archetype;
    private readonly nint _pointer;
    private readonly uint _version;
    private readonly bool _validate;
    private readonly bool _checkAccess;
    private readonly ComponentMask _access;
    private readonly string? _systemName;

    internal ChunkView(Archetype archetype, int chunkIndex, bool validate)
    {
        _archetype = archetype;
        ChunkIndex = chunkIndex;
        _pointer = PointerOf(archetype, chunkIndex);
        Count = archetype.RowCount(chunkIndex);
        _version = archetype.Version(chunkIndex);
        _validate = validate;
    }

    /// <summary>A view for a System job that also checks every column taken against the System's declared access set (spec section 10.1, validation switches).</summary>
    internal ChunkView(Archetype archetype, int chunkIndex, bool validate, ComponentMask access, string systemName)
        : this(archetype, chunkIndex, validate)
    {
        _checkAccess = true;
        _access = access;
        _systemName = systemName;
    }

    /// <summary>Index of the chunk inside its archetype.</summary>
    public int ChunkIndex { get; }

    /// <summary>Live rows in the chunk.</summary>
    public int Count { get; }

    /// <summary>Rows the chunk can hold; a multiple of 16 fixed by the layout.</summary>
    public int Capacity => _archetype.Capacity;

    /// <summary><see cref="Count"/> rounded up to a multiple of 16: the length of <see cref="PaddedColumn{T}"/>.</summary>
    public int PaddedCount => (Count + Archetype.ElementPadding - 1) & ~(Archetype.ElementPadding - 1);

    public ComponentMask Signature => _archetype.Signature;

    public bool Has(ComponentType type) => _archetype.Has(type.Id);

    /// <summary>The Entity id of every live row.</summary>
    public ReadOnlySpan<EntityId> Entities
    {
        get
        {
            Validate();
            return Span<EntityId>(0, Count);
        }
    }

    /// <summary>The Component column over the live rows.</summary>
    public Span<T> Column<T>(ComponentType type) where T : unmanaged
    {
        Validate();
        return Span<T>(Offset<T>(type), Count);
    }

    /// <summary>
    /// The Component column padded to a multiple of 16 rows so SIMD loops need no scalar tail. Rows past
    /// <see cref="Count"/> hold unspecified bytes: process them, never read them back as state.
    /// </summary>
    public Span<T> PaddedColumn<T>(ComponentType type) where T : unmanaged
    {
        Validate();
        return Span<T>(Offset<T>(type), PaddedCount);
    }

    // ---- Enable bits (spec section 4.1): per Component, per row. Spans above cover every live row; use these to honour the bits. ----

    /// <summary>True when the Component is enabled on <paramref name="row"/>.</summary>
    public bool IsEnabled(ComponentType type, int row)
    {
        Validate();
        return _archetype.IsEnabled(ChunkIndex, Column(type), (uint)row < (uint)Count ? row : throw new ArgumentOutOfRangeException(nameof(row)));
    }

    /// <summary>
    /// Flips the Component's enable bit on <paramref name="row"/>. Not a structural change. From a chunk
    /// job, flip bits only in your own chunk and only for Components in your declared Writes.
    /// </summary>
    public void SetEnabled(ComponentType type, int row, bool enabled)
    {
        Validate();
        if ((uint)row >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        _archetype.SetEnabled(ChunkIndex, Column(type), row, enabled);
    }

    /// <summary>Live rows on which the Component is enabled.</summary>
    public int EnabledCount(ComponentType type)
    {
        Validate();
        return _archetype.EnabledCount(ChunkIndex, Column(type));
    }

    /// <summary>The Component's enable bitset (bit r = row r), covering the live rows; bits past <see cref="Count"/> are zero.</summary>
    public unsafe ReadOnlySpan<ulong> EnabledBits(ComponentType type)
    {
        Validate();
        return new ReadOnlySpan<ulong>(EnabledPointer(Column(type)), Archetype.EnabledWords(Count));
    }

    /// <summary>Enumerates (ascending) the rows on which <paramref name="type"/> is enabled.</summary>
    public EnabledRowEnumerator EnabledRows(ComponentType type) => EnabledRows(ComponentMask.Of(type));

    /// <summary>Enumerates (ascending) the rows on which every Component in <paramref name="components"/> that this chunk has is enabled.</summary>
    public EnabledRowEnumerator EnabledRows(ComponentMask components)
    {
        Validate();
        return new EnabledRowEnumerator(_archetype, _pointer, components, Count);
    }

    private int Column(ComponentType type)
    {
        int column = _archetype.ColumnOf(type.Id);
        return column >= 0 ? column : throw new InvalidOperationException($"The chunk has no column for {type}.");
    }

    private unsafe void* EnabledPointer(int column) => (byte*)_pointer + _archetype.EnabledOffsets[column];

    /// <summary>
    /// The chunk's asleep bit: Systems with <see cref="SystemDefinition.SkipAsleepChunks"/> skip asleep chunks.
    /// A structural change in the chunk wakes it. Set it only from a job that owns this chunk, or outside a Tick.
    /// </summary>
    public bool IsAsleep => _archetype.IsAsleep(ChunkIndex);

    public void SetAsleep(bool asleep) => _archetype.SetAsleep(ChunkIndex, asleep);

    /// <summary>True while the chunk has had no structural change since the view was taken.</summary>
    public bool IsCurrent => ChunkIndex < _archetype.ChunkCount && _archetype.Version(ChunkIndex) == _version;

    private int Offset<T>(ComponentType type) where T : unmanaged
    {
        if (_checkAccess && !_access.Contains(type))
        {
            throw new InvalidOperationException($"System '{_systemName}' takes the column of {type}, which it declares in neither Reads nor Writes.");
        }

        int offset = _archetype.OffsetOf(type.Id);
        if (offset < 0)
        {
            throw new InvalidOperationException($"The chunk has no column for {type}.");
        }

        if (Unsafe.SizeOf<T>() != _archetype.Sizes[Array.IndexOf(_archetype.ComponentIds, type.Id)])
        {
            throw new ArgumentException($"{typeof(T).Name} does not match the registered size of {type}.", nameof(T));
        }

        return offset;
    }

    private void Validate()
    {
        if (_validate && !IsCurrent)
        {
            throw new InvalidOperationException("The chunk view is stale: a structural change happened after it was taken.");
        }
    }

    private unsafe Span<T> Span<T>(int offset, int length) where T : unmanaged => new((byte*)_pointer + offset, length);

    private static unsafe nint PointerOf(Archetype archetype, int chunkIndex) => (nint)archetype.ChunkPointer(chunkIndex);
}
