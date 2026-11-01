using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Deferred structural changes (spec section 4.1): create, destroy, add and remove Component. Commands
/// are byte-encoded into pages taken from the World's per-Tick scratch arena and applied in record order
/// by <see cref="World.Playback"/> at a sync point. <see cref="Create()"/> returns a deferred handle that
/// later commands in the same buffer may target; after playback <see cref="Resolve"/> maps it to the real
/// <see cref="EntityId"/> until the scratch arena is reset. Commands aimed at an Entity that is no longer
/// alive are skipped and counted in <see cref="World.Counters"/>.
/// </summary>
public sealed class CommandBuffer
{
    private const int PageSize = 16 * 1024;
    private const int HeaderSize = 16;
    private const int PayloadCapacity = PageSize - HeaderSize;
    private const int ResolvedBlockShift = 13;
    private const int ResolvedBlockSize = 1 << ResolvedBlockShift;

    private readonly WorldSchema _schema;
    private nint _firstPage;
    private nint _currentPage;
    private uint _arenaGeneration;
    private int _createCount;
    private nint _resolvedDirectory;
    private int _resolvedCount;
    private uint _resolvedGeneration;

    internal CommandBuffer(World world)
    {
        World = world;
        _schema = world.Schema;
    }

    private enum Kind : byte
    {
        Destroy = 1,
        Create = 2,
        Add = 3,
        AddDefault = 4,
        Remove = 5,
        Emit = 6,
        Relate = 7,
    }

    internal World World { get; }

    /// <summary>The System whose jobs record into this buffer, or -1 for a host buffer; named in failure events.</summary>
    internal int OwnerSystemId { get; set; } = -1;

    /// <summary>Commands recorded since the last playback.</summary>
    public int Count { get; private set; }

    public bool IsEmpty => Count == 0;

    /// <summary>Records the creation of an Entity with no fragmenting Components; add them with <see cref="Add{T}"/>.</summary>
    public EntityId Create() => Create(ComponentMask.Empty);

    /// <summary>
    /// Records the creation of an Entity directly in the archetype for <paramref name="signature"/>,
    /// with every Component zero-initialised. Cheaper than creating empty and adding one by one.
    /// </summary>
    public unsafe EntityId Create(ComponentMask signature)
    {
        byte* p = Reserve(1 + sizeof(ComponentMask));
        *p = (byte)Kind.Create;
        Unsafe.WriteUnaligned(p + 1, signature);
        Count++;
        return EntityId.Deferred(_createCount++);
    }

    public unsafe void Destroy(EntityId entity)
    {
        byte* p = Reserve(1 + sizeof(ulong));
        *p = (byte)Kind.Destroy;
        Unsafe.WriteUnaligned(p + 1, entity.Value);
        Count++;
    }

    /// <summary>Adds the Component with the given value, or overwrites it when the Entity already has it.</summary>
    public unsafe void Add<T>(EntityId entity, ComponentType type, in T value) where T : unmanaged
    {
        _schema.ValidateType(type);
        int size = _schema.Sizes[type.Id];
        if (Unsafe.SizeOf<T>() != size)
        {
            throw new ArgumentException($"{typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes but Component '{_schema.Describe(type).Name}' is {size} bytes.", nameof(T));
        }

        if (_schema.Components[type.Id].IsBuffer && Unsafe.As<T, BufferHeader>(ref Unsafe.AsRef(in value)).Handle != 0)
        {
            throw new ArgumentException($"The buffer Component value owns overflow storage and cannot be copied; add elements through World.GetBuffer or ChunkView.Buffer instead.", nameof(value));
        }

        byte* p = Reserve(1 + sizeof(ulong) + sizeof(int) + size);
        *p = (byte)Kind.Add;
        Unsafe.WriteUnaligned(p + 1, entity.Value);
        Unsafe.WriteUnaligned(p + 9, type.Id);
        Unsafe.WriteUnaligned(p + 13, value);
        Count++;
    }

