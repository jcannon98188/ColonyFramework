using System.Numerics;
using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>
/// The abstract level of the two-level HPA* (spec section 9.1) for one movement class: per chunk, the transition
/// tiles through which paths cross to face-adjacent chunks and the exact in-chunk distances between them.
/// </summary>
/// <remarks>
/// Entrances: on each shared face, the boundary tile pairs that are passable on both sides (and climbable for
/// vertical faces) are grouped by (local component here, local component there) into face-connected patches;
/// on a horizontal face a patch stays within one z level (a level change is not a free move).
/// A patch gets one transition at the tile nearest its bounding-box centre and, when it spans more than
/// <see cref="MaxEntranceSpan"/> tiles in either face axis, one nearest every point of a <see cref="TransitionStride"/>
/// grid over its bounding box (ends included). Both
/// chunks compute a face from the lower-coordinate side, so the two ends of a transition always agree.
/// Chunk data is rebuilt lazily when the region labels of the chunk or a face neighbour moved (lazy repair
/// on read); nothing here is hashed or saved. Reads and repairs happen only under the service's search lock.
/// </remarks>
internal sealed class ChunkGraph
{
    public const int MaxEntranceSpan = 6;
    public const int TransitionStride = 4;
    private const int FaceCells = FieldChunk.Size * FieldChunk.Size;

    private readonly RegionMap _map;
    private readonly ChunkNodes[] _chunks;
    private readonly int[] _faceKey = new int[FaceCells];
    private readonly int[] _facePatch = new int[FaceCells];
    private readonly int[] _faceStack = new int[FaceCells];
    private readonly bool[] _faceChosen = new bool[FaceCells];
    private readonly int[] _bfsDist = new int[FieldChunk.CellCount];
    private readonly ushort[] _bfsQueue = new ushort[FieldChunk.CellCount];
    private int[] _collect = new int[256];
    private int _collectCount;

    public ChunkGraph(RegionMap map)
    {
        _map = map;
        _chunks = new ChunkNodes[map.ChunkCount];
        for (int i = 0; i < _chunks.Length; i++)
        {
            _chunks[i] = new ChunkNodes();
        }
    }

    public long Rebuilds { get; private set; }

    /// <summary>The chunk's transitions, repaired first if its labels or a neighbour's moved.</summary>
    public ChunkNodes Ensure(int ordinal)
    {
        var chunk = _chunks[ordinal];
        bool stale = chunk.BuiltVersion[0] != _map.LabelVersionOf(ordinal);
        for (int d = 0; d < 6 && !stale; d++)
        {
            int n = _map.NeighbourOf(ordinal, d);
            stale = n >= 0 && chunk.BuiltVersion[d + 1] != _map.LabelVersionOf(n);
        }

        if (stale)
        {
            Rebuild(ordinal);
        }

        return chunk;
    }

    /// <summary>The tile of a node.</summary>
    public TilePos TileOf(int ordinal, int cell)
    {
        var origin = _map.Fields.ChunkAt(ordinal).Origin;
        var (x, y, z) = FieldChunk.CellCoordinates(cell);
        return new TilePos(origin.X + x, origin.Y + y, origin.Z + z);
    }

    /// <summary>The cell on the other side of a face from a boundary cell.</summary>
    public static int MirrorCell(int cell, int direction)
    {
        var (x, y, z) = FieldChunk.CellCoordinates(cell);
        switch (direction)
        {
            case 0: x = FieldChunk.Mask; break;
            case 1: x = 0; break;
            case 2: y = FieldChunk.Mask; break;
            case 3: y = 0; break;
            case 4: z = FieldChunk.Mask; break;
            default: z = 0; break;
        }

        return FieldChunk.CellIndex(x, y, z);
    }

    /// <summary>
    /// Breadth-first distances from one cell over a chunk's passable tiles (face moves; vertical moves where the
    /// lower tile's up bit is set). <paramref name="dist"/> gets -1 for unreached cells. Returns the cells popped.
    /// </summary>
    public static int Distances(ulong[] passable, ulong[] up, int start, Span<int> dist, Span<ushort> queue)
    {
        dist.Fill(-1);
        int head = 0;
        int tail = 0;
        dist[start] = 0;
        queue[tail++] = (ushort)start;
        while (head < tail)
        {
            int c = queue[head++];
            int next = dist[c] + 1;
            int x = c & FieldChunk.Mask;
            int y = (c >> FieldChunk.Shift) & FieldChunk.Mask;
            int z = c >> (2 * FieldChunk.Shift);
            if (x > 0)
            {
                Relax(c - 1, next, passable, dist, queue, ref tail);
            }

            if (x < FieldChunk.Mask)
            {
                Relax(c + 1, next, passable, dist, queue, ref tail);
            }

            if (y > 0)
            {
                Relax(c - FieldChunk.Size, next, passable, dist, queue, ref tail);
            }

            if (y < FieldChunk.Mask)
            {
                Relax(c + FieldChunk.Size, next, passable, dist, queue, ref tail);
            }

            if (z < FieldChunk.Mask && Bit(up, c))
            {
                Relax(c + FaceCells, next, passable, dist, queue, ref tail);
            }

            if (z > 0 && Bit(up, c - FaceCells))
            {
                Relax(c - FaceCells, next, passable, dist, queue, ref tail);
            }
        }

        return tail;
    }

