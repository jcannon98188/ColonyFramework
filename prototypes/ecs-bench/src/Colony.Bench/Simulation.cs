using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Colony.Ecs;

namespace Colony.Bench;

// ---------------- Components (fixed-point positions: 1 tile = 256 units) ----------------
public struct Position { public int X, Y; }
public struct Target { public int X, Y; }
public struct Hunger { public float Value; }
public struct Health { public int Value; }
public struct Food { public int Nutrition; }
public struct Lifetime { public int Ticks; }
public struct Marked { public byte Unused; }

public static class Sim
{
    public const int WorldTiles = 512;
    public const int TileUnits = 256;
    public const int Speed = 64;                 // units per axis per Tick
    public const float HungerRate = 0.1f;
    public const float HungryThreshold = 50f;
    public const float StarvingThreshold = 100f;
    public static bool Sabotage;

    public static Position RandomTile(Philox.Block r) =>
        new() { X = (int)(r.A % WorldTiles) * TileUnits + TileUnits / 2, Y = (int)(r.B % WorldTiles) * TileUnits + TileUnits / 2 };
}

// ---------------- Resources ----------------
/// <summary>Uniform grid (8x8-tile cells, 64x64 cells) of food, rebuilt each Tick in CSR form by FoodIndexSystem.</summary>
public sealed class FoodGrid
{
    public const int CellShift = 11; // 8 tiles * 256 units = 2048
    public const int Dim = 64;
    public readonly int[] CellStart = new int[Dim * Dim + 1];
    public readonly int[] Cursor = new int[Dim * Dim];
    public Entity[] Items = new Entity[8192];
    public Position[] Pos = new Position[8192];
    public int Count;

    /// <summary>Approximate nearest: ring search, stops one ring after the first hit. Ties broken by scan order (deterministic).</summary>
    public bool FindNearest(int x, int y, out Entity food, out Position foodPos)
    {
        int cx = x >> CellShift, cy = y >> CellShift;
        long best = long.MaxValue;
        int bestI = -1, foundRing = -1;
        for (int r = 0; r < Dim; r++)
        {
            if (foundRing >= 0 && r > foundRing + 1) break;
            for (int dy = -r; dy <= r; dy++)
            {
                int y2 = cy + dy;
                if ((uint)y2 >= Dim) continue;
                bool edgeRow = dy == -r || dy == r;
                int step = edgeRow ? 1 : Math.Max(1, 2 * r);
                for (int dx = -r; dx <= r; dx += step)
                {
                    int x2 = cx + dx;
                    if ((uint)x2 >= Dim) continue;
                    int cell = y2 * Dim + x2;
                    for (int k = CellStart[cell], e = CellStart[cell + 1]; k < e; k++)
                    {
                        long ddx = Pos[k].X - x, ddy = Pos[k].Y - y;
                        long d = ddx * ddx + ddy * ddy;
                        if (d < best) { best = d; bestI = k; }
                    }
                }
            }
            if (bestI >= 0 && foundRing < 0) foundRing = r;
        }
        if (bestI < 0) { food = default; foodPos = default; return false; }
        food = Items[bestI];
        foodPos = Pos[bestI];
        return true;
    }
}

public sealed class ColonyStats
{
    public double AverageHunger;
    public int Starving;
    public int Colonists;
    public int EatenThisTick;
    public long TotalEaten;
    public int FoodCount;
}

/// <summary>Positions of food eaten this Tick, in job (chunk) order. Written by Eat, read by FoodSpawner.</summary>
public sealed class EatenFood
{
    public Position[] Items = new Position[1024];
    public int Count;
}

// ---------------- Systems ----------------

/// <summary>Pre: rebuild the FoodGrid Resource (single job, counting sort over food chunks in chunk order).</summary>
public sealed class FoodIndexSystem : SystemBase
{
    public override string Name => "FoodIndex";
    public override Phase Phase => Phase.Pre;
    public FoodIndexSystem()
    {
        All = Mask.Of<Position, Food>(); Reads = All; ResourceWrites = ResourceId<FoodGrid>.Bit; SplitByChunk = false;
    }

