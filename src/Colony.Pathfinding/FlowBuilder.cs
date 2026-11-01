using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>
/// Computes flow Field distances: a multi-source breadth-first search from the goals over the passable tiles of
/// the bound (unit face moves, climbing where the class allows), ties by neighbour index. The incremental
/// repair recomputes an affected chunk set that starts as the dirty chunks and grows by every neighbour whose
/// adjacent face values moved, until nothing moves; the unaffected chunks then see the same boundary values as
/// before, so the result equals a rebuild. It gives up and rebuilds when the set reaches the rebuild threshold.
/// One instance per service, used under the flow lock; allocation free once its buffers have grown.
/// </summary>
internal sealed class FlowBuilder
{
    private long[] _queue = new long[FieldChunk.CellCount * 4];
    private long[] _seeds = new long[256];
    private int _seedCount;
    private byte[] _affected = [];
    private int[] _affectedSlots = [];
    private int _affectedCount;
    private ushort[] _old = [];

    /// <summary>Recomputes every chunk of the bound from scratch; returns the cells popped.</summary>
    public long Rebuild(FlowField field)
    {
        for (int slot = 0; slot < field.ChunkCount; slot++)
        {
            field.DistancesOf(slot).AsSpan().Fill(FlowField.Unreached);
        }

        _seedCount = 0;
        SeedGoals(field, null);
        return Run(field, null);
    }

    /// <summary>
    /// Recomputes the dirty chunks and whatever depends on them; <paramref name="fellBackToRebuild"/> is set when
    /// the affected set reached <paramref name="rebuildThreshold"/> chunks and a plain rebuild ran instead.
    /// </summary>
    public long RepairIncremental(FlowField field, int rebuildThreshold, out bool fellBackToRebuild)
    {
        int count = field.ChunkCount;
        if (_affected.Length < count)
        {
            _affected = new byte[count];
            _affectedSlots = new int[count];
        }

        // Seeds are the goals plus the outside faces of the affected set, which never exceeds the threshold: size once, not as the set grows.
        int seedsNeeded = field.Goals.Length + (6 * FaceCells * Math.Min(rebuildThreshold, count));
        if (_seeds.Length < seedsNeeded)
        {
            _seeds = new long[seedsNeeded];
        }

        Array.Clear(_affected, 0, count);
        _affectedCount = 0;
        for (int slot = 0; slot < count; slot++)
        {
            if (field.IsDirtyChunk(slot))
            {
                Join(field, slot);
            }
        }

        long units = 0;
        while (true)
        {
            if (_affectedCount >= rebuildThreshold)
            {
                fellBackToRebuild = true;
                return units + Rebuild(field);
            }

            _seedCount = 0;
            for (int i = 0; i < _affectedCount; i++)
            {
                field.DistancesOf(_affectedSlots[i]).AsSpan().Fill(FlowField.Unreached);
            }

            SeedGoals(field, _affected);
            for (int i = 0; i < _affectedCount; i++)
            {
                SeedFromOutside(field, _affectedSlots[i]);
            }

            units += Run(field, _affected);

            // Grow by the unaffected neighbours whose view of the set changed; stop when none did.
            int before = _affectedCount;
            for (int i = 0; i < before; i++)
            {
                GrowIfFacesMoved(field, i);
            }

            if (_affectedCount == before)
            {
                fellBackToRebuild = false;
                return units;
            }
        }
    }

    /// <summary>Adds a chunk to the affected set, keeping a copy of its current (old) distances.</summary>
    private void Join(FlowField field, int slot)
    {
        int index = _affectedCount++;
        _affected[slot] = 1;
        _affectedSlots[index] = slot;
        int needed = (index + 1) * FieldChunk.CellCount;
        if (_old.Length < needed)
        {
            Array.Resize(ref _old, Math.Max(needed, _old.Length * 2));
        }

        field.DistancesOf(slot).AsSpan().CopyTo(_old.AsSpan(index * FieldChunk.CellCount, FieldChunk.CellCount));
    }

    private void SeedGoals(FlowField field, byte[]? affected)
    {
        var map = field.Map;
        foreach (var goal in field.Goals)
        {
            if (!field.Bounds.Contains(goal.Chunk))
            {
                continue;
            }

            int slot = field.Bounds.SlotOf(goal.Chunk);
            if (affected is not null && affected[slot] == 0)
            {
                continue;
            }

            int cell = goal.CellIndex;
            if (!ChunkGraph.Bit(map.PassableOf(field.OrdinalOf(slot)), cell))
            {
                continue;
            }

            field.DistancesOf(slot)[cell] = 0;
            PushSeed(0, slot, cell);
        }
    }

    /// <summary>Seeds the face cells of an affected chunk from finite neighbours in unaffected chunks (their value + 1).</summary>
    private void SeedFromOutside(FlowField field, int slot)
    {
        var dist = field.DistancesOf(slot);
        var passable = field.Map.PassableOf(field.OrdinalOf(slot));
        for (int d = 0; d < 6; d++)
        {
            int nSlot = field.NeighbourSlotOf(slot, d);
            if (nSlot < 0 || _affected[nSlot] != 0)
            {
                continue;
            }

            var outside = field.DistancesOf(nSlot);
            for (int cell = 0; cell < FieldChunk.CellCount; cell++)
            {
                if (!OnFace(cell, d) || !ChunkGraph.Bit(passable, cell) || !field.TryNeighbour(slot, cell, d, out _, out int nCell))
                {
                    continue;
                }

                ushort theirs = outside[nCell];
                if (theirs == FlowField.Unreached || theirs + 1 >= FlowField.Unreached)
                {
                    continue;
                }

                int candidate = theirs + 1;
                if (candidate < dist[cell])
                {
                    dist[cell] = (ushort)candidate;
                    PushSeed(candidate, slot, cell);
                }
            }
        }
    }

