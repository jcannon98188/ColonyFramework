using Colony;

namespace Colony.Scenario;

// The acceptance oracle for TASK-7 (spec "Core slice" acceptance), extended by TASK-8: the ECS prototype's
// colonist scenario rebuilt on the real World.Tick with hand-written Systems. 100k colonists wander, get
// hungry, walk to the nearest food and eat it (destroying it through command buffers; a second colonist
// reaching the same food loses the conflict and produces a StructuralFailure event); food regrows where
// FoodEaten events say it was eaten; starved colonists have their target disabled (an enable bit, not a
// structural change) and an every-4-Ticks Recover System puts them back on their feet; Stats writes a
// ColonyStats Resource that the World hash covers; 1% of the colony churns every Tick.
// This file has no test-framework dependency: the Native AOT host compiles it as well.
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

/// <summary>Event emitted from the Eat chunk jobs: where food was eaten this Tick.</summary>
public struct FoodEaten
{
    public int X;
    public int Y;
}

/// <summary>Resource written by Stats at the end of every Tick; part of the World hash.</summary>
public struct ColonyStats
{
    public long HungerTenths;
    public int Starving;
    public int Colonists;
    public long TotalEaten;
    public long Failures;
    public long ImmobileTicks;
    public long Recovered;
}

public static class ColonistScenario
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
    public const int RecoverEvery = 4;
    public const int RecoverHealth = 10;

    /// <summary>Builds the schema and Systems. The scenario's scratch state is per schema, so create one World per schema.</summary>
    public static WorldSchema BuildSchema(bool sabotage = false, int foodTarget = DefaultFood)
        => Compose(new WorldBuilder(), sabotage, foodTarget).Build();

    /// <summary>
    /// Adds the scenario's Components, Resources and Systems to <paramref name="builder"/> without building it:
    /// the Colony.Testing harness composes its own builder (see Colony.Testing.Tests' ColonistHarnessScenario).
    /// </summary>
    public static WorldBuilder Compose(WorldBuilder builder, bool sabotage = false, int foodTarget = DefaultFood)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .AddComponent<ColonistPosition>("colonist.position")
            .AddComponent<ColonistTarget>("colonist.target")
            .AddComponent<ColonistHunger>("colonist.hunger")
            .AddComponent<ColonistHealth>("colonist.health")
            .AddComponent<FoodItem>("food")
            .AddEvent<FoodEaten>("food.eaten")
            .AddResource<ColonyStats>("colony.stats")
            .DebugValidation(false);
        var position = builder.Component<ColonistPosition>();
        var target = builder.Component<ColonistTarget>();
        var hunger = builder.Component<ColonistHunger>();
        var health = builder.Component<ColonistHealth>();
        var food = builder.Component<FoodItem>();
        var foodEaten = builder.Resource<FoodEaten>();
        var stats = builder.Resource<ColonyStats>();
        var failures = builder.StructuralFailures;
        var colonist = ComponentMask.Of(position, target, hunger, health);
        var foodSignature = ComponentMask.Of(position, food);
        var state = new ScenarioState(foodTarget, sabotage);

        return builder
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
                    // Only colonists whose target is enabled move: Starvation disables it, Recover enables it again.
                    var positions = chunk.Column<ColonistPosition>(position);
                    var targets = chunk.Column<ColonistTarget>(target);
                    foreach (int i in chunk.EnabledRows(target))
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
                WritesResources = ResourceMask.Of(foodEaten),
                After = ["Hunger", "Move"],
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => state.Eat(in context, in chunk, position, target, hunger, foodEaten),
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
                Writes = ComponentMask.Of(health, target),
                After = ["Eat", "Wander"],
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    var hungers = chunk.Column<ColonistHunger>(hunger);
                    var healths = chunk.Column<ColonistHealth>(health);
                    for (int i = 0; i < hungers.Length; i++)
                    {
                        if (hungers[i].Value > StarvingThreshold)
                        {
                            healths[i].Value = Math.Max(0, healths[i].Value - 1);
                            if (healths[i].Value == 0)
                            {
                                chunk.SetEnabled(target, i, false);
                            }
                        }
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Recover",
                Phase = Phase.Simulate,
                Every = RecoverEvery,
                Writes = ComponentMask.Of(health, hunger, target),
                IgnoreEnableBits = true,
                After = ["Starvation"],
                Begin = (in SystemContext context) => state.PrepareRecover(context.JobCount),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => state.Recover(in context, in chunk, health, hunger, target),
                WritesResources = ResourceMask.Of(stats),
                End = (in SystemContext context) => state.FinishRecover(in context, stats),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Stats",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(hunger, health, target),
                ReadsResources = ResourceMask.Of(foodEaten, failures),
                WritesResources = ResourceMask.Of(stats),
                Begin = (in SystemContext context) => state.PrepareStats(context.JobCount),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => state.Stats(in context, in chunk, hunger, health, target),
                End = (in SystemContext context) => state.FinishStats(in context, stats, foodEaten, failures),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "FoodSpawner",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(food),
                ReadsResources = ResourceMask.Of(foodEaten),
                Begin = (in SystemContext context) => state.SpawnFood(in context, position, food, foodSignature, foodEaten),
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

    /// <summary>Creates a World on <paramref name="workers"/> threads populated with colonists and food, ready to Tick.</summary>
    public static World CreateWorld(WorldSchema schema, int workers, ulong seed = DefaultSeed, int colonists = DefaultColonists, int food = DefaultFood, Colony.Diagnostics.DiagnosticsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var world = schema.CreateWorld(new WorldOptions
        {
            Seed = seed,
            WorkerThreads = workers,
            InitialEntityCapacity = (colonists + food) * 2,
            Diagnostics = options ?? Colony.Diagnostics.DiagnosticsOptions.Default,
        });
        Populate(world, colonists, food);
        return world;
    }

    /// <summary>Fills a freshly created World with <paramref name="colonists"/> colonists and <paramref name="food"/> food items and resets its scratch arena.</summary>
    public static void Populate(World world, int colonists = DefaultColonists, int food = DefaultFood)
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
        for (int i = 0; i < colonists; i++)
        {
            var block = init.BlockAt((ulong)i);
            var e = buffer.Create(ComponentMask.Of(position, target, hunger, health));
            var at = RandomTile(block[0], block[1]);
            buffer.Add(e, position, at);
            buffer.Add(e, target, new ColonistTarget { X = at.X, Y = at.Y });
            // Some start already starving and frail, so starvation and recovery happen within a short run.
            buffer.Add(e, hunger, new ColonistHunger { Value = (block[2] % 1200) * 0.1f });
            buffer.Add(e, health, new ColonistHealth { Value = 1 + (int)(block[3] % 20) });
        }

        for (int i = 0; i < food; i++)
        {
            var block = init.BlockAt((ulong)(colonists + i));
            var e = buffer.Create(ComponentMask.Of(position, foodType));
            buffer.Add(e, position, RandomTile(block[0], block[1]));
            buffer.Add(e, foodType, new FoodItem { Nutrition = 50 });
        }

        world.Playback(buffer);
        world.ResetScratch();
    }

    /// <summary>Runs <paramref name="ticks"/> Ticks and returns the World hash after each Tick listed in <paramref name="checkpoints"/>, plus the final hash last.</summary>
    public static ulong[] Run(World world, int ticks, ReadOnlySpan<int> checkpoints)
    {
        ArgumentNullException.ThrowIfNull(world);
        var hashes = new ulong[checkpoints.Length + 1];
        int next = 0;
        for (int t = 1; t <= ticks; t++)
        {
            world.Tick();
            if (next < checkpoints.Length && checkpoints[next] == t)
            {
                hashes[next++] = world.Hash();
            }
        }

        hashes[^1] = world.Hash();
        return hashes;
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

    /// <summary>Mutable scenario scratch: the food grid and per-job reduction slots. Grows only during warm-up.</summary>
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

        private long[] _hungerSum = new long[64];
        private int[] _starving = new int[64];
        private int[] _count = new int[64];
        private int[] _immobile = new int[64];
        private int[] _recovered = new int[64];

        public ScenarioState(int foodTarget, bool sabotage)
        {
            _foodTarget = foodTarget;
            _sabotage = sabotage;
            _items = new EntityId[foodTarget * 2];
            _itemPositions = new ColonistPosition[foodTarget * 2];
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

        // ---- Eat (Simulate): eaten food is reported through FoodEaten events, merged in job then record order. ----

        public void Eat(in SystemContext context, in ChunkView chunk, ComponentType position, ComponentType target, ComponentType hunger, ResourceType foodEaten)
        {
            var positions = chunk.Column<ColonistPosition>(position);
            var targets = chunk.Column<ColonistTarget>(target);
            var hungers = chunk.Column<ColonistHunger>(hunger);
            for (int i = 0; i < hungers.Length; i++)
            {
                if (hungers[i].Value < HungryThreshold || !FindNearest(positions[i].X, positions[i].Y, out var food, out var at))
                {
                    continue;
                }

                if (Math.Abs(at.X - positions[i].X) <= TileUnits && Math.Abs(at.Y - positions[i].Y) <= TileUnits)
                {
                    // Two colonists may reach one food in the same Tick: the second Destroy loses the conflict
                    // (deterministically, by System/job/record order) and becomes a StructuralFailure event.
                    context.Commands.Destroy(food);
                    hungers[i].Value = 0;
                    context.Emit(foodEaten, new FoodEaten { X = at.X, Y = at.Y });
                }
                else
                {
                    targets[i].X = at.X;
                    targets[i].Y = at.Y;
                }
            }
        }

        // ---- Wander (Simulate): fed colonists at their target pick a random tile; RNG keyed by Entity, never by thread. ----

        public void Wander(in SystemContext context, in ChunkView chunk, ComponentType position, ComponentType target, ComponentType hunger)
        {
            var positions = chunk.Column<ColonistPosition>(position);
            var targets = chunk.Column<ColonistTarget>(target);
            var hungers = chunk.Column<ColonistHunger>(hunger);
            var entities = chunk.Entities;
            // Negative control (AC #7): keying the stream by the worker count (not the thread id, which can coincide across runs on a small runner) must break the cross-thread hash comparison.
            var random = _sabotage ? context.Random.Derive((ulong)context.World.WorkerCount) : context.Random;
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

        // ---- Recover (Simulate, every 4 Ticks, sees fully disabled chunks): starved colonists get back up. ----

        public void PrepareRecover(int jobCount)
        {
            if (_recovered.Length < jobCount)
            {
                _recovered = new int[jobCount * 2];
            }
        }

        public void Recover(in SystemContext context, in ChunkView chunk, ComponentType health, ComponentType hunger, ComponentType target)
        {
            var healths = chunk.Column<ColonistHealth>(health);
            var hungers = chunk.Column<ColonistHunger>(hunger);
            int recovered = 0;
            for (int i = 0; i < healths.Length; i++)
            {
                if (healths[i].Value == 0)
                {
                    healths[i].Value = RecoverHealth;
                    hungers[i].Value = 0;
                    chunk.SetEnabled(target, i, true);
                    recovered++;
                }
            }

            _recovered[context.JobIndex] = recovered;
        }

        /// <summary>The running total lives in the ColonyStats Resource (World state), not in scenario scratch, so a loaded World continues it.</summary>
        public void FinishRecover(in SystemContext context, ResourceType statsType)
        {
            long recovered = 0;
            for (int j = 0; j < context.JobCount; j++)
            {
                recovered += _recovered[j];
            }

            context.Resource<ColonyStats>(statsType).Recovered += recovered;
        }

        // ---- Stats (Late): per-chunk reduction combined in job order into the ColonyStats Resource. ----

        public void PrepareStats(int jobCount)
        {
            if (_hungerSum.Length < jobCount)
            {
                _hungerSum = new long[jobCount * 2];
                _starving = new int[jobCount * 2];
                _count = new int[jobCount * 2];
                _immobile = new int[jobCount * 2];
            }
        }

        public void Stats(in SystemContext context, in ChunkView chunk, ComponentType hunger, ComponentType health, ComponentType target)
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
            _immobile[context.JobIndex] = chunk.Count - chunk.EnabledCount(target);
        }

        public void FinishStats(in SystemContext context, ResourceType statsType, ResourceType foodEaten, ResourceType failures)
        {
            long sum = 0;
            int starving = 0;
            int n = 0;
            int immobile = 0;
            for (int j = 0; j < context.JobCount; j++)
            {
                sum += _hungerSum[j];
                starving += _starving[j];
                n += _count[j];
                immobile += _immobile[j];
            }

            ref var stats = ref context.Resource<ColonyStats>(statsType);
            stats.HungerTenths = sum;
            stats.Starving = starving;
            stats.Colonists = n;
            stats.TotalEaten += context.Events<FoodEaten>(foodEaten).Length;
            stats.Failures += context.Events<StructuralFailure>(failures).Length;
            stats.ImmobileTicks += immobile;
        }

        // ---- FoodSpawner (Late, serial): top food back up, regrowing near where the FoodEaten events say it was eaten. ----

        public void SpawnFood(in SystemContext context, ComponentType position, ComponentType food, ComponentMask signature, ResourceType foodEaten)
        {
            int have = context.World.Query(signature).EntityCount();
            var eaten = context.Events<FoodEaten>(foodEaten);
            const int max = (WorldTiles * TileUnits) - (TileUnits / 2);
            for (int i = have, k = 0; i < _foodTarget; i++, k++)
            {
                var block = context.Random.BlockAt((ulong)i);
                ColonistPosition at;
                if (k < eaten.Length)
                {
                    at = new ColonistPosition { X = eaten[k].X, Y = eaten[k].Y };
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
