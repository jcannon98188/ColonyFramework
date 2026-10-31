using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colony.Diagnostics;
using Colony.Numerics;

namespace Colony;

/// <summary>
/// The complete simulated state of one game: its Entities and their Components, stored in archetype
/// chunks carved from native slabs (ADR 0001, spec section 4). Structural change (create, destroy, add
/// or remove a Component) goes only through <see cref="CommandBuffer"/>s played back at a sync point
/// with <see cref="Playback"/>; between sync points refs and chunk views stay valid.
/// Many Worlds can coexist in a process; nothing here is static.
/// </summary>
public sealed partial class World : IDisposable
{
    private readonly WorldOptions _options;
    private readonly EntityIndex _entities;
    private readonly NonFragmentingStore _nonFragmenting;
    private readonly ResourceStore _resources;
    private readonly Dictionary<ComponentMask, Archetype> _bySignature = [];
    private readonly List<CommandBuffer> _buffers = [];
    private Archetype[] _archetypes = new Archetype[8];
    private int _archetypeCount;
    private long _creates;
    private long _destroys;
    private long _adds;
    private long _removes;
    private long _staleSkipped;
    private int _playbackOwner = -1;
    private bool _disposed;
    private readonly IWorldExtension[] _extensions;
    private TickRunner? _runner;
    private RandomSequence _sharedRandom;

    internal World(WorldSchema schema, WorldOptions options)
    {
        Schema = schema;
        _options = options;
        DebugValidation = schema.DebugValidation;
        Allocator = new SlabAllocator(poison: DebugValidation);
        Scratch = new ScratchArena(Allocator, poison: DebugValidation);
        _bufferHeap = new BufferHeap(Allocator);
        _entities = new EntityIndex(options.InitialEntityCapacity);
        _nonFragmenting = new NonFragmentingStore(schema, _entities.Capacity);
        _relations = new RelationIndex(schema.RelationCount, _entities.Capacity);
        _resources = new ResourceStore(schema);
        Random = new RandomSource(options.Seed, RootRandomKey);
        _sharedRandom = new RandomSequence(Random.Derive("world"));
        Diagnostics = new WorldDiagnostics(this, schema, options.Diagnostics, ResolveWorkerCount());
        Inputs = new WorldInputs(this);
        _extensions = new IWorldExtension[schema.Extensions.Length];
        for (int i = 0; i < _extensions.Length; i++)
        {
            _extensions[i] = schema.Extensions[i].Create(this);
        }
    }

    /// <summary>The per-World state of a registered extension (<see cref="WorldBuilder.AddExtension"/>) by its CLR type.</summary>
    public T Extension<T>() where T : class, IWorldExtension
    {
        foreach (var extension in _extensions)
        {
            if (extension is T match)
            {
                return match;
            }
        }

        throw new InvalidOperationException($"No extension of type {typeof(T).Name} is registered in this World's schema.");
    }

    /// <summary>Extensions in registration order, for the Tick runner and the hash.</summary>
    internal ReadOnlySpan<IWorldExtension> Extensions => _extensions;

    /// <summary>
    /// Runs <paramref name="job"/> for every index in <c>[0, count)</c> on the World's worker pool and
    /// returns when all have finished. Jobs are keyed by index, never by thread, so results do not depend on
    /// <see cref="WorkerCount"/>. Call it from the Tick thread only: between Ticks, or from a System's
    /// <see cref="SystemDefinition.Begin"/>/<see cref="SystemDefinition.End"/> hook; from inside a chunk job
    /// (the pool is busy) it throws.
    /// </summary>
    public void RunParallel(int count, IIndexedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ThrowIfDisposed();
        _runner ??= new TickRunner(this, Schema.Schedule, Diagnostics.WorkerCount);
        _runner.RunParallel(count, job);
    }

    /// <summary>Per-Tick records, logs, counters and the hash trail of this World (spec section 10.1). Reading allocates nothing.</summary>
    public WorldDiagnostics Diagnostics { get; }

    /// <summary>Input Command intake and this Tick's canonical input list (spec section 7.3): <c>world.Inputs.Submit(source, command)</c> from any thread.</summary>
    public WorldInputs Inputs { get; }

    /// <summary>Stream key of the World's root <see cref="RandomSource"/> ("WRLD").</summary>
    private const ulong RootRandomKey = 0x5752_4C44UL;

    public WorldSchema Schema { get; }

