using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>
/// The HPA* search over a <see cref="ChunkGraph"/> (spec section 9.1): the start and goal tiles are joined to the
/// transitions of their chunks by one in-chunk breadth-first search each, then A* runs over the abstract graph
/// (in-chunk distances plus unit cost across faces) with the Manhattan heuristic. Integer costs; ties broken
/// by f then insertion order, so the result is a pure function of the labels. The result is a waypoint list:
/// the start, the transition tiles crossed, the goal; consecutive waypoints lie in one chunk or are
/// face-adjacent across a border. One instance per service, used under its search lock; allocation free
/// once its buffers have grown.
/// </summary>
internal sealed class PathSearch
{
    private const int Infinity = int.MaxValue / 2;

    private readonly int[] _startDist = new int[FieldChunk.CellCount];
    private readonly int[] _goalDist = new int[FieldChunk.CellCount];
    private readonly ushort[] _queue = new ushort[FieldChunk.CellCount];
    private readonly int[] _localDist = new int[2 * FieldChunk.CellCount];
    private readonly ushort[] _localQueue = new ushort[2 * FieldChunk.CellCount];
    private readonly int[] _localParent = new int[2 * FieldChunk.CellCount];
    private int[] _localCross = new int[16];
    private int _localCrossCount;
    private OpenEntry[] _open = new OpenEntry[1024];
    private int _openCount;
    private int _stamp;
    private long _seq;
    private TilePos[] _waypoints = new TilePos[64];

    /// <summary>The waypoints of the last <see cref="PathStatus.Found"/> run.</summary>
    public ReadOnlySpan<TilePos> Waypoints => _waypoints.AsSpan(0, WaypointCount);

    public int WaypointCount { get; private set; }

    public int Cost { get; private set; }

    public ChunkCoord MissingChunk { get; private set; }

    /// <summary>Work units of the last run: abstract nodes expanded plus local cells popped.</summary>
    public long WorkUnits { get; private set; }

    public PathStatus Run(RegionMap map, TilePos from, TilePos to, Func<int, ChunkResidency> residency)
    {
        WaypointCount = 0;
        Cost = -1;
        WorkUnits = 0;
        var a = map.RegionOf(from);
        if (a.IsNone || a != map.RegionOf(to))
        {
            WorkUnits = 1;
            return PathStatus.Unreachable;
        }

        var fields = map.Fields;
        var graph = map.Graph;
        int startOrdinal = fields.OrdinalOf(from.Chunk);
        int goalOrdinal = fields.OrdinalOf(to.Chunk);
        if (IsUnloaded(residency, fields, startOrdinal) || IsUnloaded(residency, fields, goalOrdinal))
        {
            return PathStatus.NeedsLoading;
        }

        int fromCell = from.CellIndex;
        int toCell = to.CellIndex;
        WorkUnits += ChunkGraph.Distances(map.PassableOf(startOrdinal), map.UpOf(startOrdinal), fromCell, _startDist, _queue);
        WorkUnits += ChunkGraph.Distances(map.PassableOf(goalOrdinal), map.UpOf(goalOrdinal), toCell, _goalDist, _queue);

        int best = Infinity;
        long bestNode = -1; // -1: direct local path
        _localCrossCount = 0;
        if (startOrdinal == goalOrdinal)
        {
            if (_startDist[toCell] >= 0)
            {
                best = _startDist[toCell];
            }
        }
        else
        {
            // Near the unit the exact answer is cheap: a local search over the start and goal chunks when they touch.
            for (int d = 0; d < 6; d++)
            {
                if (map.NeighbourOf(startOrdinal, d) == goalOrdinal)
                {
                    int local = LocalDistance(map, startOrdinal, goalOrdinal, d, fromCell, toCell);
                    if (local >= 0)
                    {
                        best = local;
                    }

                    break;
                }
            }
        }

        _stamp++;
        _openCount = 0;
        var start = graph.Ensure(startOrdinal);
        for (int i = 0; i < start.Count; i++)
        {
            int g = _startDist[start.Cell[i]];
            if (g >= 0)
            {
                Relax(graph, start, startOrdinal, i, g, -1, to);
            }
        }

        while (_openCount > 0)
        {
            var entry = Pop();
            var chunk = graph.Ensure(entry.Ordinal);
            int node = entry.Node;
            if (chunk.Stamp[node] != _stamp || chunk.Closed[node] || chunk.G[node] != entry.G)
            {
                continue;
            }

            if (entry.F >= best)
            {
                break;
            }

            chunk.Closed[node] = true;
            WorkUnits++;
            int g = entry.G;
            int cell = chunk.Cell[node];
            if (entry.Ordinal == goalOrdinal && _goalDist[cell] >= 0 && g + _goalDist[cell] < best)
            {
                best = g + _goalDist[cell];
                bestNode = Key(entry.Ordinal, node);
            }

            // In-chunk edges to the other transitions of this chunk.
            int count = chunk.Count;
            for (int j = 0; j < count; j++)
            {
                int d = chunk.Dist[(node * count) + j];
                if (j != node && d > 0)
                {
                    Relax(graph, chunk, entry.Ordinal, j, g + d, Key(entry.Ordinal, node), to);
                }
            }

            // Unit-cost edges across each face this transition crosses.
            int faces = chunk.Faces[node];
            for (int d = 0; d < 6; d++)
            {
                if ((faces & (1 << d)) == 0)
                {
                    continue;
                }

                int n = map.NeighbourOf(entry.Ordinal, d);
                if (IsUnloaded(residency, fields, n))
                {
                    return PathStatus.NeedsLoading;
                }

                var other = graph.Ensure(n);
                int their = other.CellToNode![ChunkGraph.MirrorCell(cell, d)] - 1;
                if (their >= 0)
                {
                    Relax(graph, other, n, their, g + 1, Key(entry.Ordinal, node), to);
                }
            }
        }

        if (best >= Infinity)
        {
            return PathStatus.Unreachable;
        }

        Cost = best;
        if (bestNode < 0 && _localCrossCount > 0)
        {
            // The local two-chunk path won: start, each crossing pair in walking order, goal.
            int needed = _localCrossCount + 2;
            if (_waypoints.Length < needed)
            {
                Array.Resize(ref _waypoints, Math.Max(needed, _waypoints.Length * 2));
            }

            _waypoints[0] = from;
            for (int i = 0; i < _localCrossCount; i++)
            {
                int index = _localCross[_localCrossCount - 1 - i];
                bool inA = index < FieldChunk.CellCount;
                _waypoints[1 + i] = graph.TileOf(inA ? startOrdinal : goalOrdinal, inA ? index : index - FieldChunk.CellCount);
            }

            _waypoints[needed - 1] = to;
            WaypointCount = Dedupe(needed);
        }
        else
        {
            BuildWaypoints(graph, from, to, bestNode);
        }

        return PathStatus.Found;
    }

