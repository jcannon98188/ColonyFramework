using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>
/// Independent oracle for region ids: a plain flood fill over every tile of the World (face neighbours, vertical
/// moves where <c>canClimb</c> holds for the lower tile), with the documented canonical id of a region = the
/// smallest <c>(chunk ordinal &lt;&lt; 12) | cellIndex</c> over its tiles. Shares no code with <see cref="RegionMap"/>.
/// </summary>
internal static class RegionOracle
{
    public static long[] Expected(World world, Func<TilePos, bool> passable, Func<TilePos, bool>? canClimb = null)
    {
        var fields = world.Fields();
        var size = fields.Size;
        int count = size.X * size.Y * size.Z;
        var labels = new long[count];
        Array.Fill(labels, -1);
        var open = new bool[count];
        var climb = new bool[count];
        for (int i = 0; i < count; i++)
        {
            var t = TileAt(size, i);
            open[i] = passable(t);
            climb[i] = open[i] && (canClimb?.Invoke(t) ?? true);
        }

        var stack = new Stack<int>();
        var members = new List<int>();
        for (int start = 0; start < count; start++)
        {
            if (!open[start] || labels[start] != -1)
            {
                continue;
            }

            members.Clear();
            labels[start] = 0;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                members.Add(i);
                var t = TileAt(size, i);
                Try(t.X - 1, t.Y, t.Z);
                Try(t.X + 1, t.Y, t.Z);
                Try(t.X, t.Y - 1, t.Z);
                Try(t.X, t.Y + 1, t.Z);
                if (climb[i])
                {
                    Try(t.X, t.Y, t.Z + 1);
                }

                if (t.Z > 0 && climb[Index(size, t.X, t.Y, t.Z - 1)])
                {
                    Try(t.X, t.Y, t.Z - 1);
                }
            }

            long min = long.MaxValue;
            foreach (int i in members)
            {
                var t = TileAt(size, i);
                min = Math.Min(min, ((long)fields.OrdinalOfTile(t.X, t.Y, t.Z) << 12) | (uint)t.CellIndex);
            }

            foreach (int i in members)
            {
                labels[i] = min;
            }
        }

        return labels;

        void Try(int x, int y, int z)
        {
            if (!size.Contains(x, y, z))
            {
                return;
            }

            int n = Index(size, x, y, z);
            if (open[n] && labels[n] == -1)
            {
                labels[n] = 0;
                stack.Push(n);
            }
        }
    }

    /// <summary>The service's region ids for every tile (-1 for none), in the oracle's tile order.</summary>
    public static long[] Actual(World world, MovementClass movementClass)
    {
        var size = world.Fields().Size;
        var service = world.Pathfinding();
        var labels = new long[size.X * size.Y * size.Z];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = service.RegionOf(TileAt(size, i), movementClass).Value;
        }

        return labels;
    }

    private static int Index(WorldSize size, int x, int y, int z) => x + (size.X * (y + (size.Y * z)));

    private static TilePos TileAt(WorldSize size, int i) => new(i % size.X, i / size.X % size.Y, i / (size.X * size.Y));
}
