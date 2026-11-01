namespace Colony;

/// <summary>
/// Raw-byte access for the C ABI host in Colony.Plugins (TASK-28): a Native Plugin or C host holds Components
/// and Resources only as bytes of a registered layout. Additive seam; the typed API is unchanged.
/// </summary>
public sealed partial class World
{
    /// <summary>Address of the Entity's Component value; throws when the Entity is dead or lacks it. Valid until the next structural change.</summary>
    internal unsafe byte* ComponentAddress(EntityId id, ComponentType type)
    {
        Schema.ValidateType(type);
        if (!_entities.IsAlive(id))
        {
            throw new InvalidOperationException($"{id} is not alive.");
        }

        int index = id.Index;
        int componentId = type.Id;
        int slot = Schema.NonFragmentingSlots[componentId];
        if (slot >= 0)
        {
            return _nonFragmenting.Has(slot, index)
                ? _nonFragmenting.Pointer(slot, index)
                : throw new InvalidOperationException($"{id} has no Component '{Schema.Components[componentId].Name}'.");
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        int offset = archetype.OffsetOf(componentId);
        if (offset < 0)
        {
            throw new InvalidOperationException($"{id} has no Component '{Schema.Components[componentId].Name}'.");
        }

        return archetype.ChunkPointer(_entities.ChunkOf(index)) + offset + ((long)_entities.RowOf(index) * Schema.Sizes[componentId]);
    }

    /// <summary>Address of a value Resource (stable for the World's lifetime).</summary>
    internal unsafe byte* ResourceAddress(ResourceType type)
    {
        Schema.ValidateResource(type);
        return Schema.Resources[type.Id].IsEventQueue
            ? throw new ArgumentException($"Resource '{Schema.Resources[type.Id].Name}' is an event queue, not a value.", nameof(type))
            : _resources.Pointer(type.Id);
    }

    /// <summary>The pending events of a queue as raw bytes: address and event count (element size is the queue's registered size).</summary>
    internal unsafe byte* EventsAddress(ResourceType queue, out int count)
    {
        Schema.ValidateResource(queue);
        if (!Schema.Resources[queue.Id].IsEventQueue)
        {
            throw new ArgumentException($"Resource '{Schema.Resources[queue.Id].Name}' is a value, not an event queue.", nameof(queue));
        }

        count = _resources.Count(queue.Id);
        return _resources.QueuePointer(queue.Id);
    }
}
