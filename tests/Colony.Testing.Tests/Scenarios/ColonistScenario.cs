using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

// The acceptance oracle for TASK-7 (spec "Core slice" acceptance): the ECS prototype's colonist scenario
// rebuilt on the real World.Tick with hand-written Systems. 100k colonists wander, get hungry, walk to the
// nearest food and eat it (destroying it through command buffers); food regrows; 1% of the colony churns
// every Tick. Run through Colony.Testing's harnesses (TASK-10). This file has no test-framework dependency:
// the Native AOT host (tests/Colony.Testing.AotHost) compiles it as well.
// Component names are prefixed so they never collide with the test project's own Components.

public struct ColonistPosition
{
    public int X;
    public int Y;
}

public struct ColonistTarget
{
    public int X;
    public int Y;
}

public struct ColonistHunger
{
    public float Value;
}

public struct ColonistHealth
{
    public int Value;
}

public struct FoodItem
{
    public int Nutrition;
}

public sealed class ColonistScenario : Scenario
{
    public const int DefaultColonists = 100_000;
    public const int DefaultFood = 5_000;
    public const ulong DefaultSeed = 12345;
    public const int WorldTiles = 512;
    public const int TileUnits = 256;
    public const int Speed = 64;
    public const float HungerRate = 0.1f;
    public const float HungryThreshold = 50f;
    public const float StarvingThreshold = 100f;
    public const int ChurnPercent = 1;

    /// <summary>The negative control is a separate scenario name so it never shares a golden file with the real one.</summary>
    public override string Name => Sabotage ? "colonist-negative-control" : "colonist";

    public override ulong Seed { get; init; } = DefaultSeed;

    public int Colonists { get; init; } = DefaultColonists;

    public int Food { get; init; } = DefaultFood;

    /// <summary>Negative control: key the Wander RNG by worker thread, which must break the cross-thread hash comparison.</summary>
    public bool Sabotage { get; init; }

    public override int InitialEntityCapacity => (Colonists + Food) * 2;

    /// <summary>Registers the Components and Systems. The scenario's scratch state is per schema, so the harness composes once per World.</summary>
    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .AddComponent<ColonistPosition>("colonist.position")
            .AddComponent<ColonistTarget>("colonist.target")
            .AddComponent<ColonistHunger>("colonist.hunger")
            .AddComponent<ColonistHealth>("colonist.health")
            .AddComponent<FoodItem>("food")
            .DebugValidation(false);
        var position = builder.Component<ColonistPosition>();
        var target = builder.Component<ColonistTarget>();
        var hunger = builder.Component<ColonistHunger>();
        var health = builder.Component<ColonistHealth>();
        var food = builder.Component<FoodItem>();
        var colonist = ComponentMask.Of(position, target, hunger, health);
        var foodSignature = ComponentMask.Of(position, food);
        var state = new ScenarioState(Food, Sabotage);

        builder
            .AddSystem(new SystemDefinition
            {
                Name = "FoodIndex",
                Phase = Phase.Begin,
                Reads = foodSignature,
                Begin = (in SystemContext context) => state.RebuildFoodGrid(context.World, position, food),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Hunger",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(hunger),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    var values = chunk.PaddedColumn<ColonistHunger>(hunger);
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i].Value += HungerRate;
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Move",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(target),
                Writes = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    var positions = chunk.Column<ColonistPosition>(position);
                    var targets = chunk.Column<ColonistTarget>(target);
                    for (int i = 0; i < positions.Length; i++)
                    {
                        positions[i].X += Math.Clamp(targets[i].X - positions[i].X, -Speed, Speed);
                        positions[i].Y += Math.Clamp(targets[i].Y - positions[i].Y, -Speed, Speed);
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Eat",
                Phase = Phase.Simulate,
                All = colonist,
                Reads = ComponentMask.Of(position),
                Writes = ComponentMask.Of(hunger, target),
                After = ["Hunger", "Move"],
                Begin = (in SystemContext context) => state.PrepareEat(context.JobCount),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => state.Eat(in context, in chunk, position, target, hunger),
                End = (in SystemContext context) => state.FinishEat(context.JobCount),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Wander",
                Phase = Phase.Simulate,
                All = colonist,
                Reads = ComponentMask.Of(position, hunger),
                Writes = ComponentMask.Of(target),
                After = ["Eat"],
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => state.Wander(in context, in chunk, position, target, hunger),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Starvation",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(hunger),
                Writes = ComponentMask.Of(health),
                After = ["Eat"],
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    var hungers = chunk.Column<ColonistHunger>(hunger);
                    var healths = chunk.Column<ColonistHealth>(health);
                    for (int i = 0; i < hungers.Length; i++)
                    {
                        if (hungers[i].Value > StarvingThreshold)
                        {
                            healths[i].Value = Math.Max(0, healths[i].Value - 1);
                        }
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Stats",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(hunger, health),
                Begin = (in SystemContext context) => state.PrepareStats(context.JobCount),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => state.Stats(in context, in chunk, hunger, health),
                End = (in SystemContext context) => state.FinishStats(context.JobCount),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "FoodSpawner",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(food),
                Begin = (in SystemContext context) => state.SpawnFood(in context, position, food, foodSignature),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Churn",
                Phase = Phase.Late,
                All = colonist,
                Reads = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => Churn(in context, in chunk, colonist, position, target, hunger, health),
            });
    }

