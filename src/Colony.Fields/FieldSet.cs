using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// A World's Fields (spec section 5.1): the chunk grid, every Field's storage, per-chunk per-Field change
/// versions and the change feed. Created once per World by <see cref="FieldsBuilder"/>; fetch it with
/// <see cref="WorldFieldsExtensions.Fields"/>. Fields are CPU-authoritative in v1 and all chunks are
/// resident (the "never unload" policy); residency transitions arrive with the streaming tickets.
/// </summary>
public sealed class FieldSet : IWorldExtension
{
    private readonly ChunkLayout _layout;
    private readonly FieldDescriptor[] _fields;
    private readonly Type[] _cellTypes;
    private readonly FieldStorage[] _storage;
    private readonly uint[][] _versions;
    private bool _disposed;

    internal FieldSet(World world, ChunkLayout layout, List<FieldDescriptor> fields, List<Type> cellTypes, List<byte[]> initialValues)
    {
        World = world;
        _layout = layout;
        _fields = [.. fields];
        _cellTypes = [.. cellTypes];
        _storage = new FieldStorage[_fields.Length];
        _versions = new uint[_fields.Length][];
        for (int i = 0; i < _fields.Length; i++)
        {
            _storage[i] = new CpuFieldStorage(_fields[i].CellSize, layout.ChunkCount, initialValues[i]);
            _versions[i] = new uint[layout.ChunkCount];
        }
    }

    public World World { get; }

    public WorldSize Size => _layout.Size;

    /// <summary>Chunks per Field; ordinals <c>0..ChunkCount-1</c> enumerate them in Morton order.</summary>
    public int ChunkCount => _layout.ChunkCount;

    public int FieldCount => _fields.Length;

    public ReadOnlySpan<FieldDescriptor> Fields => _fields;

    /// <summary>Dense cell bytes allocated over all Fields; uniform chunks hold no cell storage.</summary>
    public long CellBytesAllocated
    {
        get
        {
            long total = 0;
            foreach (var storage in _storage)
            {
                total += storage.CellBytesAllocated;
            }

            return total;
        }
    }

    /// <summary>The chunk coordinate of an ordinal (Morton rank).</summary>
    public ChunkCoord ChunkAt(int ordinal) => _layout.CoordOf(ordinal);

    /// <summary>The ordinal (Morton rank) of a chunk coordinate.</summary>
    public int OrdinalOf(ChunkCoord coord) => _layout.OrdinalOf(coord);

    /// <summary>The ordinal of the chunk containing a tile; throws outside the World.</summary>
    public int OrdinalOfTile(int x, int y, int z)
    {
        if (!Size.Contains(x, y, z))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Tile ({x}, {y}, {z}) lies outside the {Size} World.");
        }

