using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// The complete simulated state of one game: its Entities and their Components, stored in archetype
/// chunks carved from native slabs (ADR 0001, spec section 4). Structural change (create, destroy, add
/// or remove a Component) goes only through <see cref="CommandBuffer"/>s played back at a sync point
/// with <see cref="Playback"/>; between sync points refs and chunk views stay valid.
/// Many Worlds can coexist in a process; nothing here is static.
/// </summary>
public sealed class World : IDisposable
{
    private readonly WorldOptions _options;
    private readonly EntityIndex _entities;
    private readonly NonFragmentingStore _nonFragmenting;
    private readonly Dictionary<ComponentMask, Archetype> _bySignature = [];
    private readonly List<CommandBuffer> _buffers = [];
    private Archetype[] _archetypes = new Archetype[8];
    private int _archetypeCount;
    private long _creates;
    private long _destroys;
    private long _adds;
    private long _removes;
    private long _staleSkipped;
    private bool _disposed;

    internal World(WorldSchema schema, WorldOptions options)
    {
        Schema = schema;
        _options = options;
        DebugValidation = schema.DebugValidation;
        Allocator = new SlabAllocator(poison: DebugValidation);
        Scratch = new ScratchArena(Allocator, poison: DebugValidation);
        _entities = new EntityIndex(options.InitialEntityCapacity);
        _nonFragmenting = new NonFragmentingStore(schema, _entities.Capacity);
    }

    public WorldSchema Schema { get; }

    /// <summary>True when this World validates refs and views, poisons freed memory and reports leaks.</summary>
    public bool DebugValidation { get; }

    public int EntityCount => _entities.AliveCount;

    public StructuralCounters Counters => new(_creates, _destroys, _adds, _removes, _staleSkipped);

    /// <summary>Native memory reserved for this World's chunks, in bytes.</summary>
    public long ReservedBytes => Allocator.ReservedBytes;

    internal SlabAllocator Allocator { get; }

    internal ScratchArena Scratch { get; }

    internal int ArchetypeCount => _archetypeCount;

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

        buffer.Playback();
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

    /// <summary>Chunk-level iteration over every archetype that has all of <paramref name="all"/> and none of <paramref name="none"/>.</summary>
    public ChunkQuery Query(ComponentMask all, ComponentMask none = default)
        => new(_archetypes, _archetypeCount, all, none, DebugValidation);

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
        var id = _entities.Allocate(archetype.Id, chunk, row);
        _nonFragmenting.EnsureCapacity(_entities.Capacity);
        ((ulong*)archetype.ChunkPointer(chunk))[row] = id.Value;
        _creates++;
        return id;
    }

    internal bool DestroyEntity(EntityId id)
    {
        if (!_entities.IsAlive(id))
        {
            _staleSkipped++;
            return false;
        }

        int index = id.Index;
        RemoveFromArchetype(_archetypes[_entities.ArchetypeOf(index)], _entities.ChunkOf(index), _entities.RowOf(index));
        _nonFragmenting.ClearEntity(index);
        _entities.Free(index);
        _destroys++;
        return true;
    }

    /// <summary>Adds (or overwrites) a Component; <paramref name="source"/> null means zero-initialised.</summary>
    internal unsafe bool AddComponent(EntityId id, int componentId, byte* source)
    {
        if (!_entities.IsAlive(id))
        {
            _staleSkipped++;
            return false;
        }

        int index = id.Index;
        int slot = Schema.NonFragmentingSlots[componentId];
        if (slot >= 0)
        {
            _nonFragmenting.Set(slot, index, source);
            _adds++;
            return true;
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        if (!archetype.Has(componentId))
        {
            archetype = MoveTo(index, archetype, GetOrCreateArchetype(archetype.Signature.With(new ComponentType(componentId))));
        }

        int size = Schema.Sizes[componentId];
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

    internal bool RemoveComponent(EntityId id, int componentId)
    {
        if (!_entities.IsAlive(id))
        {
            _staleSkipped++;
            return false;
        }

        int index = id.Index;
        int slot = Schema.NonFragmentingSlots[componentId];
        if (slot >= 0)
        {
            _nonFragmenting.Remove(slot, index);
        }
        else
        {
            var archetype = _archetypes[_entities.ArchetypeOf(index)];
            if (archetype.Has(componentId))
            {
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
        var moved = archetype.RemoveRow(chunk, row, Allocator);
        if (!moved.IsNull)
        {
            _entities.Relocate(moved.Index, chunk, row);
        }
    }

    private Archetype GetOrCreateArchetype(ComponentMask signature)
    {
        if (_bySignature.TryGetValue(signature, out var archetype))
        {
            return archetype;
        }

        archetype = new Archetype(_archetypeCount, signature, Schema);
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
        for (int i = 0; i < _archetypeCount; i++)
        {
            _archetypes[i].ReturnAllChunks(Allocator);
        }

        Scratch.Dispose();
        _nonFragmenting.Dispose();
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