    /// <summary>
    /// Wire-form <see cref="Add{T}"/> (spec section 10.2): the Component's value as exactly
    /// <see cref="ComponentDescriptor.Size"/> raw bytes, for tooling and the C ABI that know Components by
    /// name only. Buffer Components are refused (their value owns storage).
    /// </summary>
    public unsafe void Add(EntityId entity, ComponentType type, ReadOnlySpan<byte> value)
    {
        _schema.ValidateType(type);
        int size = _schema.Sizes[type.Id];
        if (value.Length != size)
        {
            throw new ArgumentException($"Component '{_schema.Describe(type).Name}' is {size} bytes; the value has {value.Length}.", nameof(value));
        }

        if (_schema.Components[type.Id].IsBuffer)
        {
            throw new ArgumentException($"Component '{_schema.Describe(type).Name}' is a buffer Component; add it zeroed and fill it through World.GetBuffer.", nameof(type));
        }

        byte* p = Reserve(1 + sizeof(ulong) + sizeof(int) + size);
        *p = (byte)Kind.Add;
        Unsafe.WriteUnaligned(p + 1, entity.Value);
        Unsafe.WriteUnaligned(p + 9, type.Id);
        value.CopyTo(new Span<byte>(p + 13, size));
        Count++;
    }

    /// <summary>Adds the Component zero-initialised (a tag, or a value filled in later), or zeroes it when already present.</summary>
    public unsafe void Add(EntityId entity, ComponentType type)
    {
        _schema.ValidateType(type);
        byte* p = Reserve(1 + sizeof(ulong) + sizeof(int));
        *p = (byte)Kind.AddDefault;
        Unsafe.WriteUnaligned(p + 1, entity.Value);
        Unsafe.WriteUnaligned(p + 9, type.Id);
        Count++;
    }

    public unsafe void Remove(EntityId entity, ComponentType type)
    {
        _schema.ValidateType(type);
        byte* p = Reserve(1 + sizeof(ulong) + sizeof(int));
        *p = (byte)Kind.Remove;
        Unsafe.WriteUnaligned(p + 1, entity.Value);
        Unsafe.WriteUnaligned(p + 9, type.Id);
        Count++;
    }

    /// <summary>
    /// Records that <paramref name="source"/> relates to <paramref name="target"/> in <paramref name="relation"/>
    /// (spec section 4.1), replacing any earlier target. Either may be a deferred handle from this buffer. At
    /// playback a dead target skips the op and reports a <see cref="StructuralFailure"/> with
    /// <see cref="StructuralOp.Relate"/>; a <see cref="EntityId.Null"/> target is an <see cref="Unrelate"/>.
    /// </summary>
    public unsafe void Relate(EntityId source, RelationType relation, EntityId target)
    {
        _schema.ValidateRelation(relation);
        byte* p = Reserve(1 + sizeof(ulong) + sizeof(int) + sizeof(ulong));
        *p = (byte)Kind.Relate;
        Unsafe.WriteUnaligned(p + 1, source.Value);
        Unsafe.WriteUnaligned(p + 9, relation.Component.Id);
        Unsafe.WriteUnaligned(p + 13, target.Value);
        Count++;
    }

    /// <summary>Records that <paramref name="source"/> leaves <paramref name="relation"/> (drops its link; a no-op when it has none).</summary>
    public void Unrelate(EntityId source, RelationType relation)
    {
        _schema.ValidateRelation(relation);
        Remove(source, relation.Component);
    }

    /// <summary>Puts <paramref name="child"/> under <paramref name="parent"/> in the built-in hierarchy (<see cref="WorldBuilder.AddHierarchy"/>).</summary>
    public void SetParent(EntityId child, EntityId parent) => Relate(child, _schema.Hierarchy, parent);

    /// <summary>Detaches <paramref name="child"/> from its parent in the built-in hierarchy.</summary>
    public void ClearParent(EntityId child) => Unrelate(child, _schema.Hierarchy);

    /// <summary>
    /// Records an event for the typed queue <paramref name="queue"/> (spec section 4.3). Appended to the
    /// queue at playback, so events merge with the same System, job then record order as structural ops
    /// and become readable from the next Phase.
    /// </summary>
    public unsafe void Emit<T>(ResourceType queue, in T value) where T : unmanaged
    {
        _schema.ValidateResource<T>(queue, ResourceKind.EventQueue);
        int size = Unsafe.SizeOf<T>();
        byte* p = Reserve(1 + sizeof(int) + size);
        *p = (byte)Kind.Emit;
        Unsafe.WriteUnaligned(p + 1, queue.Id);
        Unsafe.WriteUnaligned(p + 5, value);
        Count++;
    }

