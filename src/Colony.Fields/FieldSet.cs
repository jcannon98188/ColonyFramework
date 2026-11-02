using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// A World's Fields (spec section 5.1): the chunk grid, every Field's storage, per-chunk per-Field change
/// versions and the change feed. Created once per World by <see cref="FieldsBuilder"/>; fetch it with
/// <see cref="WorldFieldsExtensions.Fields"/>. Fields are CPU-authoritative in v1. Every chunk is Active until a
/// <see cref="ChunkResidencyManager"/> (spec section 5.3) moves it: an Unloaded chunk's cells leave the storage and
/// live on as exactly their save section (<see cref="UnloadCells"/>), or as nothing at all when the chunk can be
/// regenerated unchanged from (seed, coordinate).
/// </summary>
public sealed class FieldSet : IWorldExtension
{
    /// <summary>Name of the residency section (one byte per chunk); written only when some chunk is not Active, so saves without streaming are unchanged.</summary>
    internal const string ResidencySection = "residency";

    private readonly ChunkLayout _layout;
    private readonly FieldDescriptor[] _fields;
    private readonly Type[] _cellTypes;
    private readonly FieldStorage[] _storage;
    private readonly byte[][] _initial;
    private readonly byte[]?[] _dataMasks;
    private readonly uint[][] _versions;
    private readonly ChunkResidency[] _residency;
    private readonly byte[]?[][] _stored;
    private bool _disposed;