    public override void Execute(in JobContext ctx)
    {
        var w = ctx.World;
        var g = w.GetResource<FoodGrid>();
        Array.Clear(g.CellStart);
        int total = w.CountMatching(All);
        if (g.Items.Length < total) { g.Items = new Entity[total * 2]; g.Pos = new Position[total * 2]; }
        var archetypes = w.Archetypes;
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                int sum = 0;
                for (int c = 0; c < FoodGrid.Dim * FoodGrid.Dim; c++) { int n = g.CellStart[c]; g.CellStart[c] = sum; g.Cursor[c] = sum; sum += n; }
                g.CellStart[FoodGrid.Dim * FoodGrid.Dim] = sum;
                g.Count = sum;
            }
            for (int a = 0; a < archetypes.Count; a++)
            {
                var arch = archetypes[a];
                if ((arch.Signature & All) != All) continue;
                for (int ci = 0; ci < arch.ChunkCount; ci++)
                {
                    var chunk = arch.GetChunk(ci);
                    var pos = chunk.Column<Position>();
                    var ents = chunk.Entities;
                    for (int i = 0; i < pos.Length; i++)
                    {
                        int cell = (pos[i].Y >> FoodGrid.CellShift) * FoodGrid.Dim + (pos[i].X >> FoodGrid.CellShift);
                        if (pass == 0) g.CellStart[cell]++;
                        else { int k = g.Cursor[cell]++; g.Items[k] = ents[i]; g.Pos[k] = pos[i]; }
                    }
                }
            }
        }
    }
}

/// <summary>Update: hunger += rate. SIMD over the padded column (no scalar tail thanks to 16-element padding).</summary>
public sealed class HungerSystem : SystemBase
{
    public override string Name => "Hunger";
    public override Phase Phase => Phase.Update;
    public HungerSystem() { All = Mask.Of<Hunger>(); Writes = All; }

    public override void Execute(in JobContext ctx)
    {
        var span = MemoryMarshal.Cast<Hunger, float>(ctx.Chunk.PaddedColumn<Hunger>());
        ref float f = ref MemoryMarshal.GetReference(span);
        int n = span.Length;
        if (Vector256.IsHardwareAccelerated)
        {
            var add = Vector256.Create(Sim.HungerRate);
            for (int i = 0; i < n; i += 8)
                (Vector256.LoadUnsafe(ref f, (nuint)i) + add).StoreUnsafe(ref f, (nuint)i);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var add = Vector128.Create(Sim.HungerRate);
            for (int i = 0; i < n; i += 4)
                (Vector128.LoadUnsafe(ref f, (nuint)i) + add).StoreUnsafe(ref f, (nuint)i);
        }
        else for (int i = 0; i < n; i++) span[i] += Sim.HungerRate;
    }
}

/// <summary>Update: step Position toward Target by at most Speed per axis. SIMD over interleaved int pairs.</summary>
public sealed class MoveSystem : SystemBase
{
    public override string Name => "Move";
    public override Phase Phase => Phase.Update;
    public MoveSystem() { All = Mask.Of<Position, Target>(); Reads = Mask.Of<Target>(); Writes = Mask.Of<Position>(); }

    public override void Execute(in JobContext ctx)
    {
        var pos = MemoryMarshal.Cast<Position, int>(ctx.Chunk.PaddedColumn<Position>());
        var tgt = MemoryMarshal.Cast<Target, int>(ctx.Chunk.PaddedColumn<Target>());
        ref int p = ref MemoryMarshal.GetReference(pos);
        ref int t = ref MemoryMarshal.GetReference(tgt);
        int n = pos.Length;
        if (Vector256.IsHardwareAccelerated)
        {
            var hi = Vector256.Create(Sim.Speed);
            var lo = Vector256.Create(-Sim.Speed);
            for (int i = 0; i < n; i += 8)
            {
                var vp = Vector256.LoadUnsafe(ref p, (nuint)i);
                var d = Vector256.LoadUnsafe(ref t, (nuint)i) - vp;
                (vp + Vector256.Min(Vector256.Max(d, lo), hi)).StoreUnsafe(ref p, (nuint)i);
            }
        }
        else
        {
            for (int i = 0; i < n; i++) pos[i] += Math.Clamp(tgt[i] - pos[i], -Sim.Speed, Sim.Speed);
        }
    }
}

/// <summary>Update: hungry colonists target the nearest food; within one tile they eat it (Destroy via command buffer).</summary>
public sealed class EatSystem : SystemBase
{
    public override string Name => "Eat";
    public override Phase Phase => Phase.Update;
    private int[] _eaten = new int[64];
    private Position[][] _eatenPos = CreateSlots(64);

    private static Position[][] CreateSlots(int n)
    {
        var a = new Position[n][];
        for (int i = 0; i < n; i++) a[i] = new Position[64];
        return a;
    }

    public EatSystem()
    {
        All = Mask.Of<Position, Target, Hunger>();
        Reads = Mask.Of<Position>();
        Writes = Mask.Of<Hunger, Target>();
        ResourceReads = ResourceId<FoodGrid>.Bit;
        ResourceWrites = ResourceId<ColonyStats>.Bit | ResourceId<EatenFood>.Bit;
    }

    public override void PrepareJobs(int jobCount)
    {
        if (_eaten.Length < jobCount)
        {
            _eaten = new int[jobCount * 2];
            var old = _eatenPos;
            _eatenPos = CreateSlots(jobCount * 2);
            Array.Copy(old, _eatenPos, old.Length);
        }
        Array.Clear(_eaten, 0, jobCount);
    }