    private static void Relax(int cell, int next, ulong[] passable, Span<int> dist, Span<ushort> queue, ref int tail)
    {
        if (dist[cell] < 0 && Bit(passable, cell))
        {
            dist[cell] = next;
            queue[tail++] = (ushort)cell;
        }
    }

    public static bool Bit(ulong[] bits, int cell) => (bits[cell >> 6] & (1UL << (cell & 63))) != 0;

    private void Rebuild(int ordinal)
    {
        Rebuilds++;
        var chunk = _chunks[ordinal];
        chunk.BuiltVersion[0] = _map.LabelVersionOf(ordinal);
        _collectCount = 0;
        if (_map.LabelsOf(ordinal) is not null)
        {
            for (int d = 0; d < 6; d++)
            {
                int n = _map.NeighbourOf(ordinal, d);
                if (n < 0)
                {
                    continue;
                }

                chunk.BuiltVersion[d + 1] = _map.LabelVersionOf(n);
                if (_map.LabelsOf(n) is null)
                {
                    continue;
                }

                // Faces are computed from the lower-coordinate chunk's side so both ends agree.
                if ((d & 1) != 0)
                {
                    CollectFace(ordinal, n, d, ownerIsSelf: true);
                }
                else
                {
                    CollectFace(n, ordinal, d ^ 1, ownerIsSelf: false);
                }
            }
        }
        else
        {
            for (int d = 0; d < 6; d++)
            {
                int n = _map.NeighbourOf(ordinal, d);
                chunk.BuiltVersion[d + 1] = n < 0 ? -1 : _map.LabelVersionOf(n);
            }
        }

        // Merge collected (cell, face) pairs into nodes: one per cell, ascending cell, face bits OR'ed.
        var collected = _collect.AsSpan(0, _collectCount);
        collected.Sort();
        int count = 0;
        for (int i = 0; i < collected.Length; i++)
        {
            int cell = collected[i] >> 3;
            int face = collected[i] & 7;
            if (count > 0 && chunk.Cell[count - 1] == cell)
            {
                chunk.Faces[count - 1] |= (byte)(1 << face);
                continue;
            }

            chunk.EnsureNodes(count + 1);
            chunk.Cell[count] = (ushort)cell;
            chunk.Faces[count] = (byte)(1 << face);
            count++;
        }

        chunk.Count = count;
        if (count == 0)
        {
            return;
        }

        chunk.CellToNode ??= new ushort[FieldChunk.CellCount];
        Array.Clear(chunk.CellToNode);
        for (int i = 0; i < count; i++)
        {
            chunk.CellToNode[chunk.Cell[i]] = (ushort)(i + 1);
        }

        if (chunk.Dist.Length < count * count)
        {
            chunk.Dist = new int[(int)BitOperations.RoundUpToPowerOf2((uint)(count * count))];
        }

        var passable = _map.PassableOf(ordinal);
        var up = _map.UpOf(ordinal);
        for (int i = 0; i < count; i++)
        {
            Distances(passable, up, chunk.Cell[i], _bfsDist, _bfsQueue);
            for (int j = 0; j < count; j++)
            {
                chunk.Dist[(i * count) + j] = _bfsDist[chunk.Cell[j]];
            }
        }
    }