    internal FieldSet(World world, ChunkLayout layout, List<FieldDescriptor> fields, List<Type> cellTypes, List<byte[]> initialValues, List<byte[]?> dataMasks)
    {
        World = world;
        _layout = layout;
        _fields = [.. fields];
        _cellTypes = [.. cellTypes];
        _initial = [.. initialValues];
        _dataMasks = [.. dataMasks];
        _storage = new FieldStorage[_fields.Length];
        _versions = new uint[_fields.Length][];
        _stored = new byte[]?[_fields.Length][];
        _residency = new ChunkResidency[layout.ChunkCount];
        for (int i = 0; i < _fields.Length; i++)
        {
            _storage[i] = new CpuFieldStorage(_fields[i].CellSize, layout.ChunkCount, initialValues[i], _dataMasks[i]);
            _versions[i] = new uint[layout.ChunkCount];
            _stored[i] = new byte[]?[layout.ChunkCount];
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

    /// <summary>Residency of a chunk (spec section 5.3): Active unless a <see cref="ChunkResidencyManager"/> moved it.</summary>
    public ChunkResidency ResidencyOf(int ordinal) => _residency[ordinal];

    /// <summary>True when an Unloaded chunk's cells are kept as their save section (false: resident, or regenerable unchanged).</summary>
    public bool HasStoredCells(int ordinal) => _fields.Length > 0 && _stored[0][ordinal] is not null;

    /// <summary>The residency manager of this World, when the schema registered one (<see cref="ResidencyRegistration.AddResidency"/>).</summary>
    public ChunkResidencyManager? Residency { get; internal set; }

    /// <summary>
    /// Where the residency manager gets chunks nobody holds bytes for (spec section 5.3): set by ProcGen's generation
    /// extension when its World is created, or by the host before the first Tick. Null means every unloaded chunk is
    /// kept as its save section and a never-loaded chunk holds its Fields' initial values.
    /// </summary>
    public IChunkSource? ChunkSource { get; set; }

    internal void SetResidency(int ordinal, ChunkResidency state) => _residency[ordinal] = state;

    /// <summary>
    /// Takes an Unloaded chunk's cells out of the storage (spec section 5.3): with <paramref name="store"/> each
    /// Field's chunk is kept as exactly its save section payload; without, nothing is kept (the manager will
    /// regenerate it). The storage goes back to the initial uniform value and frees the dense block. The caller
    /// marks the chunk Unloaded afterwards.
    /// </summary>
    internal void UnloadCells(int ordinal, bool store)
    {
        for (int f = 0; f < _storage.Length; f++)
        {
            _stored[f][ordinal] = store ? EncodeSection(_storage[f], ordinal) : null;
            _storage[f].Release(ordinal, _initial[f]);
        }
    }

    /// <summary>Puts stored cells back (the chunk must already be resident); false when nothing was stored for the chunk.</summary>
    internal bool ReloadStoredCells(int ordinal)
    {
        if (!HasStoredCells(ordinal))
        {
            return false;
        }

        for (int f = 0; f < _storage.Length; f++)
        {
            DecodeSection(f, ordinal, _stored[f][ordinal]!, SectionName(f, ordinal));
            _stored[f][ordinal] = null;
            MarkWritten(f, ordinal);
        }

        return true;
    }

    /// <summary>The save-section encoding of one chunk: a kind byte (0 uniform, 1 dense) then the uniform value or the cells in storage order.</summary>
    private static byte[] EncodeSection(FieldStorage storage, int ordinal)
    {
        if (storage.IsUniform(ordinal))
        {
            var payload = new byte[1 + storage.CellSize];
            storage.UniformValue(ordinal).CopyTo(payload.AsSpan(1));
            return payload;
        }

        var cells = storage.ReadCells(ordinal);
        var dense = new byte[1 + cells.Length];
        dense[0] = 1;
        cells.CopyTo(dense.AsSpan(1));
        ValueLayout.ZeroPadding(storage.CellDataMask, dense.AsSpan(1)); // saves never carry padding (spec section 6)
        return dense;
    }

    private void DecodeSection(int f, int ordinal, ReadOnlySpan<byte> payload, string name)
    {
        var storage = _storage[f];
        int chunkBytes = storage.CellSize * FieldChunk.CellCount;
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
    }

    private byte[]? _expandScratch;

    /// <summary>Hashes a stored section exactly as the dense chunk with the same cells would hash.</summary>
    private void HashStored(int f, ReadOnlySpan<byte> payload, ref Hash64State state)
    {
        int chunkBytes = _storage[f].CellSize * FieldChunk.CellCount;
        if (payload[0] == 1)
        {
            AppendCells(f, payload[1..], ref state);
            return;
        }

        if (_expandScratch is null || _expandScratch.Length < chunkBytes)
        {
            _expandScratch = new byte[chunkBytes];
        }

        var cells = _expandScratch.AsSpan(0, chunkBytes);
        var value = payload[1..];
        value.CopyTo(cells);
        ValueLayout.ZeroPadding(_storage[f].CellDataMask, cells[..value.Length]);
        int filled = value.Length;
        while (filled < cells.Length)
        {
            int copy = Math.Min(filled, cells.Length - filled);
            cells[..copy].CopyTo(cells[filled..]);
            filled += copy;
        }

        state.Append(cells);
    }

    /// <summary>
    /// Appends a full chunk of Field <paramref name="f"/>'s cells (storage order) to a hash with their padding
    /// zeroed (spec section 6): for cells held outside the storage, such as stored sections and regenerations.
    /// </summary>
    internal void AppendCells(int f, ReadOnlySpan<byte> cells, ref Hash64State state)
    {
        var mask = _storage[f].CellDataMask;
        if (mask.IsEmpty)
        {
            state.Append(cells);
            return;
        }

        if (_expandScratch is null || _expandScratch.Length < cells.Length)
        {
            _expandScratch = new byte[cells.Length];
        }

        var copy = _expandScratch.AsSpan(0, cells.Length);
        cells.CopyTo(copy);
        ValueLayout.ZeroPadding(mask, copy);
        state.Append(copy);
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

    /// <summary>
    /// Replaces every cell of one chunk of the Field <paramref name="fieldId"/> with <paramref name="cells"/>
    /// (<see cref="FieldChunk.CellCount"/> cells in storage order, raw bytes). Untyped, for whole-chunk producers
    /// such as the generation pipeline and streaming loads: a chunk whose cells are all equal is stored uniform,
    /// and storing a uniform chunk's own value again is a no-op (no expansion, no version stamp); otherwise the
    /// chunk is written dense and its change version advances. Host side, between Ticks (or from a serial hook).
    /// </summary>
    public void WriteChunkCells(int fieldId, int ordinal, ReadOnlySpan<byte> cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldId, _fields.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, _layout.ChunkCount);
        var storage = _storage[fieldId];
        int size = storage.CellSize;
        if (cells.Length != FieldChunk.CellCount * size)
        {
            throw new ArgumentException($"A chunk of Field '{_fields[fieldId].Name}' is {FieldChunk.CellCount * size} bytes, not {cells.Length}.", nameof(cells));
        }

        var first = cells[..size];
        var mask = storage.CellDataMask;
        bool uniform = true;
        for (int offset = size; offset < cells.Length; offset += size)
        {
            if (!ValueLayout.DataEqual(mask, cells.Slice(offset, size), first))
            {
                uniform = false;
                break;
            }
        }

        if (uniform)
        {
            if (storage.IsUniform(ordinal) && ValueLayout.DataEqual(mask, storage.UniformValue(ordinal), first))
            {
                return;
            }

            MarkWritten(fieldId, ordinal);
            storage.Fill(ordinal, first);
            return;
        }

        MarkWritten(fieldId, ordinal);
        cells.CopyTo(storage.WriteCells(ordinal));
    }

    internal ChunkLayout Layout => _layout;

    /// <summary>The storage behind the Field with id <paramref name="fieldId"/> (see <see cref="Fields"/>), for tooling that knows Fields by name only, such as the inspection server.</summary>
    public FieldStorage StorageAt(int fieldId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldId, _storage.Length);
        return _storage[fieldId];
    }

    private readonly List<FieldStorage> _scratch = [];

    private StencilScratchPool? _stencilScratch;

    /// <summary>Halo scratch for this World's stencil jobs: one slot per worker, freed with the set.</summary>
    internal StencilScratchPool StencilScratch => _stencilScratch ??= new StencilScratchPool(Math.Max(1, World.WorkerCount));

    /// <summary>A second storage shaped like <paramref name="field"/>'s (a stencil back buffer); owned and disposed by this set, never hashed.</summary>
    internal FieldStorage CreateScratchStorage<T>(Field<T> field)
        where T : unmanaged
    {
        int id = Validate(field);
        var storage = new CpuFieldStorage(_fields[id].CellSize, _layout.ChunkCount, _storage[id].UniformValue(0), _dataMasks[id]);
        _scratch.Add(storage);
        return storage;
    }

    /// <summary>The version a write made now gets: the Tick in progress plus one, so 0 means "never written".</summary>
    public uint CurrentVersion => (uint)(World.CurrentTick + 1);

    /// <summary>Change version of one chunk of one Field: the <see cref="CurrentVersion"/> at its last write, 0 if never written.</summary>
    public uint VersionOf<T>(Field<T> field, int ordinal)
        where T : unmanaged
        => _versions[Validate(field)][ordinal];

    internal void MarkWritten(int fieldId, int ordinal)
    {
        if (_residency[ordinal] == ChunkResidency.Unloaded)
        {
            throw new InvalidOperationException($"Chunk {_layout.CoordOf(ordinal)} is Unloaded; its cells are not resident. Writes reach only Active and Hibernating chunks (spec section 5.3).");
        }

        _versions[fieldId][ordinal] = CurrentVersion;
    }

    internal byte[] InitialValueOf(int fieldId) => _initial[fieldId];

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

    /// <summary>
    /// Every Field in id order, every chunk in Morton order, cells in storage order (spec section 6). Versions and
    /// residency are excluded (the manager hashes residency). An Unloaded chunk hashes from its stored section, or
    /// from a regeneration when it was dropped unchanged, so the hash equals the never-unloaded World's.
    /// </summary>
    void IWorldExtension.Hash(ref Hash64State state)
    {
        for (int f = 0; f < _storage.Length; f++)
        {
            for (int ordinal = 0; ordinal < _storage[f].ChunkCount; ordinal++)
            {
                AppendChunk(f, ordinal, ref state);
            }
        }
    }

    /// <summary>
    /// One item per Field (in id order) with one leaf per chunk (Morton ordinal, chunk coordinate), each leaf the
    /// digest of exactly the cells <see cref="IWorldExtension.Hash"/> appends for that chunk (spec section 10.2).
    /// </summary>
    void IWorldExtension.HashTree(HashTreeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        for (int f = 0; f < _storage.Length; f++)
        {
            builder.BeginItem(_fields[f].Name, HashTreeLeafKind.FieldChunk);
            for (int ordinal = 0; ordinal < _storage[f].ChunkCount; ordinal++)
            {
                var state = new Hash64State(WorldHashTree.NodeSeed);
                AppendChunk(f, ordinal, ref state);
                var coord = _layout.CoordOf(ordinal);
                builder.AddLeaf(ordinal, coord.X, coord.Y, coord.Z, state.Finish());
            }

            builder.EndItem();
        }
    }

    /// <summary>The cells of one Field chunk as the World hash sees them, whatever the chunk's residency or storage form.</summary>
    private void AppendChunk(int f, int ordinal, ref Hash64State state)
    {
        if (_residency[ordinal] == ChunkResidency.Unloaded)
        {
            if (_stored[f][ordinal] is { } payload)
            {
                HashStored(f, payload, ref state);
                return;
            }

            if (Residency is { } manager && manager.TryHashRegenerated(f, ordinal, ref state))
            {
                return;
            }
        }

        _storage[f].HashChunk(ordinal, ref state);
    }

    /// <summary>
    /// One section per Field chunk (spec section 7.1), named <c>&lt;field&gt;/&lt;ordinal&gt;</c>: a kind byte (0 uniform,
    /// 1 dense) then the uniform value or the cells in storage order. An Unloaded chunk writes its stored section
    /// as is, or no section when it is regenerable unchanged (never-generated chunks are not stored, spec section
    /// 5.3). Residency goes in <see cref="ResidencySection"/> when any chunk is not Active. Versions and the change
    /// feed are not saved.
    /// </summary>
    void IWorldExtension.Save(ExtensionStateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        bool anyNotActive = false;
        var residency = new byte[_layout.ChunkCount];
        for (int ordinal = 0; ordinal < residency.Length; ordinal++)
        {
            residency[ordinal] = (byte)_residency[ordinal];
            anyNotActive |= _residency[ordinal] != ChunkResidency.Active;
        }

        if (anyNotActive)
        {
            writer.WriteSection(ResidencySection, residency);
        }

        for (int f = 0; f < _storage.Length; f++)
        {
            var storage = _storage[f];
            for (int ordinal = 0; ordinal < storage.ChunkCount; ordinal++)
            {
                string name = SectionName(f, ordinal);
                if (_residency[ordinal] == ChunkResidency.Unloaded)
                {
                    if (_stored[f][ordinal] is { } payload)
                    {
                        writer.WriteSection(name, payload);
                    }

                    continue;
                }

                writer.WriteSection(name, EncodeSection(storage, ordinal));
            }
        }
    }

    void IWorldExtension.Load(ExtensionStateReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.TryGetSection(ResidencySection, out var states))
        {
            if (states.Length != _layout.ChunkCount)
            {
                throw new InvalidDataException($"Section '{ResidencySection}' holds {states.Length} chunk states, not {_layout.ChunkCount}.");
            }

            for (int ordinal = 0; ordinal < states.Length; ordinal++)
            {
                if (states[ordinal] > (byte)ChunkResidency.Unloaded)
                {
                    throw new InvalidDataException($"Section '{ResidencySection}' chunk {ordinal} has residency {states[ordinal]}.");
                }

                _residency[ordinal] = (ChunkResidency)states[ordinal];
            }
        }
        else
        {
            Array.Fill(_residency, ChunkResidency.Active);
        }

        for (int f = 0; f < _storage.Length; f++)
        {
            var storage = _storage[f];
            for (int ordinal = 0; ordinal < storage.ChunkCount; ordinal++)
            {
                string name = SectionName(f, ordinal);
                _versions[f][ordinal] = 0;
                _stored[f][ordinal] = null;
                if (!reader.TryGetSection(name, out var payload))
                {
                    if (_residency[ordinal] == ChunkResidency.Unloaded)
                    {
                        continue;
                    }

                    throw new InvalidDataException($"The save has no section '{name}' for extension '{reader.ExtensionName}'.");
                }

                if (_residency[ordinal] == ChunkResidency.Unloaded)
                {
                    // Exactly its save section (spec section 5.3): kept as bytes, never materialised until reloaded.
                    _stored[f][ordinal] = payload.ToArray();
                    continue;
                }

                DecodeSection(f, ordinal, payload, name);
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

        _stencilScratch?.Dispose();
    }
}

/// <summary>Residency state of a chunk (spec section 5.3); transitions happen only in the Streaming Phase (<see cref="ChunkResidencyManager"/>).</summary>
public enum ChunkResidency
{
    /// <summary>Resident and simulated.</summary>
    Active,

    /// <summary>Resident, not simulated.</summary>
    Hibernating,

    /// <summary>On disk or not yet generated.</summary>
    Unloaded,
}
