using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>
/// Independent oracle for path costs: a plain tile-level breadth-first search over the whole World (face
/// neighbours, vertical moves where <c>canClimb</c> holds for the lower tile). Shares no code with the service.
/// </summary>
internal static class PathOracle
{
    /// <summary>The optimal cost in face moves, -1 when unreachable.</summary>
    public static int Optimal(WorldSize size, Func<TilePos, bool> passable, Func<TilePos, bool>? canClimb, TilePos from, TilePos to)
    {
        if (!size.Contains(from) || !size.Contains(to) || !passable(from) || !passable(to))
        {
            return -1;
        }

        int count = size.X * size.Y * size.Z;
        var dist = new int[count];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        int start = Index(size, from);
        int goal = Index(size, to);
        dist[start] = 0;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (i == goal)
            {
                return dist[i];
            }

            var t = TileAt(size, i);
            int next = dist[i] + 1;
            Try(t.X - 1, t.Y, t.Z, next);
            Try(t.X + 1, t.Y, t.Z, next);
            Try(t.X, t.Y - 1, t.Z, next);
            Try(t.X, t.Y + 1, t.Z, next);
            if (canClimb?.Invoke(t) ?? true)
            {
                Try(t.X, t.Y, t.Z + 1, next);
            }

            if (t.Z > 0 && (canClimb?.Invoke(new TilePos(t.X, t.Y, t.Z - 1)) ?? true))
            {
                Try(t.X, t.Y, t.Z - 1, next);
            }
        }

        return -1;

        void Try(int x, int y, int z, int d)
        {
            var n = new TilePos(x, y, z);
            if (!size.Contains(n) || !passable(n))
            {
                return;
            }

            int j = Index(size, n);
            if (dist[j] < 0)
            {
                dist[j] = d;
                queue.Enqueue(j);
            }
        }
    }

    private static int Index(WorldSize size, TilePos t) => t.X + (size.X * (t.Y + (size.Y * t.Z)));

    private static TilePos TileAt(WorldSize size, int i) => new(i % size.X, i / size.X % size.Y, i / (size.X * size.Y));
}