    // ---- Raw-byte recording (TASK-28): the C ABI records values it holds only as bytes. Same records as Add<T>/Emit<T>. ----

    /// <summary>Records an Add of <paramref name="size"/> bytes at <paramref name="value"/>; the size must match the Component's.</summary>
    internal unsafe void AddRaw(EntityId entity, ComponentType type, byte* value, int size)
    {
        _schema.ValidateType(type);
        int expected = _schema.Sizes[type.Id];
        if (size != expected || value == null)
        {
            throw new ArgumentException($"Component '{_schema.Describe(type).Name}' is {expected} bytes; {size} bytes were given.", nameof(size));
        }

        if (_schema.Components[type.Id].IsBuffer || _schema.RelationOfComponent[type.Id] >= 0)
        {
            throw new ArgumentException($"Component '{_schema.Describe(type).Name}' is a buffer or Relation Component and cannot be added from raw bytes.", nameof(type));
        }

        byte* p = Reserve(1 + sizeof(ulong) + sizeof(int) + size);
        *p = (byte)Kind.Add;
        Unsafe.WriteUnaligned(p + 1, entity.Value);
        Unsafe.WriteUnaligned(p + 9, type.Id);
        new ReadOnlySpan<byte>(value, size).CopyTo(new Span<byte>(p + 13, size));
        Count++;
    }

    /// <summary>Records an event of <paramref name="size"/> bytes for the event queue <paramref name="queue"/>.</summary>
    internal unsafe void EmitRaw(ResourceType queue, byte* value, int size)
    {
        _schema.ValidateResource(queue);
        var descriptor = _schema.Resources[queue.Id];
        if (!descriptor.IsEventQueue || size != descriptor.Size || value == null)
        {
            throw new ArgumentException($"Resource '{descriptor.Name}' is not an event queue of {size} bytes.", nameof(queue));
        }

        byte* p = Reserve(1 + sizeof(int) + size);
        *p = (byte)Kind.Emit;
        Unsafe.WriteUnaligned(p + 1, queue.Id);
        new ReadOnlySpan<byte>(value, size).CopyTo(new Span<byte>(p + 5, size));
        Count++;
    }

    /// <summary>
    /// The real id of an Entity created by this buffer's last playback. Valid until the next playback or
    /// until the World resets its scratch arena.
    /// </summary>
    public unsafe EntityId Resolve(EntityId deferred)
    {
        if (!deferred.IsDeferred)
        {
            throw new ArgumentException("Only deferred handles from Create can be resolved.", nameof(deferred));
        }

        if (_resolvedDirectory == 0 || _resolvedGeneration != World.Scratch.Generation)
        {
            throw new InvalidOperationException("The deferred handle cannot be resolved: the buffer has not been played back since it was recorded, or the scratch arena was reset.");
        }

        int ordinal = deferred.DeferredOrdinal;
        if ((uint)ordinal >= (uint)_resolvedCount)
        {
            throw new ArgumentOutOfRangeException(nameof(deferred), deferred, "The handle was not created by this buffer's last playback.");
        }

        return ResolvedAt((EntityId**)_resolvedDirectory, ordinal);
    }

    private static unsafe EntityId ResolvedAt(EntityId** directory, int ordinal)
        => directory[ordinal >> ResolvedBlockShift][ordinal & (ResolvedBlockSize - 1)];

    /// <summary>Drops every recorded command without applying it.</summary>
    public void Clear()
    {
        _firstPage = 0;
        _currentPage = 0;
        Count = 0;
        _createCount = 0;
    }