    /// <summary>False when any Plugin of the schema declared itself NonDeterministic (spec section 8); see <see cref="WorldSchema.IsDeterministic"/>.</summary>
    public bool IsDeterministic => Schema.IsDeterministic;

    /// <summary>The seed every random stream in this World derives from.</summary>
    public ulong Seed => _options.Seed;

    /// <summary>Root random stream (stateless). Systems get a per-System, per-Tick derivation in <see cref="SystemContext.Random"/>.</summary>
    public RandomSource Random { get; }

    /// <summary>
    /// A World-owned random cursor for serial use (host code, <see cref="SystemDefinition.Begin"/>/<see cref="SystemDefinition.End"/>
    /// hooks). Its counter is World state: saved and part of the World hash. Never draw from it inside a chunk job.
    /// </summary>
    public ref RandomSequence SharedRandom => ref _sharedRandom;

    /// <summary>Ticks completed so far; the Tick about to run has this number.</summary>
    public long CurrentTick { get; private set; }

    /// <summary>Set when a System threw: the World no longer Ticks (spec section 4.3).</summary>
    public WorldFault? Fault { get; private set; }

    public bool IsFaulted => Fault.HasValue;

    /// <summary>Threads a Tick runs on, including the caller; fixed when the first Tick starts.</summary>
    public int WorkerCount => _runner?.WorkerCount ?? ResolveWorkerCount();

    /// <summary>Monotonic counter of change epochs (waves and sync points), stamped on columns as they change (chunk skipping, spec section 4.3).</summary>
    internal uint ChangeCounter;

    private uint _waveCounter;

    /// <summary>Opens a wave's change epoch; a System that runs in it remembers the value as its last run.</summary>
    internal uint BeginWave()
    {
        _waveCounter = ++ChangeCounter;
        return _waveCounter;
    }

    /// <summary>
    /// Runs one Tick: every Phase in order, Systems in waves on the worker pool, command buffers merged at
    /// each Phase boundary, scratch reset at the end. Returns false, running nothing, when the
    /// <see cref="WorldOptions.TickGate"/> holds the Tick. A System exception aborts the Tick, faults the
    /// World and surfaces as <see cref="SystemFaultException"/>.
    /// </summary>
    public bool Tick()
    {
        ThrowIfDisposed();
        if (Fault is { } fault)
        {
            throw new InvalidOperationException($"The World is faulted: System '{fault.SystemName}' threw during Tick {fault.Tick}.");
        }

        if (_options.TickGate is { } gate && !gate.TryPass())
        {
            return false;
        }

        _runner ??= new TickRunner(this, Schema.Schedule, Diagnostics.WorkerCount);
        Diagnostics.BeforeTick(CurrentTick);
        try
        {
            _runner.RunTick(CurrentTick);
        }
        catch (SystemFaultException e)
        {
            Fault = e.Fault;
            throw;
        }

        CurrentTick++;
        Diagnostics.AfterTick(CurrentTick);
        return true;
    }

    /// <summary>Managed bytes the dedicated worker threads have allocated so far (0 after warm-up in a steady state).</summary>
    public long WorkerAllocatedBytes => _runner?.WorkerAllocatedBytes ?? 0;

    private int ResolveWorkerCount()
        => _options.WorkerThreads > 0 ? _options.WorkerThreads : PhysicalCores.Count();

    /// <summary>True when this World validates refs and views, poisons freed memory and reports leaks.</summary>
    public bool DebugValidation { get; }

    public int EntityCount => _entities.AliveCount;

    public StructuralCounters Counters => new(_creates, _destroys, _adds, _removes, _staleSkipped);

    /// <summary>Running totals of what the scheduler ran and skipped (spec section 4.3: rates, change versions, asleep and enable bits).</summary>
    public SchedulerCounters SchedulerCounters => SchedulerCounts;

    internal SchedulerCounters SchedulerCounts;

    /// <summary>Native memory reserved for this World's chunks, in bytes.</summary>
    public long ReservedBytes => Allocator.ReservedBytes;

    internal SlabAllocator Allocator { get; }

    internal ScratchArena Scratch { get; }

    /// <summary>Archetypes this World has created so far (per World, in creation order; see <see cref="DescribeArchetype"/>).</summary>
    public int ArchetypeCount => _archetypeCount;

    internal Archetype ArchetypeAt(int index) => _archetypes[index];