    /// <summary>Exact breadth-first distance over two face-adjacent chunks (cells 0..4095 in <paramref name="a"/>, 4096.. in <paramref name="b"/>), -1 when not connected inside them.</summary>
    private int LocalDistance(RegionMap map, int a, int b, int direction, int fromCell, int toCell)
    {
        var dist = _localDist;
        var queue = _localQueue;
        var parent = _localParent;
        Array.Fill(dist, -1);
        int head = 0;
        int tail = 0;
        int goal = FieldChunk.CellCount + toCell;
        dist[fromCell] = 0;
        parent[fromCell] = -1;
        queue[tail++] = (ushort)fromCell;
        var passableA = map.PassableOf(a);
        var passableB = map.PassableOf(b);
        var upA = map.UpOf(a);
        var upB = map.UpOf(b);
        while (head < tail)
        {
            int index = queue[head++];
            WorkUnits++;
            if (index == goal)
            {
                // Every face crossing of the path (goal to start order) becomes a pair of waypoints.
                _localCrossCount = 0;
                for (int i = index; parent[i] >= 0; i = parent[i])
                {
                    if ((i >= FieldChunk.CellCount) != (parent[i] >= FieldChunk.CellCount))
                    {
                        if (_localCrossCount + 2 > _localCross.Length)
                        {
                            Array.Resize(ref _localCross, _localCross.Length * 2);
                        }

                        _localCross[_localCrossCount++] = i;
                        _localCross[_localCrossCount++] = parent[i];
                    }
                }

                return dist[index];
            }

            bool inA = index < FieldChunk.CellCount;
            int c = inA ? index : index - FieldChunk.CellCount;
            int offset = inA ? 0 : FieldChunk.CellCount;
            var passable = inA ? passableA : passableB;
            var up = inA ? upA : upB;
            int next = dist[index] + 1;
            int x = c & FieldChunk.Mask;
            int y = (c >> FieldChunk.Shift) & FieldChunk.Mask;
            int z = c >> (2 * FieldChunk.Shift);

            Step(x > 0, c - 1, 0);
            Step(x < FieldChunk.Mask, c + 1, 1);
            Step(y > 0, c - FieldChunk.Size, 2);
            Step(y < FieldChunk.Mask, c + FieldChunk.Size, 3);
            Step(z > 0 && ChunkGraph.Bit(up, c - (FieldChunk.Size * FieldChunk.Size)), c - (FieldChunk.Size * FieldChunk.Size), 4);
            Step(z < FieldChunk.Mask && ChunkGraph.Bit(up, c), c + (FieldChunk.Size * FieldChunk.Size), 5);

            void Step(bool inside, int neighbour, int face)
            {
                if (inside)
                {
                    int j = offset + neighbour;
                    if (dist[j] < 0 && ChunkGraph.Bit(passable, neighbour))
                    {
                        dist[j] = next;
                        parent[j] = index;
                        queue[tail++] = (ushort)j;
                    }

                    return;
                }

                // Leaving the chunk: only across the shared face, into the other chunk.
                int shared = inA ? direction : direction ^ 1;
                if (face != shared)
                {
                    return;
                }

                int mirror = ChunkGraph.MirrorCell(c, face);
                var otherPassable = inA ? passableB : passableA;
                bool climb = face switch
                {
                    5 => ChunkGraph.Bit(up, c),
                    4 => ChunkGraph.Bit(inA ? upB : upA, mirror),
                    _ => true,
                };
                int k = (inA ? FieldChunk.CellCount : 0) + mirror;
                if (climb && dist[k] < 0 && ChunkGraph.Bit(otherPassable, mirror))
                {
                    dist[k] = next;
                    parent[k] = index;
                    queue[tail++] = (ushort)k;
                }
            }
        }

        return -1;
    }