    /// <summary>Collects the transitions of the face between <paramref name="owner"/> (lower coordinate) and <paramref name="other"/> along positive <paramref name="direction"/>.</summary>
    private void CollectFace(int owner, int other, int direction, bool ownerIsSelf)
    {
        var mine = _map.LabelsOf(owner)!;
        var theirs = _map.LabelsOf(other)!;
        var up = _map.UpOf(owner);
        for (int a = 0; a < FieldChunk.Size; a++)
        {
            for (int b = 0; b < FieldChunk.Size; b++)
            {
                int my;
                int their;
                bool vertical = true;
                switch (direction)
                {
                    case 1: my = FieldChunk.CellIndex(FieldChunk.Mask, a, b); their = FieldChunk.CellIndex(0, a, b); break;
                    case 3: my = FieldChunk.CellIndex(a, FieldChunk.Mask, b); their = FieldChunk.CellIndex(a, 0, b); break;
                    default:
                        my = FieldChunk.CellIndex(a, b, FieldChunk.Mask);
                        their = FieldChunk.CellIndex(a, b, 0);
                        vertical = Bit(up, my);
                        break;
                }

                int la = mine[my];
                int lb = theirs[their];
                _faceKey[(a * FieldChunk.Size) + b] = la != 0 && lb != 0 && vertical ? (la << 16) | lb : 0;
            }
        }

        Array.Clear(_facePatch);
        Array.Clear(_faceChosen);
        int patches = 0;
        for (int f = 0; f < FaceCells; f++)
        {
            if (_faceKey[f] == 0 || _facePatch[f] != 0)
            {
                continue;
            }

            // Flood the patch over the 16x16 face and find its bounding box.
            patches++;
            int key = _faceKey[f];
            int top = 0;
            _facePatch[f] = patches;
            _faceStack[top++] = f;
            int minA = FieldChunk.Mask;
            int maxA = 0;
            int minB = FieldChunk.Mask;
            int maxB = 0;
            while (top > 0)
            {
                int g = _faceStack[--top];
                int a = g / FieldChunk.Size;
                int b = g % FieldChunk.Size;
                minA = Math.Min(minA, a);
                maxA = Math.Max(maxA, a);
                minB = Math.Min(minB, b);
                maxB = Math.Max(maxB, b);
                if (a > 0)
                {
                    Flood(g - FieldChunk.Size, key, patches, ref top);
                }

                if (a < FieldChunk.Mask)
                {
                    Flood(g + FieldChunk.Size, key, patches, ref top);
                }

                // On a horizontal face b is z: levels are not free moves, so patches never span them.
                if (direction == 5 && b > 0)
                {
                    Flood(g - 1, key, patches, ref top);
                }

                if (direction == 5 && b < FieldChunk.Mask)
                {
                    Flood(g + 1, key, patches, ref top);
                }
            }

            Choose(patches, (minA + maxA) >> 1, (minB + maxB) >> 1);
            if (maxA - minA + 1 > MaxEntranceSpan || maxB - minB + 1 > MaxEntranceSpan)
            {
                // A wide entrance gets a transition near every point of a stride grid over its bounding box, ends included.
                for (int a = minA; ; a += TransitionStride)
                {
                    for (int b = minB; ; b += TransitionStride)
                    {
                        Choose(patches, a, b);
                        if (b >= maxB)
                        {
                            break;
                        }

                        b = Math.Min(b, maxB - TransitionStride);
                    }

                    if (a >= maxA)
                    {
                        break;
                    }

                    a = Math.Min(a, maxA - TransitionStride);
                }
            }
        }

        for (int f = 0; f < FaceCells; f++)
        {
            if (!_faceChosen[f])
            {
                continue;
            }

            int a = f / FieldChunk.Size;
            int b = f % FieldChunk.Size;
            int cell = direction switch
            {
                1 => ownerIsSelf ? FieldChunk.CellIndex(FieldChunk.Mask, a, b) : FieldChunk.CellIndex(0, a, b),
                3 => ownerIsSelf ? FieldChunk.CellIndex(a, FieldChunk.Mask, b) : FieldChunk.CellIndex(a, 0, b),
                _ => ownerIsSelf ? FieldChunk.CellIndex(a, b, FieldChunk.Mask) : FieldChunk.CellIndex(a, b, 0),
            };
            int face = ownerIsSelf ? direction : direction ^ 1;
            if (_collectCount == _collect.Length)
            {
                Array.Resize(ref _collect, _collect.Length * 2);
            }

            _collect[_collectCount++] = (cell << 3) | face;
        }
    }

    private void Flood(int g, int key, int patch, ref int top)
    {
        if (_faceKey[g] == key && _facePatch[g] == 0)
        {
            _facePatch[g] = patch;
            _faceStack[top++] = g;
        }
    }

    /// <summary>Marks the patch cell nearest (a, b) (Manhattan on the face, ties by face index) as a transition.</summary>
    private void Choose(int patch, int a, int b)
    {
        int best = -1;
        int bestDistance = int.MaxValue;
        for (int f = 0; f < FaceCells; f++)
        {
            if (_facePatch[f] != patch)
            {
                continue;
            }

            int distance = Math.Abs((f / FieldChunk.Size) - a) + Math.Abs((f % FieldChunk.Size) - b);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = f;
            }
        }

        _faceChosen[best] = true;
    }

    /// <summary>One chunk's transitions (nodes of the abstract graph) and the search scratch per node.</summary>
    internal sealed class ChunkNodes
    {
        public int Count;
        public ushort[] Cell = [];
        public byte[] Faces = [];
        public int[] Dist = [];
        public ushort[]? CellToNode;
        public int[] BuiltVersion = [-1, -1, -1, -1, -1, -1, -1];

        // Search scratch, valid while Stamp[i] equals the running search's stamp.
        public int[] Stamp = [];
        public int[] G = [];
        public long[] Parent = [];
        public bool[] Closed = [];

        public void EnsureNodes(int count)
        {
            if (Cell.Length >= count)
            {
                return;
            }

            int capacity = Math.Max(8, (int)BitOperations.RoundUpToPowerOf2((uint)count));
            Array.Resize(ref Cell, capacity);
            Array.Resize(ref Faces, capacity);
            Array.Resize(ref Stamp, capacity);
            Array.Resize(ref G, capacity);
            Array.Resize(ref Parent, capacity);
            Array.Resize(ref Closed, capacity);
        }
    }
}