    /// <summary>
    /// Creates a command buffer bound to this World. Buffers are reusable; their storage comes from the
    /// per-Tick scratch arena, so a buffer must be played back before <see cref="ResetScratch"/>.
    /// </summary>
    public CommandBuffer CreateCommandBuffer()
    {
        ThrowIfDisposed();
        var buffer = new CommandBuffer(this);
        _buffers.Add(buffer);
        return buffer;
    }

    /// <summary>Sync point: applies every command in record order, then clears the buffer.</summary>
    public void Playback(CommandBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfDisposed();
        if (!ReferenceEquals(buffer.World, this))
        {
            throw new ArgumentException("The command buffer belongs to another World.", nameof(buffer));
        }

        // A sync point is its own change epoch: its structural changes are stamped above the wave that just
        // ended, so a change-filtered System that ran in that wave (last run = wave counter) still sees them.
        if (ChangeCounter == _waveCounter)
        {
            ChangeCounter++;
        }

        if (!_inPhaseMerge)
        {
            StructuralEpoch++;
        }

        _playbackOwner = buffer.OwnerSystemId;
        try
        {
            buffer.Playback();
        }
        finally
        {
            _playbackOwner = -1;
        }
    }

    /// <summary>
    /// Counts sync points: each Phase boundary (one merge of every System's buffers) and each host
    /// <see cref="Playback"/> outside a Tick. An op whose Entity died in the current epoch lost a same-Phase
    /// conflict (spec section 4.1); one whose Entity died earlier was stale.
    /// </summary>
    internal uint StructuralEpoch;

    private bool _inPhaseMerge;

    /// <summary>Brackets a Phase boundary so every buffer merged in it shares one epoch.</summary>
    internal void BeginPhaseMerge()
    {
        StructuralEpoch++;
        _inPhaseMerge = true;
    }

    internal void EndPhaseMerge() => _inPhaseMerge = false;

    /// <summary>Records that an op was not applied: counted, and reported on the <see cref="WorldSchema.StructuralFailures"/> queue for the next Phase.</summary>
    private unsafe void ReportFailure(EntityId id, StructuralOp op, int componentId)
    {
        _staleSkipped++;
        var reason = _entities.DiedInEpoch(id, StructuralEpoch) ? StructuralFailureReason.Conflict : StructuralFailureReason.Stale;
        var failure = new StructuralFailure(id, op, componentId < 0 ? default : new ComponentType(componentId), _playbackOwner, reason);
        _resources.Append(0, (byte*)&failure);
    }

    /// <summary>
    /// Sync point at the end of a Tick: rewinds the scratch arena. Every command buffer must have been
    /// played back; in debug mode a pending buffer is an error.
    /// </summary>
    public void ResetScratch()
    {
        ThrowIfDisposed();
        if (DebugValidation)
        {
            foreach (var buffer in _buffers)
            {
                if (!buffer.IsEmpty)
                {
                    throw new InvalidOperationException("A command buffer still holds commands; play it back before resetting the scratch arena.");
                }
            }
        }

        Scratch.Reset();
    }

    public bool IsAlive(EntityId id) => _entities.IsAlive(id);

    public bool Has(EntityId id, ComponentType type)
    {
        Schema.ValidateType(type);
        if (!_entities.IsAlive(id))
        {
            return false;
        }

        int slot = Schema.NonFragmentingSlots[type.Id];
        return slot >= 0
            ? _nonFragmenting.Has(slot, id.Index)
            : _archetypes[_entities.ArchetypeOf(id.Index)].Has(type.Id);
    }

    /// <summary>
    /// A ref to the Entity's Component. Valid until the next structural change (the next playback);
    /// throws when the Entity is dead or lacks the Component.
    /// </summary>
    public ref T Get<T>(EntityId id, ComponentType type) where T : unmanaged
    {
        Schema.ValidateType(type);
        if (Unsafe.SizeOf<T>() != Schema.Sizes[type.Id])
        {
            throw new ArgumentException($"{typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes but Component '{Schema.Describe(type).Name}' is {Schema.Sizes[type.Id]} bytes.", nameof(T));
        }

        if (!_entities.IsAlive(id))
        {
            throw new InvalidOperationException($"{id} is not alive.");
        }

        return ref GetUnchecked<T>(id.Index, type.Id);
    }