    /// <summary>Populates the World with colonists and food, drawing from <see cref="World.Random"/>, ready to Tick.</summary>
    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var schema = world.Schema;
        var position = schema.Component<ColonistPosition>();
        var target = schema.Component<ColonistTarget>();
        var hunger = schema.Component<ColonistHunger>();
        var health = schema.Component<ColonistHealth>();
        var foodType = schema.Component<FoodItem>();
        var init = world.Random.Derive("init");
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Colonists; i++)
        {
            var block = init.BlockAt((ulong)i);
            var e = buffer.Create(ComponentMask.Of(position, target, hunger, health));
            var at = RandomTile(block[0], block[1]);
            buffer.Add(e, position, at);
            buffer.Add(e, target, new ColonistTarget { X = at.X, Y = at.Y });
            buffer.Add(e, hunger, new ColonistHunger { Value = (block[2] % 500) * 0.1f });
            buffer.Add(e, health, new ColonistHealth { Value = 100 });
        }

        for (int i = 0; i < Food; i++)
        {
            var block = init.BlockAt((ulong)(Colonists + i));
            var e = buffer.Create(ComponentMask.Of(position, foodType));
            buffer.Add(e, position, RandomTile(block[0], block[1]));
            buffer.Add(e, foodType, new FoodItem { Nutrition = 50 });
        }

        world.Playback(buffer);
        world.ResetScratch();
    }

    public static ColonistPosition RandomTile(uint a, uint b)
        => new() { X = ((int)(a % WorldTiles) * TileUnits) + (TileUnits / 2), Y = ((int)(b % WorldTiles) * TileUnits) + (TileUnits / 2) };

    /// <summary>Late: every Tick, the colonists whose slot index falls in this Tick's percent bucket are replaced by newborns at a random tile.</summary>
    private static void Churn(in SystemContext context, in ChunkView chunk, ComponentMask colonist, ComponentType position, ComponentType target, ComponentType hunger, ComponentType health)
    {
        var entities = chunk.Entities;
        int bucket = (int)(context.Tick % (100 / ChurnPercent));
        for (int i = 0; i < entities.Length; i++)
        {
            if (entities[i].Index % (100 / ChurnPercent) != bucket)
            {
                continue;
            }

            context.Commands.Destroy(entities[i]);
            var block = context.Random.BlockAt((ulong)entities[i].Index);
            var born = context.Commands.Create(colonist);
            var at = RandomTile(block[0], block[1]);
            context.Commands.Add(born, position, at);
            context.Commands.Add(born, target, new ColonistTarget { X = at.X, Y = at.Y });
            context.Commands.Add(born, hunger, new ColonistHunger { Value = 0 });
            context.Commands.Add(born, health, new ColonistHealth { Value = 100 });
        }
    }

    /// <summary>Mutable scenario scratch: the food grid, per-job reduction slots and the stats. Grows only during warm-up.</summary>
    private sealed class ScenarioState
    {
        private const int CellShift = 11; // 8 tiles of 256 units
        private const int Dim = WorldTiles * TileUnits >> CellShift;

        private readonly int _foodTarget;
        private readonly bool _sabotage;
        private readonly int[] _cellStart = new int[(Dim * Dim) + 1];
        private readonly int[] _cursor = new int[Dim * Dim];
        private EntityId[] _items;
        private ColonistPosition[] _itemPositions;
        private int _foodCount;

        private int[] _eatenCount = new int[64];
        private ColonistPosition[][] _eatenPositions = CreateSlots(64);
        private ColonistPosition[] _eatenLog;
        private int _eatenLogCount;

        private long[] _hungerSum = new long[64];
        private int[] _starving = new int[64];
        private int[] _count = new int[64];

        public ScenarioState(int foodTarget, bool sabotage)
        {
            _foodTarget = foodTarget;
            _sabotage = sabotage;
            _items = new EntityId[foodTarget * 2];
            _itemPositions = new ColonistPosition[foodTarget * 2];
            _eatenLog = new ColonistPosition[foodTarget];
        }

        public long TotalEaten { get; private set; }

        public int Starving { get; private set; }

        public int Colonists { get; private set; }

        public long HungerTenths { get; private set; }

        private static ColonistPosition[][] CreateSlots(int n)
        {
            var slots = new ColonistPosition[n][];
            for (int i = 0; i < n; i++)
            {
                slots[i] = new ColonistPosition[64];
            }

            return slots;
        }

        // ---- FoodIndex (Begin Phase, serial): counting sort of food into a CSR grid, in chunk order. ----

        public void RebuildFoodGrid(World world, ComponentType position, ComponentType food)
        {
            Array.Clear(_cellStart);
            var query = ComponentMask.Of(position, food);
            int total = world.Query(query).EntityCount();
            if (_items.Length < total)
            {
                _items = new EntityId[total * 2];
                _itemPositions = new ColonistPosition[total * 2];
            }

            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    int sum = 0;
                    for (int c = 0; c < Dim * Dim; c++)
                    {
                        int n = _cellStart[c];
                        _cellStart[c] = sum;
                        _cursor[c] = sum;
                        sum += n;
                    }

                    _cellStart[Dim * Dim] = sum;
                    _foodCount = sum;
                }

                foreach (var chunk in world.Query(query))
                {
                    var positions = chunk.Column<ColonistPosition>(position);
                    var entities = chunk.Entities;
                    for (int i = 0; i < positions.Length; i++)
                    {
                        int cell = ((positions[i].Y >> CellShift) * Dim) + (positions[i].X >> CellShift);
                        if (pass == 0)
                        {
                            _cellStart[cell]++;
                        }
                        else
                        {
                            int k = _cursor[cell]++;
                            _items[k] = entities[i];
                            _itemPositions[k] = positions[i];
                        }
                    }
                }
            }
        }

        /// <summary>Approximate nearest food: ring search over the grid, one extra ring after the first hit; ties by scan order.</summary>
        private bool FindNearest(int x, int y, out EntityId food, out ColonistPosition at)
        {
            int cx = x >> CellShift;
            int cy = y >> CellShift;
            long best = long.MaxValue;
            int bestIndex = -1;
            int foundRing = -1;
            for (int r = 0; r < Dim; r++)
            {
                if (foundRing >= 0 && r > foundRing + 1)
                {
                    break;
                }

                for (int dy = -r; dy <= r; dy++)
                {
                    int y2 = cy + dy;
                    if ((uint)y2 >= Dim)
                    {
                        continue;
                    }

                    bool edgeRow = dy == -r || dy == r;
                    int step = edgeRow ? 1 : Math.Max(1, 2 * r);
                    for (int dx = -r; dx <= r; dx += step)
                    {
                        int x2 = cx + dx;
                        if ((uint)x2 >= Dim)
                        {
                            continue;
                        }

                        int cell = (y2 * Dim) + x2;
                        for (int k = _cellStart[cell], end = _cellStart[cell + 1]; k < end; k++)
                        {
                            long ddx = _itemPositions[k].X - x;
                            long ddy = _itemPositions[k].Y - y;
                            long d = (ddx * ddx) + (ddy * ddy);
                            if (d < best)
                            {
                                best = d;
                                bestIndex = k;
                            }
                        }
                    }
                }

                if (bestIndex >= 0 && foundRing < 0)
                {
                    foundRing = r;
                }
            }

            if (bestIndex < 0)
            {
                food = default;
                at = default;
                return false;
            }

            food = _items[bestIndex];
            at = _itemPositions[bestIndex];
            return true;
        }

        // ---- Eat (Simulate): per-job eaten lists combined in job order at End. ----

        public void PrepareEat(int jobCount)
        {
            if (_eatenCount.Length < jobCount)
            {
                _eatenCount = new int[jobCount * 2];
                var old = _eatenPositions;
                _eatenPositions = CreateSlots(jobCount * 2);
                Array.Copy(old, _eatenPositions, old.Length);
            }

            Array.Clear(_eatenCount, 0, jobCount);
        }

        public void Eat(in SystemContext context, in ChunkView chunk, ComponentType position, ComponentType target, ComponentType hunger)
        {
            var positions = chunk.Column<ColonistPosition>(position);
            var targets = chunk.Column<ColonistTarget>(target);
            var hungers = chunk.Column<ColonistHunger>(hunger);
            ref var slot = ref _eatenPositions[context.JobIndex];
            int eaten = 0;
            for (int i = 0; i < hungers.Length; i++)
            {
                if (hungers[i].Value < HungryThreshold || !FindNearest(positions[i].X, positions[i].Y, out var food, out var at))
                {
                    continue;
                }

                if (Math.Abs(at.X - positions[i].X) <= TileUnits && Math.Abs(at.Y - positions[i].Y) <= TileUnits)
                {
                    // Two colonists may reach one food in the same Tick; the second Destroy is skipped as stale (deterministically).
                    context.Commands.Destroy(food);
                    hungers[i].Value = 0;
                    if (eaten == slot.Length)
                    {
                        Array.Resize(ref slot, slot.Length * 2); // warm-up only
                    }

                    slot[eaten++] = at;
                }
                else
                {
                    targets[i].X = at.X;
                    targets[i].Y = at.Y;
                }
            }

            _eatenCount[context.JobIndex] = eaten;
        }

        public void FinishEat(int jobCount)
        {
            int sum = 0;
            for (int j = 0; j < jobCount; j++)
            {
                sum += _eatenCount[j];
            }

            if (_eatenLog.Length < sum)
            {
                _eatenLog = new ColonistPosition[sum * 2];
            }

            _eatenLogCount = 0;
            for (int j = 0; j < jobCount; j++)
            {
                Array.Copy(_eatenPositions[j], 0, _eatenLog, _eatenLogCount, _eatenCount[j]);
                _eatenLogCount += _eatenCount[j];
            }

            TotalEaten += sum;
        }

        // ---- Wander (Simulate): fed colonists at their target pick a random tile; RNG keyed by Entity, never by thread. ----

        public void Wander(in SystemContext context, in ChunkView chunk, ComponentType position, ComponentType target, ComponentType hunger)
        {
            var positions = chunk.Column<ColonistPosition>(position);
            var targets = chunk.Column<ColonistTarget>(target);
            var hungers = chunk.Column<ColonistHunger>(hunger);
            var entities = chunk.Entities;
            // Negative control (TASK-7 AC #7, TASK-10 AC #2): keying the stream by the worker thread must break the cross-thread hash comparison.
            var random = _sabotage ? context.Random.Derive((ulong)Environment.CurrentManagedThreadId) : context.Random;
            for (int i = 0; i < positions.Length; i++)
            {
                if (hungers[i].Value >= HungryThreshold || positions[i].X != targets[i].X || positions[i].Y != targets[i].Y)
                {
                    continue;
                }

                var block = random.BlockAt(entities[i].Value);
                var at = RandomTile(block[0], block[1]);
                targets[i].X = at.X;
                targets[i].Y = at.Y;
            }
        }

        // ---- Stats (Late): per-chunk reduction combined in job order. ----

        public void PrepareStats(int jobCount)
        {
            if (_hungerSum.Length < jobCount)
            {
                _hungerSum = new long[jobCount * 2];
                _starving = new int[jobCount * 2];
                _count = new int[jobCount * 2];
            }
        }

        public void Stats(in SystemContext context, in ChunkView chunk, ComponentType hunger, ComponentType health)
        {
            var hungers = chunk.Column<ColonistHunger>(hunger);
            var healths = chunk.Column<ColonistHealth>(health);
            long sum = 0;
            int starving = 0;
            for (int i = 0; i < hungers.Length; i++)
            {
                sum += (long)(hungers[i].Value * 10f);
                if (healths[i].Value < 100)
                {
                    starving++;
                }
            }

            _hungerSum[context.JobIndex] = sum;
            _starving[context.JobIndex] = starving;
            _count[context.JobIndex] = hungers.Length;
        }

        public void FinishStats(int jobCount)
        {
            long sum = 0;
            int starving = 0;
            int n = 0;
            for (int j = 0; j < jobCount; j++)
            {
                sum += _hungerSum[j];
                starving += _starving[j];
                n += _count[j];
            }

            HungerTenths = sum;
            Starving = starving;
            Colonists = n;
        }

        // ---- FoodSpawner (Late, serial): top food back up, regrowing near where it was eaten. ----

        public void SpawnFood(in SystemContext context, ComponentType position, ComponentType food, ComponentMask signature)
        {
            int have = context.World.Query(signature).EntityCount();
            const int max = (WorldTiles * TileUnits) - (TileUnits / 2);
            for (int i = have, k = 0; i < _foodTarget; i++, k++)
            {
                var block = context.Random.BlockAt((ulong)i);
                ColonistPosition at;
                if (k < _eatenLogCount)
                {
                    at = _eatenLog[k];
                    at.X = Math.Clamp(at.X + (((int)(block[2] % 17) - 8) * TileUnits), TileUnits / 2, max);
                    at.Y = Math.Clamp(at.Y + (((int)(block[3] % 17) - 8) * TileUnits), TileUnits / 2, max);
                }
                else
                {
                    at = RandomTile(block[0], block[1]);
                }

                var e = context.Commands.Create(signature);
                context.Commands.Add(e, position, at);
                context.Commands.Add(e, food, new FoodItem { Nutrition = 50 });
            }
        }
    }
}