    public override void Execute(in JobContext ctx)
    {
        var grid = ctx.World.GetResource<FoodGrid>();
        var pos = ctx.Chunk.Column<Position>();
        var tgt = ctx.Chunk.Column<Target>();
        var hun = ctx.Chunk.Column<Hunger>();
        int eaten = 0;
        for (int i = 0; i < hun.Length; i++)
        {
            if (hun[i].Value < Sim.HungryThreshold) continue;
            if (!grid.FindNearest(pos[i].X, pos[i].Y, out var food, out var fp)) continue;
            if (Math.Abs(fp.X - pos[i].X) <= Sim.TileUnits && Math.Abs(fp.Y - pos[i].Y) <= Sim.TileUnits)
            {
                // Shortcut: two colonists may eat the same food in one Tick; the second Destroy is skipped as stale.
                ctx.Commands.Destroy(food);
                hun[i].Value = 0;
                ref var slot = ref _eatenPos[ctx.JobIndex];
                if (eaten == slot.Length) Array.Resize(ref slot, slot.Length * 2); // warm-up only
                slot[eaten++] = fp;
            }
            else
            {
                tgt[i].X = fp.X;
                tgt[i].Y = fp.Y;
            }
        }
        _eaten[ctx.JobIndex] = eaten;
    }

    public override void EndTick(World w, long tick)
    {
        var s = w.GetResource<ColonyStats>();
        var log = w.GetResource<EatenFood>();
        int sum = 0;
        for (int j = 0; j < JobsThisTick; j++) sum += _eaten[j];
        if (log.Items.Length < sum) log.Items = new Position[sum * 2];
        log.Count = 0;
        for (int j = 0; j < JobsThisTick; j++)
        {
            Array.Copy(_eatenPos[j], 0, log.Items, log.Count, _eaten[j]);
            log.Count += _eaten[j];
        }
        s.EatenThisTick = sum;
        s.TotalEaten += sum;
    }
}

/// <summary>Update: fed colonists that reached their Target pick a new random tile (Philox keyed by seed/system/entity/tick).</summary>
public sealed class WanderSystem : SystemBase
{
    public override string Name => "Wander";
    public override Phase Phase => Phase.Update;
    public WanderSystem() { All = Mask.Of<Position, Target, Hunger>(); Reads = Mask.Of<Position, Hunger>(); Writes = Mask.Of<Target>(); }

    public override void Execute(in JobContext ctx)
    {
        var pos = ctx.Chunk.Column<Position>();
        var tgt = ctx.Chunk.Column<Target>();
        var hun = ctx.Chunk.Column<Hunger>();
        var ents = ctx.Chunk.Entities;
        ulong seed = ctx.World.Seed;
        for (int i = 0; i < pos.Length; i++)
        {
            if (hun[i].Value >= Sim.HungryThreshold) continue;
            if (pos[i].X != tgt[i].X || pos[i].Y != tgt[i].Y) continue;
            // Sim.Sabotage: negative control for the determinism test (keys the RNG by worker thread, which must FAIL).
            var p = Sim.RandomTile(Philox.For(seed, Id, ents[i], ctx.Tick, Sim.Sabotage ? (uint)ctx.ThreadIndex : 0u));
            tgt[i].X = p.X;
            tgt[i].Y = p.Y;
        }
    }
}

/// <summary>Update: starving colonists lose 1 health per Tick (branchless SIMD compare + add).</summary>
public sealed class StarvationSystem : SystemBase
{
    public override string Name => "Starvation";
    public override Phase Phase => Phase.Update;
    public StarvationSystem() { All = Mask.Of<Hunger, Health>(); Reads = Mask.Of<Hunger>(); Writes = Mask.Of<Health>(); }

    public override void Execute(in JobContext ctx)
    {
        var hun = MemoryMarshal.Cast<Hunger, float>(ctx.Chunk.PaddedColumn<Hunger>());
        var hp = MemoryMarshal.Cast<Health, int>(ctx.Chunk.PaddedColumn<Health>());
        ref float h = ref MemoryMarshal.GetReference(hun);
        ref int v = ref MemoryMarshal.GetReference(hp);
        int n = hun.Length;
        if (Vector256.IsHardwareAccelerated)
        {
            var thr = Vector256.Create(Sim.StarvingThreshold);
            for (int i = 0; i < n; i += 8)
            {
                var m = Vector256.GreaterThan(Vector256.LoadUnsafe(ref h, (nuint)i), thr).AsInt32(); // -1 where starving
                Vector256.Max(Vector256.LoadUnsafe(ref v, (nuint)i) + m, Vector256<int>.Zero).StoreUnsafe(ref v, (nuint)i);
            }
        }
        else
        {
            for (int i = 0; i < n; i++) if (hun[i] > Sim.StarvingThreshold) hp[i] = Math.Max(0, hp[i] - 1);
        }
    }
}

