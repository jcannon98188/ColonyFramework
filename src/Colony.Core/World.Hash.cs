using Colony.Hashing;

namespace Colony;

public sealed partial class World
{
    /// <summary>Seed of the World hash ("WHSH").</summary>
    private const ulong WorldHashSeed = 0x5748_5348UL;

    /// <summary>
    /// The World hash (spec section 6): <see cref="Hash64"/> over the schema hash, the Tick, every
    /// archetype's chunks in archetype id then chunk order (the Entity column and each Component column
    /// over the live rows only, each with its enable bits), the non-fragmenting Components of every live
    /// Entity in slot order (buffer Components by length and elements, never by heap handle), the persistent
    /// Resources in id order, the string table and the blob store in id order, every extension's state in
    /// registration order (<see cref="IWorldExtension.Hash"/>), and the RNG state. Padding rows, change versions,
    /// the asleep bit, transient Resources (including non-persistent event queues), the rebuildable relation
    /// index and caches are excluded. Linear in the
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
            ref readonly var descriptor = ref Schema.Components[id];
            for (int index = 0; index < _entities.HighWater; index++)
            {
                if (_entities.IsAliveSlot(index) && _nonFragmenting.Has(slot, index))
                {
                    state.Append(index);
                    if (descriptor.IsBuffer)
                    {
                        HashBuffer(ref state, _nonFragmenting.Pointer(slot, index), in descriptor);
                    }
                    else
                    {
                        state.Append(new ReadOnlySpan<byte>(_nonFragmenting.Pointer(slot, index), size));
                    }

                    state.Append(_nonFragmenting.IsEnabled(slot, index) ? (byte)1 : (byte)0);
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

        Strings.Hash(ref state);
        Blobs.Hash(ref state);

        foreach (var extension in _extensions)
        {
            extension.Hash(ref state);
        }

        state.Append(_sharedRandom.Stream.Seed);
        state.Append(_sharedRandom.Stream.Key);
        state.Append(_sharedRandom.Counter);
        return state.Finish();
    }

    /// <summary>Slots per leaf of a non-fragmenting Component in the hash tree.</summary>
    private const int SlotBlockSize = 4096;

    /// <summary>
    /// The hierarchical World hash (spec section 10.2, desync localisation): the root is <see cref="Hash"/>, below it
    /// the groups <c>tick</c>, <c>components</c> (the Entity column and every Component, one leaf per archetype chunk;
    /// non-fragmenting Components one leaf per 4096-slot block that holds any), <c>resources</c> (persistent ones),
    /// <c>tables</c> (<c>strings</c>, <c>blobs</c>), one group per extension in registration order
    /// (<see cref="IWorldExtension.HashTree"/>; Fields add one leaf per Field chunk) and <c>random</c>. Every leaf
    /// covers exactly the bytes <see cref="Hash"/> covers for that chunk, so the tree is as deterministic as the World
    /// hash (equal on 1 or N workers). Built on demand at a Tick boundary; it allocates the tree, so it is a
    /// diagnostic, not a per-Tick path. Two trees compare with <see cref="WorldHashTree.Compare"/>.
    /// </summary>
    public unsafe WorldHashTree HashTree()
    {
        ThrowIfDisposed();
        var builder = new HashTreeBuilder();
        builder.AddGroup(WorldHashTree.TickGroup, (ulong)CurrentTick);

        builder.BeginGroup(WorldHashTree.ComponentsGroup);
        builder.BeginItem(WorldHashTree.EntitiesItem, HashTreeLeafKind.ArchetypeChunk);
        for (int a = 0; a < _archetypeCount; a++)
        {
            var archetype = _archetypes[a];
            for (int c = 0; c < archetype.ChunkCount; c++)
            {
                int rows = archetype.RowCount(c);
                var state = new Hash64State(WorldHashTree.NodeSeed);
                state.Append(rows);
                state.Append(new ReadOnlySpan<byte>(archetype.ChunkPointer(c), rows * Archetype.EntityColumnElementSize));
                builder.AddLeaf(c, a, c, -1, state.Finish());
            }
        }

        builder.EndItem();

        for (int id = 0; id < Schema.ComponentCount; id++)
        {
            ref readonly var descriptor = ref Schema.Components[id];
            int slot = Schema.NonFragmentingSlots[id];
            if (slot >= 0)
            {
                AddNonFragmentingLeaves(builder, in descriptor, slot);
                continue;
            }

            builder.BeginItem(descriptor.Name, HashTreeLeafKind.ArchetypeChunk);
            for (int a = 0; a < _archetypeCount; a++)
            {
                var archetype = _archetypes[a];
                if (!archetype.Has(id))
                {
                    continue;
                }

                int column = Array.IndexOf(archetype.ComponentIds, id);
                for (int c = 0; c < archetype.ChunkCount; c++)
                {
                    int rows = archetype.RowCount(c);
                    byte* chunk = archetype.ChunkPointer(c);
                    var state = new Hash64State(WorldHashTree.NodeSeed);
                    state.Append(rows);
                    AppendColumn(ref state, archetype, chunk, column, rows, in descriptor);
                    builder.AddLeaf(c, a, c, -1, state.Finish());
                }
            }

            builder.EndItem();
        }

        builder.EndGroup();

        builder.BeginGroup(WorldHashTree.ResourcesGroup);
        for (int id = 0; id < Schema.ResourceCount; id++)
        {
            var descriptor = Schema.Resources[id];
            if (!descriptor.IsPersistent)
            {
                continue;
            }

            var state = new Hash64State(WorldHashTree.NodeSeed);
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

            builder.AddItem(descriptor.Name, state.Finish());
        }

        builder.EndGroup();

        builder.BeginGroup(WorldHashTree.TablesGroup);
        var strings = new Hash64State(WorldHashTree.NodeSeed);
        Strings.Hash(ref strings);
        builder.AddItem("strings", strings.Finish());
        var blobs = new Hash64State(WorldHashTree.NodeSeed);
        Blobs.Hash(ref blobs);
        builder.AddItem("blobs", blobs.Finish());
        builder.EndGroup();

        for (int i = 0; i < _extensions.Length; i++)
        {
            builder.BeginGroup(Schema.Extensions[i].Name);
            _extensions[i].HashTree(builder);
            builder.EndGroup();
        }

        builder.BeginGroup(WorldHashTree.RandomGroup);
        var random = new Hash64State(WorldHashTree.NodeSeed);
        random.Append(_sharedRandom.Stream.Seed);
        random.Append(_sharedRandom.Stream.Key);
        random.Append(_sharedRandom.Counter);
        builder.AddItem("shared", random.Finish());
        builder.EndGroup();

        return builder.Build(CurrentTick, Schema.SchemaHash, Hash());
    }

    private unsafe void AddNonFragmentingLeaves(HashTreeBuilder builder, in ComponentDescriptor descriptor, int slot)
    {
        builder.BeginItem(descriptor.Name, HashTreeLeafKind.SlotBlock);
        int size = descriptor.Size;
        for (int start = 0; start < _entities.HighWater; start += SlotBlockSize)
        {
            int end = Math.Min(start + SlotBlockSize, _entities.HighWater);
            var state = new Hash64State(WorldHashTree.NodeSeed);
            bool any = false;
            for (int index = start; index < end; index++)
            {
                if (_entities.IsAliveSlot(index) && _nonFragmenting.Has(slot, index))
                {
                    any = true;
                    state.Append(index);
                    if (descriptor.IsBuffer)
                    {
                        HashBuffer(ref state, _nonFragmenting.Pointer(slot, index), in descriptor);
                    }
                    else
                    {
                        state.Append(new ReadOnlySpan<byte>(_nonFragmenting.Pointer(slot, index), size));
                    }

                    state.Append(_nonFragmenting.IsEnabled(slot, index) ? (byte)1 : (byte)0);
                }
            }

            if (any)
            {
                builder.AddLeaf(start / SlotBlockSize, start, start + SlotBlockSize - 1, -1, state.Finish());
            }
        }

        builder.EndItem();
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

    private unsafe void AppendArchetype(ref Hash64State state, Archetype archetype)
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
                AppendColumn(ref state, archetype, chunk, i, rows, in Schema.Components[archetype.ComponentIds[i]]);
            }
        }
    }

    /// <summary>One Component column of one chunk as the World hash sees it: the live rows' bytes (buffers logically) then the enable bits.</summary>
    private unsafe void AppendColumn(ref Hash64State state, Archetype archetype, byte* chunk, int column, int rows, in ComponentDescriptor descriptor)
    {
        if (descriptor.IsBuffer)
        {
            // Logical content only: heap handles depend on thread timing and never reach the hash.
            for (int row = 0; row < rows; row++)
            {
                HashBuffer(ref state, chunk + archetype.Offsets[column] + (long)row * descriptor.Size, in descriptor);
            }
        }
        else
        {
            state.Append(new ReadOnlySpan<byte>(chunk + archetype.Offsets[column], rows * archetype.Sizes[column]));
        }

        state.Append(new ReadOnlySpan<byte>(chunk + archetype.EnabledOffsets[column], (rows + 7) / 8));
    }
}
