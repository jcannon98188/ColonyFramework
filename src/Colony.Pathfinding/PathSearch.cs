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
        long bestNode = -1; // -1: direct in-chunk path
        if (startOrdinal == goalOrdinal && _startDist[toCell] >= 0)
        {
            best = _startDist[toCell];
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
        BuildWaypoints(graph, from, to, bestNode);
        return PathStatus.Found;
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

        // Drop repeated tiles (the start or goal may itself be a transition).
        int w = 1;
        for (int i = 1; i < needed; i++)
        {
            if (_waypoints[i] != _waypoints[w - 1])
            {
                _waypoints[w++] = _waypoints[i];
            }
        }

        WaypointCount = w;
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
