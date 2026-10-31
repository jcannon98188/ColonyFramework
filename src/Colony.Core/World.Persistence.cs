using Colony.Numerics;

namespace Colony;

/// <summary>
/// Additive save/load seams for Colony.Persistence (spec section 7.1): internal readers over the storage
/// the World hash covers and writers that restore it into a fresh World. Nothing here is a public API;
/// the public surface is Colony.Persistence's <c>world.Save(...)</c> / <c>Load(...)</c>.
/// </summary>
public sealed partial class World
{
    internal EntityIndex EntityIndex => _entities;

    internal NonFragmentingStore NonFragmentingStore => _nonFragmenting;

    internal ResourceStore ResourceStore => _resources;

    internal BufferHeap BufferHeap => _bufferHeap;

    /// <summary>True until the first Entity, Tick or archetype: the only state a load may populate.</summary>
    internal bool IsFresh => CurrentTick == 0 && _archetypeCount == 0 && _entities.HighWater == 0 && _creates == 0 && Strings.Count == 0 && Blobs.Count == 0;

    /// <summary>Restores the saved Entity index (every slot dead); the archetype loader revives the live slots.</summary>
    internal void RestoreEntityIndex(int highWater, ReadOnlySpan<uint> generations, ReadOnlySpan<uint> freedEpochs, ReadOnlySpan<int> freeSlots)
    {
        _entities.Restore(highWater, generations, freedEpochs, freeSlots);
        _nonFragmenting.EnsureCapacity(_entities.Capacity);
        _relations.EnsureCapacity(_entities.Capacity);
    }

    /// <summary>Creates the next archetype (ids are creation order, so the loader calls this in saved id order).</summary>
    internal Archetype RestoreArchetype(ComponentMask signature)
    {
        if (_bySignature.ContainsKey(signature))
        {
            throw new InvalidOperationException("The save lists the same archetype signature twice.");
        }

        return GetOrCreateArchetype(signature);
    }

    internal void RestoreTick(long tick) => CurrentTick = tick;

    internal void RestoreSharedRandom(ulong counter) => _sharedRandom = new RandomSequence(_sharedRandom.Stream, counter);

    /// <summary>Restores the structural counters and epochs, so failure reasons and index-driven consumers continue as the saved World would.</summary>
    internal void RestoreCounters(StructuralCounters counters, uint structuralEpoch, uint changeCounter)
    {
        _creates = counters.Creates;
        _destroys = counters.Destroys;
        _adds = counters.Adds;
        _removes = counters.Removes;
        _staleSkipped = counters.StaleSkipped;
        StructuralEpoch = structuralEpoch;
        ChangeCounter = changeCounter;
    }

    /// <summary>Stamps every chunk as changed in the current epoch, so change-filtered Systems revisit everything after a load (scheduler caches are rebuilt, not saved).</summary>
    internal void MarkAllChunksChanged()
    {
        ChangeCounter++;
        for (int a = 0; a < _archetypeCount; a++)
        {
            var archetype = _archetypes[a];
            for (int c = 0; c < archetype.ChunkCount; c++)
            {
                archetype.MarkChanged(c, ChangeCounter);
            }
        }
    }

    /// <summary>Calls every extension's <see cref="IWorldExtension.Save"/> in registration order.</summary>
    internal void SaveExtensions(Func<string, ExtensionStateWriter> writerFor)
    {
        for (int i = 0; i < _extensions.Length; i++)
        {
            _extensions[i].Save(writerFor(Schema.Extensions[i].Name));
        }
    }

    /// <summary>Calls every extension's <see cref="IWorldExtension.Load"/> in registration order.</summary>
    internal void LoadExtensions(Func<string, ExtensionStateReader> readerFor)
    {
        for (int i = 0; i < _extensions.Length; i++)
        {
            _extensions[i].Load(readerFor(Schema.Extensions[i].Name));
        }
    }
}
