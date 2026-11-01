using System.Buffers.Binary;
using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// Per-World residency state of spec section 5.3. Focus points arrive as <see cref="FocusPoint"/> Input Commands and
/// are applied in the Input Phase; the Streaming-Phase System computes every chunk's desired state (Active within a
/// focus radius, Hibernating in the ring one chunk further out, Unloaded elsewhere; everything Active without focus
/// points) and applies the transitions in canonical chunk order. A chunk-owned Entity (one carrying the TilePos
/// Component) freezes in place with its chunk: every Component's enable bit goes off and <see cref="Frozen"/> keeps
/// the bits, so thawing restores the Entity and the World hash exactly. An Unloaded chunk's cells live on as their
/// save section, or as nothing when the <see cref="IChunkSource"/> regenerates them identically. <see cref="Pinned"/>
/// Entities are never frozen.
/// </summary>
public sealed class ChunkResidencyManager : IWorldExtension
{
    private const string StateSection = "state";
    private const int FocusBytes = 20;

    private readonly World _world;
    private readonly FieldSet _fields;
    private readonly ChunkLayout _layout;
    private readonly ResidencyBuilder _definition;
    private readonly ComponentType _tilePos;
    private readonly ComponentType _frozen;
    private readonly ComponentType _pinned;
    private readonly ComponentType[] _freezable;
    private readonly ChunkResidency[] _desired;
    private readonly bool[] _generated;
    private readonly byte[] _hashScratch;
    private readonly CommandBuffer _commands;
    private readonly CommandBuffer _placements;
    private readonly List<int> _placedOrdinals = [];
    private FocusPoint[] _focus;
    private int _focusCount;
    private List<EntityId>[] _movers = [];
    private IChunkSource? _source;
    private bool _sourceResolved;
    private ChunkCellBuffer? _cells;
    private int _cellsOrdinal = -1;

    internal ChunkResidencyManager(World world, ResidencyBuilder definition)
    {
        _world = world;
        _definition = definition;
        _fields = world.Fields();
        _layout = _fields.Layout;
        _tilePos = definition.TilePos;
        _frozen = definition.Frozen;
        _pinned = definition.Pinned;
        _fields.Residency = this;

        var components = world.Schema.Components;
        var freezable = new List<ComponentType>(components.Length);
        foreach (ref readonly var component in components)
        {
            if (component.Type != _frozen && component.Type != _pinned)
            {
                freezable.Add(component.Type);
            }
        }

        _freezable = [.. freezable];
        _desired = new ChunkResidency[_layout.ChunkCount];
        _generated = new bool[_layout.ChunkCount];
        _hashScratch = new byte[_layout.ChunkCount * 2];
        _focus = new FocusPoint[definition.Options.MaxFocusPoints];
        _commands = world.CreateCommandBuffer();
        _placements = world.CreateCommandBuffer();
    }

    public World World => _world;

    /// <summary>The focus points in Id order.</summary>
    public ReadOnlySpan<FocusPoint> FocusPoints => _focus.AsSpan(0, _focusCount);

    public ChunkResidency ResidencyOf(int ordinal) => _fields.ResidencyOf(ordinal);

    public ChunkResidency ResidencyOf(ChunkCoord coord) => _fields.ResidencyOf(_layout.OrdinalOf(coord));

    public ResidencyCounts Counts
    {
        get
        {
            int active = 0, hibernating = 0, unloaded = 0;
            for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
            {
                switch (_fields.ResidencyOf(ordinal))
                {
                    case ChunkResidency.Active: active++; break;
                    case ChunkResidency.Hibernating: hibernating++; break;
                    default: unloaded++; break;
                }
            }

            return new ResidencyCounts(active, hibernating, unloaded);
        }
    }

    /// <summary>True when the Entity is frozen with its chunk (carries <see cref="Frozen"/>).</summary>
    public bool IsFrozen(EntityId id) => _world.Has(id, _frozen);

    /// <summary>True when an Unloaded chunk's cells are kept as their save section (false: resident, or regenerable).</summary>
    public bool HasStoredCells(int ordinal) => _fields.HasStoredCells(ordinal);

