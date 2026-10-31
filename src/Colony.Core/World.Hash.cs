using Colony.Hashing;

namespace Colony;

public sealed partial class World
{
    /// <summary>Seed of the World hash ("WHSH").</summary>
    private const ulong WorldHashSeed = 0x5748_5348UL;

    /// <summary>
    /// The World hash (spec section 6): <see cref="Hash64"/> over the schema hash, the Tick, every
    /// archetype's chunks in archetype id then chunk order (the Entity column and each Component column
    /// over the live rows only), the non-fragmenting Components of every live Entity in slot order, the
    /// persistent Resources in id order and the RNG state. Padding rows, change versions, the asleep bit,
    /// transient Resources (including non-persistent event queues) and caches are excluded. Linear in the
    /// live data, so it is cheap enough to run every few Ticks in a test. Equal on 1 or N threads, JIT or
    /// Native AOT, for the same seed and inputs (Tier 1 determinism, ADR 0002).
    /// </summary>
    public unsafe ulong Hash()
    {
        ThrowIfDisposed();
        var state = new Hash64State(WorldHashSeed);
        state.Append(Schema.SchemaHash);
        state.Append(CurrentTick);

        for (int a = 0; a < _archetypeCount; a++)
        {
            var archetype = _archetypes[a];
            if (archetype.ChunkCount == 0)
            {
                continue;
            }

            var signature = archetype.Signature;
            state.Append(in signature);
            for (int c = 0; c < archetype.ChunkCount; c++)
            {
                int rows = archetype.RowCount(c);
                byte* chunk = archetype.ChunkPointer(c);
                state.Append(rows);
                state.Append(new ReadOnlySpan<byte>(chunk, rows * Archetype.EntityColumnElementSize));
                for (int i = 0; i < archetype.ComponentIds.Length; i++)
                {
                    state.Append(new ReadOnlySpan<byte>(chunk + archetype.Offsets[i], rows * archetype.Sizes[i]));
                }
            }
        }

        for (int id = 0; id < Schema.ComponentCount; id++)
        {
            int slot = Schema.NonFragmentingSlots[id];
            if (slot < 0)
            {
                continue;
            }

            int size = Schema.Sizes[id];
            for (int index = 0; index < _entities.HighWater; index++)
            {
                if (_entities.IsAliveSlot(index) && _nonFragmenting.Has(slot, index))
                {
                    state.Append(index);
                    state.Append(new ReadOnlySpan<byte>(_nonFragmenting.Pointer(slot, index), size));
                }
            }
        }

        for (int id = 0; id < Schema.ResourceCount; id++)
        {
            var descriptor = Schema.Resources[id];
            if (!descriptor.IsPersistent)
            {
                continue;
            }

            if (descriptor.Kind == ResourceKind.Value)
            {
                state.Append(new ReadOnlySpan<byte>(_resources.Pointer(id), descriptor.Size));
            }
            else
            {
                int count = _resources.Count(id);
                state.Append(count);
                state.Append(new ReadOnlySpan<byte>(_resources.QueuePointer(id), count * descriptor.Size));
            }
        }

        state.Append(_sharedRandom.Stream.Seed);
        state.Append(_sharedRandom.Stream.Key);
        state.Append(_sharedRandom.Counter);
        return state.Finish();
    }
}