/// <summary>Late: per-chunk reduction of hunger/health, combined in chunk (job) order for determinism.</summary>
public sealed class StatsSystem : SystemBase
{
    public override string Name => "Stats";
    public override Phase Phase => Phase.Late;
    private double[] _sum = new double[64];
    private int[] _starving = new int[64];
    private int[] _count = new int[64];

    public StatsSystem()
    {
        All = Mask.Of<Hunger, Health>(); Reads = All; ResourceWrites = ResourceId<ColonyStats>.Bit;
    }

    public override void PrepareJobs(int jobCount)
    {
        if (_sum.Length < jobCount) { _sum = new double[jobCount * 2]; _starving = new int[jobCount * 2]; _count = new int[jobCount * 2]; }
    }

    public override void Execute(in JobContext ctx)
    {
        var hun = ctx.Chunk.Column<Hunger>();
        var hp = ctx.Chunk.Column<Health>();
        double sum = 0;
        int starving = 0;
        for (int i = 0; i < hun.Length; i++)
        {
            sum += hun[i].Value;
            if (hp[i].Value < 100) starving++;
        }
        _sum[ctx.JobIndex] = sum;
        _starving[ctx.JobIndex] = starving;
        _count[ctx.JobIndex] = hun.Length;
    }

    public override void EndTick(World w, long tick)
    {
        double sum = 0; int starving = 0, n = 0;
        for (int j = 0; j < JobsThisTick; j++) { sum += _sum[j]; starving += _starving[j]; n += _count[j]; }
        var s = w.GetResource<ColonyStats>();
        s.AverageHunger = n == 0 ? 0 : sum / n;
        s.Starving = starving;
        s.Colonists = n;
    }
}

/// <summary>
/// Late: top food back up to the target count via Create commands. Food regrows within +-8 tiles of where it was eaten
/// (keeps the food distribution stable; purely random respawn let the colony strip the centre and starve).
/// </summary>
public sealed class FoodSpawnerSystem : SystemBase
{
    public override string Name => "FoodSpawner";
    public override Phase Phase => Phase.Late;
    private readonly int _target;

    public FoodSpawnerSystem(int target)
    {
        _target = target;
        All = Mask.Of<Position, Food>(); Reads = Mask.Of<Food>(); ResourceReads = ResourceId<EatenFood>.Bit; SplitByChunk = false;
    }

    public override void Execute(in JobContext ctx)
    {
        int have = ctx.World.CountMatching(All);
        var log = ctx.World.GetResource<EatenFood>();
        const int max = Sim.WorldTiles * Sim.TileUnits - Sim.TileUnits / 2;
        for (int i = have, k = 0; i < _target; i++, k++)
        {
            var r = Philox.For(ctx.World.Seed, Id, (uint)i, ctx.Tick);
            Position p;
            if (k < log.Count)
            {
                p = log.Items[k];
                p.X = Math.Clamp(p.X + ((int)(r.C % 17) - 8) * Sim.TileUnits, Sim.TileUnits / 2, max);
                p.Y = Math.Clamp(p.Y + ((int)(r.D % 17) - 8) * Sim.TileUnits, Sim.TileUnits / 2, max);
            }
            else p = Sim.RandomTile(r);
            ctx.Commands.Create(p, new Food { Nutrition = 50 });
        }
    }
}

/// <summary>
/// Late (churn stress only): debris counts down; at 5 it gains Marked, at 2 loses Marked, at 0 it is destroyed and a new
/// one is created. With lifetimes uniform in 1..10 that is ~10% destroy + 10% create + 10% add + 10% remove per Tick.
/// </summary>
public sealed class ChurnSystem : SystemBase
{
    public override string Name => "Churn";
    public override Phase Phase => Phase.Late;
    public ChurnSystem() { All = Mask.Of<Position, Lifetime>(); Writes = Mask.Of<Lifetime>(); }

    public override void Execute(in JobContext ctx)
    {
        var life = ctx.Chunk.Column<Lifetime>();
        var ents = ctx.Chunk.Entities;
        bool marked = ctx.Chunk.Has<Marked>();
        ulong seed = ctx.World.Seed;
        for (int i = 0; i < life.Length; i++)
        {
            int l = --life[i].Ticks;
            if (l <= 0)
            {
                ctx.Commands.Destroy(ents[i]);
                ctx.Commands.Create(Sim.RandomTile(Philox.For(seed, Id, ents[i], ctx.Tick)), new Lifetime { Ticks = 10 });
            }
            else if (l == 5 && !marked) ctx.Commands.Add(ents[i], new Marked());
            else if (l == 2 && marked) ctx.Commands.Remove<Marked>(ents[i]);
        }
    }
}