    /// <summary>True once the chunk's initial Entities were placed from the chunk source (or the host marked it generated).</summary>
    public bool HasBeenGenerated(int ordinal) => _generated[ordinal];

    /// <summary>The chunk source of this World, or null when the schema registered none.</summary>
    public IChunkSource? Source
    {
        get
        {
            if (!_sourceResolved)
            {
                _sourceResolved = true;
                _source = _definition.ChunkSource?.Invoke(_world);
            }

            return _source;
        }
    }

    /// <summary>
    /// Records that the chunk's cells and Entities were written from the chunk source by the host (for example a
    /// pipeline's initial GenerateAll), so a later reload does not place its Entities again.
    /// </summary>
    public void MarkGenerated(int ordinal) => _generated[ordinal] = true;

    /// <summary>Every Active chunk's in-grid neighbours (26-neighbourhood) are Active or Hibernating: no Active chunk borders unloaded space.</summary>
    public bool RingInvariantHolds()
    {
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            if (_fields.ResidencyOf(ordinal) != ChunkResidency.Active)
            {
                continue;
            }

            var c = _layout.CoordOf(ordinal);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int neighbour = _layout.TryOrdinalOf(c.X + dx, c.Y + dy, c.Z + dz);
                        if (neighbour >= 0 && _fields.ResidencyOf(neighbour) == ChunkResidency.Unloaded)
                        {
                            return false;
                        }
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Host-side transition between Ticks (tests, editors, a forced evict/reload): applies the same transition the
    /// Streaming Phase would and plays its commands back at once. The next Streaming Phase reconciles the chunk with
    /// the focus points again.
    /// </summary>
    public void ForceResidency(int ordinal, ChunkResidency state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, _layout.ChunkCount);
        Transition(ordinal, _fields.ResidencyOf(ordinal), state, _commands);
        Flush(_commands);
    }

    // ---- Systems ----

    internal void ApplyFocusCommands(in SystemContext context)
    {
        foreach (var entry in context.Inputs<FocusPoint>(_definition.FocusCommand))
        {
            var point = entry.Command;
            int index = IndexOfFocus(point.Id);
            if (point.Radius < 0)
            {
                if (index >= 0)
                {
                    Array.Copy(_focus, index + 1, _focus, index, _focusCount - index - 1);
                    _focusCount--;
                }

                continue;
            }

            if (index >= 0)
            {
                _focus[index] = point;
                continue;
            }

            if (_focusCount == _focus.Length)
            {
                context.Reject(in entry, ResidencyBuilder.RejectTooManyFocusPoints);
                continue;
            }

            int at = ~index;
            Array.Copy(_focus, at, _focus, at + 1, _focusCount - at);
            _focus[at] = point;
            _focusCount++;
        }
    }

    /// <summary>Binary search by Id: the index, or the bitwise complement of the insertion point.</summary>
    private int IndexOfFocus(uint id)
    {
        int low = 0, high = _focusCount - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            uint candidate = _focus[mid].Id;
            if (candidate == id)
            {
                return mid;
            }

            if (candidate < id)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }

