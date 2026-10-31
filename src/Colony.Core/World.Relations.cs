using System.Runtime.CompilerServices;

namespace Colony;

public sealed partial class World
{
    private readonly RelationIndex _relations;
    private EntityId[] _cascade = new EntityId[64];
    private int _cascadeCount;

    /// <summary>The rebuildable reverse index, exposed so tests can corrupt it and prove the hash ignores it.</summary>
    internal RelationIndex RelationIndexForTests => _relations;

    /// <summary>
    /// The Entity <paramref name="source"/> relates to in <paramref name="relation"/>, or
    /// <see cref="EntityId.Null"/> when it is dead or has no link.
    /// </summary>
    public EntityId TargetOf(EntityId source, RelationType relation)
    {
        Schema.ValidateRelation(relation);
        int slot = Schema.NonFragmentingSlots[relation.Component.Id];
        if (!_entities.IsAlive(source) || !_nonFragmenting.Has(slot, source.Index))
        {
            return EntityId.Null;
        }

        return ReadLink(slot, source.Index).Target;
    }

    /// <summary>
    /// Every source currently related to <paramref name="target"/> in <paramref name="relation"/>, in ascending
    /// Entity slot order (deterministic whatever the insertion order); empty for a dead target. Reverse lookup
    /// through the built-in relation index (spec section 4.1); valid until the next structural change and safe
    /// to read from chunk jobs.
    /// </summary>
    public RelationSources SourcesOf(EntityId target, RelationType relation)
    {
        Schema.ValidateRelation(relation);
        return _entities.IsAlive(target) ? new RelationSources(_relations, relation.Id, target.Index) : default;
    }

    /// <summary>The parent of <paramref name="child"/> in the built-in hierarchy, or <see cref="EntityId.Null"/>.</summary>
    public EntityId ParentOf(EntityId child) => TargetOf(child, Schema.Hierarchy);

    /// <summary>The children of <paramref name="parent"/> in the built-in hierarchy, in ascending slot order.</summary>
    public RelationSources ChildrenOf(EntityId parent) => SourcesOf(parent, Schema.Hierarchy);

    /// <summary>
    /// Drops the reverse relation index and recreates it from the Relation Components of every live Entity.
    /// The incremental index is equivalent; this is the oracle and the recovery path (after a load). Call it
    /// outside a Tick or from a serial hook.
    /// </summary>
    public void RebuildRelationIndex()
    {
        ThrowIfDisposed();
        _relations.Clear();
        var relations = Schema.Relations;
        for (int r = 0; r < relations.Length; r++)
        {
            int slot = Schema.NonFragmentingSlots[relations[r].Component.Id];
            for (int index = 0; index < _entities.HighWater; index++)
            {
                if (_entities.IsAliveSlot(index) && _nonFragmenting.Has(slot, index))
                {
                    _relations.Link(r, _entities.IdAt(index), ReadLink(slot, index).Target.Index);
                }
            }
        }
    }

    private unsafe RelationLink ReadLink(int slot, int index) => Unsafe.ReadUnaligned<RelationLink>(_nonFragmenting.Pointer(slot, index));

    /// <summary>Structural op: links <paramref name="source"/> to <paramref name="target"/> in the Relation stored by <paramref name="componentId"/>.</summary>
    internal unsafe bool Relate(EntityId source, int componentId, EntityId target)
    {
        if (!_entities.IsAlive(source))
        {
            ReportFailure(source, StructuralOp.Add, componentId);
            return false;
        }

        if (target.IsNull)
        {
            return RemoveComponent(source, componentId);
        }

        if (!_entities.IsAlive(target))
        {
            ReportFailure(target, StructuralOp.Relate, componentId);
            return false;
        }

        int relation = Schema.RelationOfComponent[componentId];
        int slot = Schema.NonFragmentingSlots[componentId];
        int index = source.Index;
        if (_nonFragmenting.Has(slot, index))
        {
            var previous = ReadLink(slot, index).Target;
            if (previous == target)
            {
                _adds++;
                return true;
            }

            _relations.Unlink(relation, index, previous.Index);
        }

        var link = new RelationLink(target);
        _nonFragmenting.Set(slot, index, (byte*)&link);
        _relations.Link(relation, source, target.Index);
        _adds++;
        return true;
    }

    /// <summary>Removes the source's outgoing link of one Relation from the index (the Component itself is removed by the caller).</summary>
    private void UnlinkOutgoing(int relation, int slot, int index)
    {
        if (_nonFragmenting.Has(slot, index))
        {
            _relations.Unlink(relation, index, ReadLink(slot, index).Target.Index);
        }
    }

    /// <summary>
    /// Detaches a dying Entity from every Relation: its own links leave the index, and every source pointing
    /// at it loses its link, or is queued for destruction when the Relation cascades (spec section 4.1).
    /// </summary>
    private void DetachRelations(int index)
    {
        var relations = Schema.Relations;
        for (int r = 0; r < relations.Length; r++)
        {
            int slot = Schema.NonFragmentingSlots[relations[r].Component.Id];
            UnlinkOutgoing(r, slot, index);
            bool cascade = relations[r].CascadeDelete;
            int source;
            while ((source = _relations.Head(r, index)) >= 0)
            {
                var sourceId = _relations.IdOf(r, source);
                _relations.Unlink(r, source, index);
                _nonFragmenting.Remove(slot, source);
                if (cascade)
                {
                    if (_cascadeCount == _cascade.Length)
                    {
                        Array.Resize(ref _cascade, _cascade.Length * 2);
                    }

                    _cascade[_cascadeCount++] = sourceId;
                }
            }
        }
    }
}
