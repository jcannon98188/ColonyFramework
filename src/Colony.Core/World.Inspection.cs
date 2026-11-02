namespace Colony;

/// <summary>
/// Untyped read seams for tooling (spec section 10.2): the inspection protocol, diff tools and the C ABI
/// identify Components and Resources by their registered name, never by CLR type, so they read raw bytes.
/// Call them at a sync point (between Ticks); the spans are valid until the next structural change.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// The raw bytes of an Entity's Component (exactly <see cref="ComponentDescriptor.Size"/> bytes; for a
    /// buffer Component the header and inline storage). Throws when the Entity is dead or lacks the Component.
    /// </summary>
    public unsafe ReadOnlySpan<byte> ComponentBytes(EntityId id, ComponentType type)
    {
        Schema.ValidateType(type);
        if (!_entities.IsAlive(id))
        {
            throw new InvalidOperationException($"{id} is not alive.");
        }

        int index = id.Index;
        int size = Schema.Sizes[type.Id];
        int slot = Schema.NonFragmentingSlots[type.Id];
        if (slot >= 0)
        {
            if (!_nonFragmenting.Has(slot, index))
            {
                throw new InvalidOperationException($"{id} has no Component '{Schema.Components[type.Id].Name}'.");
            }

            return new ReadOnlySpan<byte>(_nonFragmenting.Pointer(slot, index), size);
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        int offset = archetype.OffsetOf(type.Id);
        if (offset < 0)
        {
            throw new InvalidOperationException($"{id} has no Component '{Schema.Components[type.Id].Name}'.");
        }

        byte* pointer = archetype.ChunkPointer(_entities.ChunkOf(index)) + offset + (long)_entities.RowOf(index) * size;
        return new ReadOnlySpan<byte>(pointer, size);
    }

    /// <summary>The raw bytes of a value Resource (exactly <see cref="ResourceDescriptor.Size"/> bytes). Throws for an event queue.</summary>
    public unsafe ReadOnlySpan<byte> ResourceBytes(ResourceType type)
    {
        Schema.ValidateResource(type);
        var descriptor = Schema.Resources[type.Id];
        if (descriptor.IsEventQueue)
        {
            throw new ArgumentException($"Resource '{descriptor.Name}' is an event queue; read it with EventBytes.", nameof(type));
        }

        return new ReadOnlySpan<byte>(_resources.Pointer(type.Id), descriptor.Size);
    }

    /// <summary>The pending events of a queue as raw bytes: <c>count * Size</c> bytes in merge order. Throws for a value Resource.</summary>
    public unsafe ReadOnlySpan<byte> EventBytes(ResourceType queue)
    {
        Schema.ValidateResource(queue);
        var descriptor = Schema.Resources[queue.Id];
        if (!descriptor.IsEventQueue)
        {
            throw new ArgumentException($"Resource '{descriptor.Name}' is a value; read it with ResourceBytes.", nameof(queue));
        }

        return new ReadOnlySpan<byte>(_resources.QueuePointer(queue.Id), _resources.Count(queue.Id) * descriptor.Size);
    }

    /// <summary>Number of pending events in a queue.</summary>
    public int EventCount(ResourceType queue)
    {
        Schema.ValidateResource(queue);
        return _resources.Count(queue.Id);
    }

    /// <summary>The signature, live Entity count and chunk count of archetype <paramref name="index"/> (archetype ids are per World, in creation order).</summary>
    public (ComponentMask Signature, int EntityCount, int ChunkCount) DescribeArchetype(int index)
    {
        if ((uint)index >= (uint)_archetypeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The World has {_archetypeCount} archetype(s).");
        }

        var archetype = _archetypes[index];
        return (archetype.Signature, archetype.EntityCount, archetype.ChunkCount);
    }

    /// <summary>The Entity ids of every live row of the <paramref name="chunk"/>-th chunk of archetype <paramref name="archetype"/>.</summary>
    public unsafe ReadOnlySpan<EntityId> ArchetypeEntities(int archetype, int chunk)
    {
        var a = _archetypes[archetype];
        if ((uint)chunk >= (uint)a.ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk), chunk, $"Archetype {archetype} has {a.ChunkCount} chunk(s).");
        }

        return new ReadOnlySpan<EntityId>(a.ChunkPointer(chunk), a.RowCount(chunk));
    }
}