    internal void BeginStreaming(in SystemContext context)
    {
        if (_movers.Length < context.JobCount)
        {
            int old = _movers.Length;
            Array.Resize(ref _movers, Math.Max(context.JobCount, old * 2));
            for (int i = old; i < _movers.Length; i++)
            {
                _movers[i] = [];
            }
        }

        ComputeDesired();
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            var current = _fields.ResidencyOf(ordinal);
            if (current != _desired[ordinal])
            {
                Transition(ordinal, current, _desired[ordinal], context.Commands);
            }
        }
    }

    internal void CollectMovers(in SystemContext context, in ChunkView chunk)
    {
        var positions = chunk.Column<TilePos>(_tilePos);
        var ids = chunk.Entities;
        var movers = _movers[context.JobIndex];
        foreach (int row in chunk.EnabledRows(_tilePos))
        {
            var pos = positions[row];
            int ordinal = _layout.TryOrdinalOf(pos.X >> FieldChunk.Shift, pos.Y >> FieldChunk.Shift, pos.Z >> FieldChunk.Shift);
            if (ordinal < 0 || _fields.ResidencyOf(ordinal) == ChunkResidency.Active || _world.Has(ids[row], _pinned))
            {
                continue;
            }

            movers.Add(ids[row]);
        }
    }

    internal void EndStreaming(in SystemContext context)
    {
        for (int job = 0; job < context.JobCount; job++)
        {
            var movers = _movers[job];
            foreach (var id in movers)
            {
                FreezeEntity(id, context.Commands);
            }

            movers.Clear();
        }
    }

    private void ComputeDesired()
    {
        if (_focusCount == 0)
        {
            Array.Fill(_desired, ChunkResidency.Active);
            return;
        }

        Array.Fill(_desired, _definition.Options.HibernateInsteadOfUnload ? ChunkResidency.Hibernating : ChunkResidency.Unloaded);
        for (int i = 0; i < _focusCount; i++)
        {
            var focus = _focus[i];
            var centre = ChunkCoord.OfTile(focus.Center);
            int r = focus.Radius;
            for (int z = centre.Z - r; z <= centre.Z + r; z++)
            {
                for (int y = centre.Y - r; y <= centre.Y + r; y++)
                {
                    for (int x = centre.X - r; x <= centre.X + r; x++)
                    {
                        int ordinal = _layout.TryOrdinalOf(x, y, z);
                        if (ordinal >= 0)
                        {
                            _desired[ordinal] = ChunkResidency.Active;
                        }
                    }
                }
            }
        }

        // The Hibernating ring: every non-Active chunk touching an Active one (26-neighbourhood).
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            if (_desired[ordinal] == ChunkResidency.Active)
            {
                continue;
            }

            var c = _layout.CoordOf(ordinal);
            for (int dz = -1; dz <= 1 && _desired[ordinal] != ChunkResidency.Hibernating; dz++)
            {
                for (int dy = -1; dy <= 1 && _desired[ordinal] != ChunkResidency.Hibernating; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int neighbour = _layout.TryOrdinalOf(c.X + dx, c.Y + dy, c.Z + dz);
                        if (neighbour >= 0 && _desired[neighbour] == ChunkResidency.Active)
                        {
                            _desired[ordinal] = ChunkResidency.Hibernating;
                            break;
                        }
                    }
                }
            }
        }
    }

    // ---- Transitions ----

    private void Transition(int ordinal, ChunkResidency from, ChunkResidency to, CommandBuffer commands)
    {
        if (from == to)
        {
            return;
        }

        switch (from, to)
        {
            case (ChunkResidency.Active, ChunkResidency.Hibernating):
                FreezeChunk(ordinal, commands);
                _fields.SetResidency(ordinal, ChunkResidency.Hibernating);
                break;
            case (ChunkResidency.Active, ChunkResidency.Unloaded):
                FreezeChunk(ordinal, commands);
                UnloadCells(ordinal);
                _fields.SetResidency(ordinal, ChunkResidency.Unloaded);
                break;
            case (ChunkResidency.Hibernating, ChunkResidency.Active):
                ThawChunk(ordinal, commands);
                _fields.SetResidency(ordinal, ChunkResidency.Active);
                break;
            case (ChunkResidency.Hibernating, ChunkResidency.Unloaded):
                UnloadCells(ordinal);
                _fields.SetResidency(ordinal, ChunkResidency.Unloaded);
                break;
            case (ChunkResidency.Unloaded, ChunkResidency.Hibernating):
                LoadCells(ordinal);
                break;
            case (ChunkResidency.Unloaded, ChunkResidency.Active):
                LoadCells(ordinal);
                ThawChunk(ordinal, commands);
                _fields.SetResidency(ordinal, ChunkResidency.Active);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(to), to, "Unknown residency state.");
        }
    }

    /// <summary>Takes the chunk's cells out of the storage: dropped when the source regenerates them identically, otherwise kept as their save section.</summary>
    private void UnloadCells(int ordinal)
    {
        bool store = true;
        if (Source is { } source)
        {
            var cells = Regenerate(source, ordinal);
            store = false;
            for (int f = 0; f < _fields.FieldCount; f++)
            {
                if (!_fields.StorageAt(f).CellsEqual(ordinal, cells.Cells(f)))
                {
                    store = true;
                    break;
                }
            }
        }

        _fields.UnloadCells(ordinal, store);
    }

    /// <summary>Makes the chunk Hibernating with its cells back: from the stored section, else regenerated (placing its Entities the first time).</summary>
    private void LoadCells(int ordinal)
    {
        _fields.SetResidency(ordinal, ChunkResidency.Hibernating);
        if (_fields.ReloadStoredCells(ordinal) || Source is not { } source)
        {
            return;
        }

        var cells = Regenerate(source, ordinal);
        for (int f = 0; f < _fields.FieldCount; f++)
        {
            _fields.WriteChunkCells(f, ordinal, cells.Cells(f));
        }

        if (!_generated[ordinal])
        {
            _generated[ordinal] = true;
            source.PlaceEntities(_layout.CoordOf(ordinal), _placements);
            _placedOrdinals.Add(ordinal);
        }
    }

    private ChunkCellBuffer Regenerate(IChunkSource source, int ordinal)
    {
        _cells ??= new ChunkCellBuffer(_fields);
        if (_cellsOrdinal != ordinal)
        {
            _cellsOrdinal = -1;
            _cells.Reset();
            source.GenerateCells(_layout.CoordOf(ordinal), _cells);
            _cellsOrdinal = ordinal;
        }

        return _cells;
    }

    /// <summary>Hashes an unstored Unloaded chunk's cells of one Field as the regenerated dense chunk; false without a source.</summary>
    internal bool TryHashRegenerated(int fieldId, int ordinal, ref Hash64State state)
    {
        if (Source is not { } source)
        {
            return false;
        }

        state.Append(Regenerate(source, ordinal).Cells(fieldId));
        return true;
    }

    /// <summary>Plays back pending commands and freezes the Entities just placed into chunks that are not Active.</summary>
    private void Flush(CommandBuffer commands)
    {
        if (!commands.IsEmpty)
        {
            _world.Playback(commands);
        }

        if (_placements.IsEmpty && _placedOrdinals.Count == 0)
        {
            return;
        }

        _world.Playback(_placements);
        foreach (int ordinal in _placedOrdinals)
        {
            if (_fields.ResidencyOf(ordinal) != ChunkResidency.Active)
            {
                FreezeChunk(ordinal, _placements);
            }
        }

        _placedOrdinals.Clear();
        if (!_placements.IsEmpty)
        {
            _world.Playback(_placements);
        }
    }

    private void FreezeChunk(int ordinal, CommandBuffer commands)
    {
        var coord = _layout.CoordOf(ordinal);
        foreach (var chunk in _world.Query(ComponentMask.Of(_tilePos), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(_tilePos);
            var ids = chunk.Entities;
            for (int row = 0; row < positions.Length; row++)
            {
                if (ChunkCoord.OfTile(positions[row]) == coord)
                {
                    FreezeEntity(ids[row], commands);
                }
            }
        }
    }

    private void ThawChunk(int ordinal, CommandBuffer commands)
    {
        var coord = _layout.CoordOf(ordinal);
        foreach (var chunk in _world.Query(ComponentMask.Of(_tilePos), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(_tilePos);
            var ids = chunk.Entities;
            for (int row = 0; row < positions.Length; row++)
            {
                if (ChunkCoord.OfTile(positions[row]) == coord)
                {
                    ThawEntity(ids[row], commands);
                }
            }
        }
    }

    private void FreezeEntity(EntityId id, CommandBuffer commands)
    {
        if (_world.Has(id, _frozen) || _world.Has(id, _pinned))
        {
            return;
        }

        var enabled = ComponentMask.Empty;
        foreach (var type in _freezable)
        {
            if (!_world.Has(id, type))
            {
                continue;
            }

            if (_world.IsEnabled(id, type))
            {
                enabled = enabled.With(type);
                _world.SetEnabled(id, type, false);
            }
        }

        commands.Add(id, _frozen, new Frozen(enabled));
    }

    private void ThawEntity(EntityId id, CommandBuffer commands)
    {
        if (!_world.Has(id, _frozen))
        {
            return;
        }

        var enabled = _world.Get<Frozen>(id, _frozen).Enabled;
        foreach (var type in _freezable)
        {
            if (enabled.Contains(type) && _world.Has(id, type))
            {
                _world.SetEnabled(id, type, true);
            }
        }

        commands.Remove(id, _frozen);
    }

    // ---- IWorldExtension ----

    public void BeginTick(long tick)
    {
    }

    public void EndPhase(Phase phase)
    {
        // The Streaming Phase's buffers were merged; Entities a chunk source placed this Phase are created now, still
        // inside the Streaming boundary, and frozen when their chunk is Hibernating. (Jobs of the Phase were gathered
        // before the Begin hook ran, so the placement cannot be played back from the hook itself.)
        if (phase == Phase.Streaming)
        {
            Flush(_placements);
        }
    }

    public void EndTick(long tick)
    {
    }

    /// <summary>Focus points, every chunk's residency and generated flag (canonical order). Entities and cells are hashed by their owners.</summary>
    public void Hash(ref Hash64State state)
    {
        state.Append(in _focusCount);
        state.Append<FocusPoint>(_focus.AsSpan(0, _focusCount));
        state.Append(ResidencyAndGeneratedBytes());
    }

    private ReadOnlySpan<byte> ResidencyAndGeneratedBytes()
    {
        int n = _layout.ChunkCount;
        for (int ordinal = 0; ordinal < n; ordinal++)
        {
            _hashScratch[ordinal] = (byte)_fields.ResidencyOf(ordinal);
            _hashScratch[n + ordinal] = _generated[ordinal] ? (byte)1 : (byte)0;
        }

        return _hashScratch;
    }

    /// <summary>One section: focus count, the focus points (Id, X, Y, Z, Radius as little-endian 32-bit values) and one generated byte per chunk. Residency is saved by the Fields.</summary>
    public void Save(ExtensionStateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var payload = new byte[4 + (_focusCount * FocusBytes) + _layout.ChunkCount];
        BinaryPrimitives.WriteInt32LittleEndian(payload, _focusCount);
        int offset = 4;
        for (int i = 0; i < _focusCount; i++)
        {
            var f = _focus[i];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), f.Id);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4), f.Center.X);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 8), f.Center.Y);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 12), f.Center.Z);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 16), f.Radius);
            offset += FocusBytes;
        }

        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            payload[offset + ordinal] = _generated[ordinal] ? (byte)1 : (byte)0;
        }

        writer.WriteSection(StateSection, payload);
    }

    public void Load(ExtensionStateReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var payload = reader.Section(StateSection);
        if (payload.Length < 4)
        {
            throw new InvalidDataException($"Section '{StateSection}' of extension '{reader.ExtensionName}' is {payload.Length} bytes; expected at least 4.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0 || count > _focus.Length || payload.Length != 4 + (count * FocusBytes) + _layout.ChunkCount)
        {
            throw new InvalidDataException($"Section '{StateSection}' of extension '{reader.ExtensionName}' holds {count} focus points in {payload.Length} bytes; expected {4 + (Math.Max(count, 0) * FocusBytes) + _layout.ChunkCount} bytes for at most {_focus.Length} points.");
        }

        int offset = 4;
        uint previous = 0;
        for (int i = 0; i < count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            if (i > 0 && id <= previous)
            {
                throw new InvalidDataException($"Section '{StateSection}' of extension '{reader.ExtensionName}': focus points are not in ascending Id order.");
            }

            previous = id;
            var centre = new TilePos(
                BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 8)..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 12)..]));
            _focus[i] = new FocusPoint(id, centre, BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 16)..]));
            offset += FocusBytes;
        }

        _focusCount = count;
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            _generated[ordinal] = payload[offset + ordinal] != 0;
        }

        _cellsOrdinal = -1;
    }

    public void Dispose()
    {
    }
}