    /// <summary>
    /// Adds the unaffected face neighbours of the <paramref name="index"/>-th affected chunk whose adjacent face
    /// values differ from the old ones. A horizontal edge changes only with a face cell's passability, which shows
    /// in its value; a climb out of a dirty chunk's top face also depends on that cell's Up bit, which does not,
    /// so the chunk above joins too when one of its cells may have leaned on a climb that is gone.
    /// </summary>
    private void GrowIfFacesMoved(FlowField field, int index)
    {
        int slot = _affectedSlots[index];
        var now = field.DistancesOf(slot);
        var old = _old.AsSpan(index * FieldChunk.CellCount, FieldChunk.CellCount);
        for (int d = 0; d < 6; d++)
        {
            int nSlot = field.NeighbourSlotOf(slot, d);
            if (nSlot < 0 || _affected[nSlot] != 0)
            {
                continue;
            }

            bool join = false;
            for (int cell = 0; cell < FieldChunk.CellCount && !join; cell++)
            {
                join = OnFace(cell, d) && now[cell] != old[cell];
            }

            if (!join && d == 5 && field.IsDirtyChunk(slot))
            {
                join = AClimbMayHaveGone(field, slot, nSlot, old);
            }

            if (join)
            {
                Join(field, nSlot);
            }
        }
    }

    /// <summary>True when a bottom-face cell of the chunk above sits at exactly old + 1 over a top-face cell it can no longer climb down to.</summary>
    private static bool AClimbMayHaveGone(FlowField field, int slot, int above, ReadOnlySpan<ushort> old)
    {
        var theirs = field.DistancesOf(above);
        for (int cell = FieldChunk.CellCount - FaceCells; cell < FieldChunk.CellCount; cell++)
        {
            ushort was = old[cell];
            if (was == FlowField.Unreached || field.TryNeighbour(slot, cell, 5, out _, out _))
            {
                continue;
            }

            if (theirs[cell - (FieldChunk.Mask * FaceCells)] == was + 1)
            {
                return true;
            }
        }

        return false;
    }

    private const int FaceCells = FieldChunk.Size * FieldChunk.Size;

    private static bool OnFace(int cell, int direction)
    {
        int x = cell & FieldChunk.Mask;
        int y = (cell >> FieldChunk.Shift) & FieldChunk.Mask;
        int z = cell >> (2 * FieldChunk.Shift);
        return direction switch
        {
            0 => x == 0,
            1 => x == FieldChunk.Mask,
            2 => y == 0,
            3 => y == FieldChunk.Mask,
            4 => z == 0,
            _ => z == FieldChunk.Mask,
        };
    }

    /// <summary>
    /// Dijkstra with unit weights: the sorted seeds merge with the monotone FIFO queue, so every pop is the
    /// global minimum and a stale entry (its cell improved meanwhile) is skipped. Restricted to
    /// <paramref name="affected"/> slots when given. Returns the cells popped.
    /// </summary>
    private long Run(FlowField field, byte[]? affected)
    {
        Array.Sort(_seeds, 0, _seedCount);
        int seed = 0;
        int head = 0;
        int tail = 0;
        long popped = 0;
        while (seed < _seedCount || head < tail)
        {
            long entry;
            if (head < tail && (seed >= _seedCount || _queue[head] <= _seeds[seed]))
            {
                entry = _queue[head++];
            }
            else
            {
                entry = _seeds[seed++];
            }

            int d = (int)(entry >> 32);
            int slot = (int)((entry >> 12) & 0xF_FFFF);
            int cell = (int)(entry & 0xFFF);
            var dist = field.DistancesOf(slot);
            if (dist[cell] != d)
            {
                continue;
            }

            popped++;
            int next = d + 1;
            if (next >= FlowField.Unreached)
            {
                continue;
            }

            for (int dir = 0; dir < 6; dir++)
            {
                if (!field.TryNeighbour(slot, cell, dir, out int nSlot, out int nCell))
                {
                    continue;
                }

                if (affected is not null && affected[nSlot] == 0)
                {
                    continue;
                }

                var nDist = field.DistancesOf(nSlot);
                if (next < nDist[nCell])
                {
                    nDist[nCell] = (ushort)next;
                    if (tail == _queue.Length)
                    {
                        Array.Resize(ref _queue, _queue.Length * 2);
                    }

                    _queue[tail++] = Pack(next, nSlot, nCell);
                }
            }
        }

        return popped;
    }

    private void PushSeed(int distance, int slot, int cell)
    {
        if (_seedCount == _seeds.Length)
        {
            Array.Resize(ref _seeds, _seeds.Length * 2);
        }

        _seeds[_seedCount++] = Pack(distance, slot, cell);
    }

    private static long Pack(int distance, int slot, int cell) => ((long)distance << 32) | ((long)slot << 12) | (uint)cell;
}
