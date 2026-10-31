using Colony.Hashing;

namespace Colony;

public sealed partial class World
{
    /// <summary>Seed of the World hash ("WHSH").</summary>
    private const ulong WorldHashSeed = 0x5748_5348UL;

    /// <summary>
    /// The World hash (spec section 6): <see cref="Hash64"/> over the schema hash, the Tick, every
    /// archetype's chunks in archetype id then chunk order (the Entity column and each Component column
    /// over the live rows only), the non-fragmenting Components of every live Entity in slot order, and
    /// every extension's state in registration order (<see cref="IWorldExtension.Hash"/>), and the RNG
    /// state. Padding rows, change versions, the asleep bit and caches are excluded. Linear in the
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

            AppendArchetype(ref state, archetype);
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

        foreach (var extension in _extensions)
        {
            extension.Hash(ref state);
        }

        state.Append(_sharedRandom.Stream.Seed);
        state.Append(_sharedRandom.Stream.Key);
        state.Append(_sharedRandom.Counter);
        return state.Finish();
    }

    /// <summary>Hash of one archetype's live rows (its part of <see cref="Hash"/>), for the diagnostics hash trail. Empty archetypes hash to the seed alone.</summary>
    internal ulong HashArchetype(int index)
    {
        var state = new Hash64State(WorldHashSeed);
        var archetype = _archetypes[index];
        if (archetype.ChunkCount > 0)
        {
            AppendArchetype(ref state, archetype);
        }

        return state.Finish();
    }

    private static unsafe void AppendArchetype(ref Hash64State state, Archetype archetype)
    {
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
}
