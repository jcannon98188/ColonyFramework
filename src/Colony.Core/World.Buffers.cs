using System.Runtime.CompilerServices;

namespace Colony;

public sealed partial class World
{
    private readonly BufferHeap _bufferHeap;

    /// <summary>Running totals of the buffer heap: overflows, grows, frees and what is in use (spec section 4.1 diagnostics).</summary>
    public BufferHeapCounters BufferCounters => _bufferHeap.Counters;

    /// <summary>
    /// The buffer Component (<see cref="WorldBuilder.AddBufferComponent{TElement, TInline}"/>) of an Entity.
    /// Valid until the next structural change; throws when the Entity is dead, lacks the Component, or the
    /// Component is not a buffer of <typeparamref name="TElement"/>. Outside a Tick or from a serial hook;
    /// inside a chunk job use <see cref="ChunkView.Buffer{TElement}"/>.
    /// </summary>
    public unsafe BufferAccessor<TElement> GetBuffer<TElement>(EntityId id, ComponentType type) where TElement : unmanaged
    {
        Schema.ValidateType(type);
        var descriptor = Schema.Components[type.Id];
        if (!descriptor.IsBuffer || descriptor.ElementSize != Unsafe.SizeOf<TElement>())
        {
            throw new ArgumentException($"Component '{descriptor.Name}' is not a buffer of {Unsafe.SizeOf<TElement>()} byte elements.", nameof(type));
        }

        ThrowIfMissing(id, type);
        byte* component = ComponentPointer(id.Index, type.Id);
        return new BufferAccessor<TElement>(component, descriptor.InlineOffset, descriptor.InlineCapacity, _bufferHeap);
    }

    private unsafe byte* ComponentPointer(int index, int componentId)
    {
        int slot = Schema.NonFragmentingSlots[componentId];
        if (slot >= 0)
        {
            return _nonFragmenting.Pointer(slot, index);
        }

        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        return archetype.ChunkPointer(_entities.ChunkOf(index)) + archetype.OffsetOf(componentId) + (long)_entities.RowOf(index) * Schema.Sizes[componentId];
    }

    /// <summary>Releases the overflow block of one buffer Component value (before it is overwritten or dropped).</summary>
    private unsafe void FreeBuffer(byte* component)
    {
        var header = (BufferHeader*)component;
        _bufferHeap.Free(header->Handle);
        header->Handle = 0;
    }

    /// <summary>Releases every overflow block a dying Entity owns.</summary>
    private unsafe void FreeBuffers(int index)
    {
        var archetype = _archetypes[_entities.ArchetypeOf(index)];
        archetype.FreeBuffers(_entities.ChunkOf(index), _entities.RowOf(index));
        for (int id = 0; id < Schema.ComponentCount; id++)
        {
            int slot = Schema.NonFragmentingSlots[id];
            if (slot >= 0 && Schema.Components[id].IsBuffer && _nonFragmenting.Has(slot, index))
            {
                FreeBuffer(_nonFragmenting.Pointer(slot, index));
            }
        }
    }

    /// <summary>Hashes a buffer Component logically: its length then its elements, wherever they live.</summary>
    private unsafe void HashBuffer(ref Hashing.Hash64State state, byte* component, in ComponentDescriptor descriptor)
    {
        var header = (BufferHeader*)component;
        state.Append(header->Length);
        byte* data = header->Handle == 0 ? component + descriptor.InlineOffset : _bufferHeap.Pointer(header->Handle);
        state.Append(new ReadOnlySpan<byte>(data, header->Length * descriptor.ElementSize));
    }
}