        return _layout.OrdinalOf(ChunkCoord.OfTile(x, y, z));
    }

    /// <summary>Residency of a chunk (spec section 5.3). Every chunk is Active in v1; the seam exists for the streaming tickets.</summary>
    public ChunkResidency ResidencyOf(int ordinal)
    {
        _ = _layout.CoordOf(ordinal);
        return ChunkResidency.Active;
    }

    public FieldDescriptor Describe<T>(Field<T> field)
        where T : unmanaged
        => _fields[Validate(field)];

    public Field<T> Field<T>(string name)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < _fields.Length; i++)
        {
            if (string.Equals(_fields[i].Name, name, StringComparison.Ordinal))
            {
                if (_cellTypes[i] != typeof(T))
                {
                    throw new ArgumentException($"Field '{name}' has cells of type {_cellTypes[i].Name}, not {typeof(T).Name}.", nameof(name));
                }

                return new Field<T>(i);
            }
        }

        throw new ArgumentException($"No Field named '{name}' is registered in this World.", nameof(name));
    }

    /// <summary>The typed view through which Systems read and write a Field.</summary>
    public FieldAccessor<T> Accessor<T>(Field<T> field)
        where T : unmanaged
    {
        int id = Validate(field);
        return new FieldAccessor<T>(this, id, _storage[id]);
    }

    /// <summary>The storage behind a Field (the GPU seam); for tooling and tests, not for Systems.</summary>
    public FieldStorage StorageOf<T>(Field<T> field)
        where T : unmanaged
        => _storage[Validate(field)];

    internal ChunkLayout Layout => _layout;

    /// <summary>The storage behind the Field with id <paramref name="fieldId"/> (see <see cref="Fields"/>), for tooling that knows Fields by name only, such as the inspection server.</summary>
    public FieldStorage StorageAt(int fieldId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldId, _storage.Length);
        return _storage[fieldId];
    }

    private readonly List<FieldStorage> _scratch = [];

    /// <summary>A second storage shaped like <paramref name="field"/>'s (a stencil back buffer); owned and disposed by this set, never hashed.</summary>
    internal FieldStorage CreateScratchStorage<T>(Field<T> field)
        where T : unmanaged
    {
        int id = Validate(field);
        var storage = new CpuFieldStorage(_fields[id].CellSize, _layout.ChunkCount, _storage[id].UniformValue(0));
        _scratch.Add(storage);
        return storage;
    }

    /// <summary>The version a write made now gets: the Tick in progress plus one, so 0 means "never written".</summary>
    public uint CurrentVersion => (uint)(World.CurrentTick + 1);

    /// <summary>Change version of one chunk of one Field: the <see cref="CurrentVersion"/> at its last write, 0 if never written.</summary>
    public uint VersionOf<T>(Field<T> field, int ordinal)
        where T : unmanaged
        => _versions[Validate(field)][ordinal];

    internal void MarkWritten(int fieldId, int ordinal) => _versions[fieldId][ordinal] = CurrentVersion;

    internal uint[] VersionsOf(int fieldId) => _versions[fieldId];

    private int Validate<T>(Field<T> field)
        where T : unmanaged
    {
        if ((uint)field.Id >= (uint)_fields.Length || _cellTypes[field.Id] != typeof(T))
        {
            throw new ArgumentException($"{field} does not belong to this World.", nameof(field));
        }

        return field.Id;
    }

    void IWorldExtension.BeginTick(long tick)
    {
    }

    void IWorldExtension.EndPhase(Phase phase)
    {
    }

    void IWorldExtension.EndTick(long tick) => CollectTickChanges();

    /// <summary>
    /// The World change feed (spec section 5.1): every chunk of every Field written during the last
    /// completed Tick, by Field id then Morton order. Rebuilt at the end of each Tick; valid until the next
    /// Tick ends. Region upkeep, the work index and renderers subscribe here.
    /// </summary>
    public ReadOnlySpan<FieldChange> LastTickChanges => _lastTick.AsSpan(0, _lastTickCount);

    /// <summary>
    /// Chunks whose change version is above <paramref name="version"/>, by Field id then Morton order, as
    /// of now (including writes made so far in the running Tick). Pass the <see cref="CurrentVersion"/>
    /// you saw when you last consumed the feed.
    /// </summary>
    public FieldChangeFeed ChangedSince(uint version) => new(this, version);

    private FieldChange[] _lastTick = new FieldChange[64];
    private int _lastTickCount;

    private void CollectTickChanges()
    {
        uint version = CurrentVersion;
        _lastTickCount = 0;
        for (int f = 0; f < _versions.Length; f++)
        {
            var versions = _versions[f];
            for (int ordinal = 0; ordinal < versions.Length; ordinal++)
            {
                if (versions[ordinal] != version)
                {
                    continue;
                }

                if (_lastTickCount == _lastTick.Length)
                {
                    Array.Resize(ref _lastTick, _lastTick.Length * 2);
                }

                _lastTick[_lastTickCount++] = new FieldChange(f, ordinal, version);
            }
        }
    }

    /// <summary>Every Field in id order, every chunk in Morton order, cells in storage order (spec section 6). Versions are excluded.</summary>
    void IWorldExtension.Hash(ref Hash64State state)
    {
        for (int f = 0; f < _storage.Length; f++)
        {
            var storage = _storage[f];
            for (int ordinal = 0; ordinal < storage.ChunkCount; ordinal++)
            {
                storage.HashChunk(ordinal, ref state);
            }
        }
    }

    /// <summary>
    /// One section per Field chunk (spec section 7.1), named <c>&lt;field&gt;/&lt;ordinal&gt;</c>: a kind byte (0 uniform,
    /// 1 dense) then the uniform value or the cells in storage order. Versions and the change feed are not saved.
    /// </summary>
    void IWorldExtension.Save(ExtensionStateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        byte[]? buffer = null;
        for (int f = 0; f < _storage.Length; f++)
        {
            var storage = _storage[f];
            int chunkBytes = storage.CellSize * FieldChunk.CellCount;
            if (buffer is null || buffer.Length < chunkBytes + 1)
            {
                buffer = new byte[chunkBytes + 1];
            }

            for (int ordinal = 0; ordinal < storage.ChunkCount; ordinal++)
            {
                string name = SectionName(f, ordinal);
                if (storage.IsUniform(ordinal))
                {
                    buffer[0] = 0;
                    storage.UniformValue(ordinal).CopyTo(buffer.AsSpan(1));
                    writer.WriteSection(name, buffer.AsSpan(0, 1 + storage.CellSize));
                }
                else
                {
                    buffer[0] = 1;
                    storage.ReadCells(ordinal).CopyTo(buffer.AsSpan(1));
                    writer.WriteSection(name, buffer.AsSpan(0, 1 + chunkBytes));
                }
            }
        }
    }

    void IWorldExtension.Load(ExtensionStateReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        for (int f = 0; f < _storage.Length; f++)
        {
            var storage = _storage[f];
            int chunkBytes = storage.CellSize * FieldChunk.CellCount;
            for (int ordinal = 0; ordinal < storage.ChunkCount; ordinal++)
            {
                string name = SectionName(f, ordinal);
                var payload = reader.Section(name);
                if (payload.Length == 1 + storage.CellSize && payload[0] == 0)
                {
                    storage.Fill(ordinal, payload[1..]);
                }
                else if (payload.Length == 1 + chunkBytes && payload[0] == 1)
                {
                    payload[1..].CopyTo(storage.WriteCells(ordinal));
                }
                else
                {
                    throw new InvalidDataException($"Field '{_fields[f].Name}' chunk {ordinal}: section '{name}' holds {payload.Length} bytes, not a uniform value ({1 + storage.CellSize}) or a dense chunk ({1 + chunkBytes}).");
                }

                _versions[f][ordinal] = 0;
            }
        }
    }

    private string SectionName(int field, int ordinal) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_fields[field].Name}/{ordinal}");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var storage in _storage)
        {
            storage.Dispose();
        }

        foreach (var storage in _scratch)
        {
            storage.Dispose();
        }
    }
}

/// <summary>Residency state of a chunk (spec section 5.3). Only <see cref="Active"/> occurs until the streaming tickets land.</summary>
public enum ChunkResidency
{
    /// <summary>Resident and simulated.</summary>
    Active,

    /// <summary>Resident, not simulated.</summary>
    Hibernating,

    /// <summary>On disk or not yet generated.</summary>
    Unloaded,
}