    /// <summary>
    /// The World's value of a Resource (spec section 4.1), by reference; stable for the World's lifetime.
    /// Inside a System use <see cref="SystemContext.Resource{T}"/> / <see cref="SystemContext.ReadResource{T}"/>,
    /// which check the declared access.
    /// </summary>
    public unsafe ref T Resource<T>(ResourceType type) where T : unmanaged
    {
        Schema.ValidateResource<T>(type, ResourceKind.Value);
        return ref Unsafe.AsRef<T>(_resources.Pointer(type.Id));
    }

    /// <summary>
    /// The pending events of a typed queue (spec section 4.3), in merge order: System order, then job
    /// order, then record order. Inside a System use <see cref="SystemContext.Events{T}"/>, which checks the
    /// declared access. The span is valid until the next playback or the End Phase boundary.
    /// </summary>
    public unsafe ReadOnlySpan<T> Events<T>(ResourceType queue) where T : unmanaged
    {
        Schema.ValidateResource<T>(queue, ResourceKind.EventQueue);
        return new ReadOnlySpan<T>(_resources.QueuePointer(queue.Id), _resources.Count(queue.Id));
    }

    /// <summary>Clears every event queue: the End Phase boundary does this before the End Phase's buffers merge.</summary>
    internal void ClearEvents() => _resources.ClearAllQueues();

    internal unsafe void AppendEvent(int queue, byte* source) => _resources.Append(queue, source);