    private bool IsUnloaded(Func<int, ChunkResidency> residency, FieldSet fields, int ordinal)
    {
        if (residency(ordinal) != ChunkResidency.Unloaded)
        {
            return false;
        }

        MissingChunk = fields.ChunkAt(ordinal);
        return true;
    }

    private void Relax(ChunkGraph graph, ChunkGraph.ChunkNodes chunk, int ordinal, int node, int g, long parent, TilePos goal)
    {
        if (chunk.Stamp[node] == _stamp)
        {
            if (chunk.Closed[node] || chunk.G[node] <= g)
            {
                return;
            }
        }
        else
        {
            chunk.Stamp[node] = _stamp;
            chunk.Closed[node] = false;
        }

        chunk.G[node] = g;
        chunk.Parent[node] = parent;
        var tile = graph.TileOf(ordinal, chunk.Cell[node]);
        int h = Math.Abs(tile.X - goal.X) + Math.Abs(tile.Y - goal.Y) + Math.Abs(tile.Z - goal.Z);
        Push(new OpenEntry(g + h, _seq++, ordinal, node, g));
    }

    private void BuildWaypoints(ChunkGraph graph, TilePos from, TilePos to, long bestNode)
    {
        // Count the chain first so the waypoints come out start to goal without a second buffer.
        int chain = 0;
        for (long key = bestNode; key >= 0; key = ParentOf(graph, key))
        {
            chain++;
        }

        int needed = chain + 2;
        if (_waypoints.Length < needed)
        {
            Array.Resize(ref _waypoints, Math.Max(needed, _waypoints.Length * 2));
        }

        _waypoints[0] = from;
        int index = chain;
        for (long key = bestNode; key >= 0; key = ParentOf(graph, key))
        {
            _waypoints[index--] = graph.TileOf(OrdinalOf(key), graph.Ensure(OrdinalOf(key)).Cell[NodeOf(key)]);
        }

        _waypoints[chain + 1] = to;
        WaypointCount = Dedupe(needed);
    }

    /// <summary>Drops repeated consecutive tiles (the start or goal may itself be a transition); returns the count kept.</summary>
    private int Dedupe(int count)
    {
        int w = 1;
        for (int i = 1; i < count; i++)
        {
            if (_waypoints[i] != _waypoints[w - 1])
            {
                _waypoints[w++] = _waypoints[i];
            }
        }

        return w;
    }

    private static long ParentOf(ChunkGraph graph, long key) => graph.Ensure(OrdinalOf(key)).Parent[NodeOf(key)];

    private static long Key(int ordinal, int node) => ((long)ordinal << 16) | (uint)node;

    private static int OrdinalOf(long key) => (int)(key >> 16);

    private static int NodeOf(long key) => (int)(key & 0xFFFF);

    // ---- binary heap ordered by (F, Seq) ----

    private void Push(OpenEntry entry)
    {
        if (_openCount == _open.Length)
        {
            Array.Resize(ref _open, _open.Length * 2);
        }

        int i = _openCount++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (!Less(entry, _open[parent]))
            {
                break;
            }

            _open[i] = _open[parent];
            i = parent;
        }

        _open[i] = entry;
    }

    private OpenEntry Pop()
    {
        var top = _open[0];
        var last = _open[--_openCount];
        int i = 0;
        while (true)
        {
            int child = (2 * i) + 1;
            if (child >= _openCount)
            {
                break;
            }

            if (child + 1 < _openCount && Less(_open[child + 1], _open[child]))
            {
                child++;
            }

            if (!Less(_open[child], last))
            {
                break;
            }

            _open[i] = _open[child];
            i = child;
        }

        _open[i] = last;
        return top;
    }

    private static bool Less(in OpenEntry a, in OpenEntry b) => a.F != b.F ? a.F < b.F : a.Seq < b.Seq;

    private readonly record struct OpenEntry(int F, long Seq, int Ordinal, int Node, int G);
}