    internal unsafe void Playback()
    {
        if (Count == 0)
        {
            return;
        }

        var arena = World.Scratch;
        if (_arenaGeneration != arena.Generation)
        {
            throw new InvalidOperationException("The command buffer outlived the scratch arena it was recorded into; play buffers back before ResetScratch.");
        }

        // The resolution table is a directory of 64 KiB blocks (8192 ids each), filled lazily as creates
        // are applied, so any number of creates fits the arena's per-allocation limit.
        int blocks = Math.Max(1, (_createCount + ResolvedBlockSize - 1) >> ResolvedBlockShift);
        _resolvedDirectory = (nint)arena.Alloc(blocks * sizeof(nint), sizeof(nint));
        _resolvedCount = 0;
        _resolvedGeneration = arena.Generation;
        var resolved = (EntityId**)_resolvedDirectory;

        for (nint page = _firstPage; page != 0; page = ((PageHeader*)page)->Next)
        {
            byte* p = (byte*)page + HeaderSize;
            byte* end = p + ((PageHeader*)page)->Used;
            while (p < end)
            {
                var kind = (Kind)(*p++);
                if (kind == Kind.Emit)
                {
                    int queue = Unsafe.ReadUnaligned<int>(p);
                    p += sizeof(int);
                    World.AppendEvent(queue, p);
                    p += _schema.ResourceSizes[queue];
                    continue;
                }

                if (kind == Kind.Create)
                {
                    var signature = Unsafe.ReadUnaligned<ComponentMask>(p);
                    p += sizeof(ComponentMask);
                    if ((_resolvedCount & (ResolvedBlockSize - 1)) == 0)
                    {
                        resolved[_resolvedCount >> ResolvedBlockShift] = (EntityId*)arena.Alloc(ResolvedBlockSize * sizeof(ulong), sizeof(ulong));
                    }

                    var created = World.CreateEntity(signature);
                    resolved[_resolvedCount >> ResolvedBlockShift][_resolvedCount & (ResolvedBlockSize - 1)] = created;
                    _resolvedCount++;
                    continue;
                }

                var target = Target(Unsafe.ReadUnaligned<ulong>(p), resolved);
                p += sizeof(ulong);
                if (kind == Kind.Destroy)
                {
                    World.DestroyEntity(target);
                    continue;
                }

                int componentId = Unsafe.ReadUnaligned<int>(p);
                p += sizeof(int);
                switch (kind)
                {
                    case Kind.Relate:
                        World.Relate(target, componentId, Target(Unsafe.ReadUnaligned<ulong>(p), resolved));
                        p += sizeof(ulong);
                        break;
                    case Kind.Add:
                        World.AddComponent(target, componentId, p);
                        p += _schema.Sizes[componentId];
                        break;
                    case Kind.AddDefault:
                        World.AddComponent(target, componentId, null);
                        break;
                    case Kind.Remove:
                        World.RemoveComponent(target, componentId);
                        break;
                    default:
                        throw new InvalidOperationException("Corrupt command buffer.");
                }
            }
        }

        Clear();
    }

    private unsafe EntityId Target(ulong raw, EntityId** resolved)
    {
        var id = new EntityId(raw);
        if (!id.IsDeferred)
        {
            return id;
        }

        int ordinal = id.DeferredOrdinal;
        if ((uint)ordinal >= (uint)_resolvedCount)
        {
            throw new InvalidOperationException($"{id} targets an Entity this buffer has not created.");
        }

        return ResolvedAt(resolved, ordinal);
    }

    private unsafe byte* Reserve(int bytes)
    {
        var arena = World.Scratch;
        if (_firstPage == 0)
        {
            _arenaGeneration = arena.Generation;
            _firstPage = _currentPage = NewPage(arena);
        }
        else if (_arenaGeneration != arena.Generation)
        {
            throw new InvalidOperationException("The command buffer outlived the scratch arena it was recorded into; play buffers back before ResetScratch.");
        }

        var header = (PageHeader*)_currentPage;
        if (header->Used + bytes > PayloadCapacity)
        {
            nint next = NewPage(arena);
            header->Next = next;
            _currentPage = next;
            header = (PageHeader*)next;
        }

        byte* p = (byte*)_currentPage + HeaderSize + header->Used;
        header->Used += bytes;
        return p;
    }

    private static unsafe nint NewPage(ScratchArena arena)
    {
        var page = (PageHeader*)arena.Alloc(PageSize, 16);
        page->Next = 0;
        page->Used = 0;
        return (nint)page;
    }

    private struct PageHeader
    {
        public nint Next;
        public int Used;
    }
}