    /// <summary>True when the Entity has the Component and its enable bit is set (spec section 4.1). Throws when the Entity is dead or lacks the Component.</summary>
    public bool IsEnabled(EntityId id, ComponentType type)
    {
        Schema.ValidateType(type);
        ThrowIfMissing(id, type);
        int index = id.Index;
        int slot = Schema.NonFragmentingSlots[type.Id];
        if (slot >= 0)
        {
            return _nonFragmenting.IsEnabled(slot, index);
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        return archetype.IsEnabled(_entities.ChunkOf(index), archetype.ColumnOf(type.Id), _entities.RowOf(index));
    }

    /// <summary>
    /// Flips a Component's enable bit (spec section 4.1): the standard way to turn frequent state on and
    /// off. Not a structural change: no archetype move, views and refs stay valid. Call it outside a Tick
    /// or from a serial hook; inside a chunk job use <see cref="ChunkView.SetEnabled"/> on your own chunk.
    /// </summary>
    public void SetEnabled(EntityId id, ComponentType type, bool enabled)
    {
        Schema.ValidateType(type);
        ThrowIfMissing(id, type);
        int index = id.Index;
        int slot = Schema.NonFragmentingSlots[type.Id];
        if (slot >= 0)
        {
            _nonFragmenting.SetEnabled(slot, index, enabled);
            return;
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        archetype.SetEnabled(_entities.ChunkOf(index), archetype.ColumnOf(type.Id), _entities.RowOf(index), enabled);
    }

    private void ThrowIfMissing(EntityId id, ComponentType type)
    {
        if (!_entities.IsAlive(id))
        {
            throw new InvalidOperationException($"{id} is not alive.");
        }

        if (!Has(id, type))
        {
            throw new InvalidOperationException($"{id} has no Component '{Schema.Components[type.Id].Name}'.");
        }
    }

    /// <summary>
    /// Chunk-level iteration over every archetype that has all of <paramref name="all"/> and none of
    /// <paramref name="none"/>. Iteration respects enable bits: a chunk in which some queried Component is
    /// disabled on every row is skipped, unless <paramref name="ignoreEnableBits"/>.
    /// </summary>
    public ChunkQuery Query(ComponentMask all, ComponentMask none = default, bool ignoreEnableBits = false)
        => new(_archetypes, _archetypeCount, all, none, DebugValidation, ignoreEnableBits);

    public ChunkQuery Query(params ReadOnlySpan<ComponentType> all) => Query(ComponentMask.Of(all));

    private unsafe ref T GetUnchecked<T>(int index, int componentId) where T : unmanaged
    {
        int slot = Schema.NonFragmentingSlots[componentId];
        if (slot >= 0)
        {
            if (!_nonFragmenting.Has(slot, index))
            {
                throw new InvalidOperationException($"Entity slot {index} has no Component '{Schema.Components[componentId].Name}'.");
            }

            return ref Unsafe.AsRef<T>(_nonFragmenting.Pointer(slot, index));
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        int offset = archetype.OffsetOf(componentId);
        if (offset < 0)
        {
            throw new InvalidOperationException($"Entity slot {index} has no Component '{Schema.Components[componentId].Name}'.");
        }

        byte* pointer = archetype.ChunkPointer(_entities.ChunkOf(index)) + offset + (long)_entities.RowOf(index) * Unsafe.SizeOf<T>();
        return ref Unsafe.AsRef<T>(pointer);
    }

    // ---- Structural operations: called only from command playback (the sync point). ----

    internal unsafe EntityId CreateEntity(ComponentMask signature)
    {
        var archetype = GetOrCreateArchetype(signature);
        var (chunk, row) = archetype.AddRow(Allocator);
        archetype.ClearRow(chunk, row);
        archetype.MarkChanged(chunk, ChangeCounter);
        var id = _entities.Allocate(archetype.Id, chunk, row);
        _nonFragmenting.EnsureCapacity(_entities.Capacity);
        _relations.EnsureCapacity(_entities.Capacity);
        ((ulong*)archetype.ChunkPointer(chunk))[row] = id.Value;
        _creates++;
        return id;
    }

    /// <summary>
    /// Destroys an Entity and, through cascading Relations (<see cref="RelationOptions.CascadeDelete"/>),
    /// its descendants: an explicit work list, no recursion, no allocation once it has grown.
    /// </summary>
    internal bool DestroyEntity(EntityId id)
    {
        if (!_entities.IsAlive(id))
        {
            ReportFailure(id, StructuralOp.Destroy, -1);
            return false;
        }

        DestroyCore(id.Index);
        while (_cascadeCount > 0)
        {
            var next = _cascade[--_cascadeCount];
            if (_entities.IsAlive(next))
            {
                DestroyCore(next.Index);
            }
        }

        return true;
    }

    private void DestroyCore(int index)
    {
        if (Schema.RelationCount > 0)
        {
            DetachRelations(index);
        }

        if (Schema.HasBufferComponents)
        {
            FreeBuffers(index);
        }

        RemoveFromArchetype(_archetypes[_entities.ArchetypeOf(index)], _entities.ChunkOf(index), _entities.RowOf(index));
        _nonFragmenting.ClearEntity(index);
        _entities.Free(index, StructuralEpoch);
        _destroys++;
    }

    /// <summary>Adds (or overwrites) a Component; <paramref name="source"/> null means zero-initialised.</summary>
    internal unsafe bool AddComponent(EntityId id, int componentId, byte* source)
    {
        if (Schema.RelationOfComponent[componentId] >= 0)
        {
            return Relate(id, componentId, source is null ? EntityId.Null : Unsafe.ReadUnaligned<RelationLink>(source).Target);
        }

        if (!_entities.IsAlive(id))
        {
            ReportFailure(id, StructuralOp.Add, componentId);
            return false;
        }

        int index = id.Index;
        int slot = Schema.NonFragmentingSlots[componentId];
        bool isBuffer = Schema.Components[componentId].IsBuffer;
        if (slot >= 0)
        {
            if (isBuffer && _nonFragmenting.Has(slot, index))
            {
                FreeBuffer(_nonFragmenting.Pointer(slot, index));
            }

            _nonFragmenting.Set(slot, index, source);
            _adds++;
            return true;
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        if (!archetype.Has(componentId))
        {
            archetype = MoveTo(index, archetype, GetOrCreateArchetype(archetype.Signature.With(new ComponentType(componentId))));
        }
        else if (isBuffer)
        {
            FreeBuffer(archetype.ChunkPointer(_entities.ChunkOf(index)) + archetype.OffsetOf(componentId) + (long)_entities.RowOf(index) * Schema.Sizes[componentId]);
        }

        int size = Schema.Sizes[componentId];
        archetype.MarkChanged(_entities.ChunkOf(index), ChangeCounter);
        byte* destination = archetype.ChunkPointer(_entities.ChunkOf(index)) + archetype.OffsetOf(componentId) + (long)_entities.RowOf(index) * size;
        if (source is null)
        {
            NativeMemory.Clear(destination, (nuint)size);
        }
        else
        {
            Buffer.MemoryCopy(source, destination, size, size);
        }

        _adds++;
        return true;
    }

    internal unsafe bool RemoveComponent(EntityId id, int componentId)
    {
        if (!_entities.IsAlive(id))
        {
            ReportFailure(id, StructuralOp.Remove, componentId);
            return false;
        }

        int index = id.Index;
        int slot = Schema.NonFragmentingSlots[componentId];
        if (slot >= 0)
        {
            int relation = Schema.RelationOfComponent[componentId];
            if (relation >= 0)
            {
                UnlinkOutgoing(relation, slot, index);
            }

            if (Schema.Components[componentId].IsBuffer && _nonFragmenting.Has(slot, index))
            {
                FreeBuffer(_nonFragmenting.Pointer(slot, index));
            }

            _nonFragmenting.Remove(slot, index);
        }
        else
        {
            var archetype = _archetypes[_entities.ArchetypeOf(index)];
            if (archetype.Has(componentId))
            {
                if (Schema.Components[componentId].IsBuffer)
                {
                    FreeBuffer(archetype.ChunkPointer(_entities.ChunkOf(index)) + archetype.OffsetOf(componentId) + (long)_entities.RowOf(index) * Schema.Sizes[componentId]);
                }

                MoveTo(index, archetype, GetOrCreateArchetype(archetype.Signature.Without(new ComponentType(componentId))));
            }
        }

        _removes++;
        return true;
    }

    private unsafe Archetype MoveTo(int index, Archetype source, Archetype destination)
    {
        int sourceChunk = _entities.ChunkOf(index);
        int sourceRow = _entities.RowOf(index);
        var (chunk, row) = destination.AddRow(Allocator);
        destination.MarkChanged(chunk, ChangeCounter);
        byte* to = destination.ChunkPointer(chunk);
        byte* from = source.ChunkPointer(sourceChunk);
        ((ulong*)to)[row] = ((ulong*)from)[sourceRow];
        for (int i = 0; i < destination.ComponentIds.Length; i++)
        {
            int size = destination.Sizes[i];
            byte* target = to + destination.Offsets[i] + (long)row * size;
            int sourceOffset = source.OffsetOf(destination.ComponentIds[i]);
            if (sourceOffset >= 0)
            {
                Buffer.MemoryCopy(from + sourceOffset + (long)sourceRow * size, target, size, size);
                destination.SetEnabled(chunk, i, row, source.IsEnabled(sourceChunk, source.ColumnOf(destination.ComponentIds[i]), sourceRow));
            }
            else
            {
                NativeMemory.Clear(target, (nuint)size);
            }
        }

        RemoveFromArchetype(source, sourceChunk, sourceRow);
        _entities.Move(index, destination.Id, chunk, row);
        return destination;
    }

    private void RemoveFromArchetype(Archetype archetype, int chunk, int row)
    {
        int lastChunk = archetype.ChunkCount - 1;
        var moved = archetype.RemoveRow(chunk, row, Allocator);
        if (!moved.IsNull)
        {
            _entities.Relocate(moved.Index, chunk, row);
        }

        if (chunk < archetype.ChunkCount)
        {
            archetype.MarkChanged(chunk, ChangeCounter);
        }

        if (lastChunk != chunk && lastChunk < archetype.ChunkCount)
        {
            archetype.MarkChanged(lastChunk, ChangeCounter);
        }
    }

    private Archetype GetOrCreateArchetype(ComponentMask signature)
    {
        if (_bySignature.TryGetValue(signature, out var archetype))
        {
            return archetype;
        }

        archetype = new Archetype(_archetypeCount, signature, Schema, _bufferHeap);
        if (_archetypeCount == _archetypes.Length)
        {
            Array.Resize(ref _archetypes, _archetypes.Length * 2);
        }

        _archetypes[_archetypeCount++] = archetype;
        _bySignature.Add(signature, archetype);
        return archetype;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Releases every chunk and slab. In debug mode, native memory still rented when the archetypes
    /// and arena have been torn down is a leak: it goes to <see cref="WorldOptions.DebugListener"/>, or
    /// throws when there is none.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runner?.Dispose();
        foreach (var extension in _extensions)
        {
            extension.Dispose();
        }

        for (int i = 0; i < _archetypeCount; i++)
        {
            _archetypes[i].ReturnAllChunks(Allocator);
        }

        Scratch.Dispose();
        _bufferHeap.Dispose();
        Strings.Dispose();
        Blobs.Dispose();
        _nonFragmenting.Dispose();
        _resources.Dispose();
        var report = new LeakReport(Allocator.ChunksInUse, (long)Allocator.ChunksInUse * SlabAllocator.ChunkSize);
        Allocator.Dispose();
        if (DebugValidation && report.HasLeaks)
        {
            if (_options.DebugListener is { } listener)
            {
                listener.OnLeak(report);
            }
            else
            {
                throw new InvalidOperationException($"World disposed with {report.LeakedChunks} chunk(s) ({report.LeakedBytes} bytes) still rented.");
            }
        }
    }
}
