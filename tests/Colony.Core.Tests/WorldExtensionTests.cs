using Colony.Hashing;

namespace Colony.Core.Tests;

/// <summary>
/// The generic World extension hook that packages outside Core (Fields, spatial index, pathfinding) build on:
/// registered on the builder, created per World, hashed into the World hash, told about Tick boundaries, and
/// able to run index-keyed jobs on the World's worker pool.
/// </summary>
public sealed class WorldExtensionTests
{
    private sealed class CounterDefinition : WorldExtensionDefinition
    {
        public CounterDefinition(ulong layoutHash, string name = "counter")
        {
            LayoutHash = layoutHash;
            Name = name;
        }

        public override string Name { get; }

        public override ulong LayoutHash { get; }

        public bool Frozen { get; private set; }

        public override void Freeze() => Frozen = true;

        public override IWorldExtension Create(World world) => new Counter(world);
    }

    private sealed class Counter : IWorldExtension
    {
        public Counter(World world)
        {
            World = world;
        }

        public World World { get; }

        public long State { get; set; }

        public int BeginTicks { get; private set; }

        public int EndTicks { get; private set; }

        public List<Phase> PhasesEnded { get; } = [];

        public bool Disposed { get; private set; }

        public void BeginTick(long tick) => BeginTicks++;

        public void EndPhase(Phase phase) => PhasesEnded.Add(phase);

        public void EndTick(long tick) => EndTicks++;

        public void Hash(ref Hash64State state) => state.Append(State);

        public void Dispose() => Disposed = true;
    }

    private sealed class SquareJob : IIndexedJob
    {
        public long[] Results = [];

        public void Execute(int index) => Results[index] = (long)index * index;
    }

    private static WorldSchema Schema(ulong layoutHash, CounterDefinition? definition = null)
        => new WorldBuilder().AddExtension(definition ?? new CounterDefinition(layoutHash)).DebugValidation(false).Build();

    [Fact]
    public void An_extension_is_created_per_world_and_its_layout_hash_is_part_of_the_schema_hash()
    {
        var definition = new CounterDefinition(1);
        var schema = Schema(1, definition);
        Assert.True(definition.Frozen);
        Assert.NotEqual(Schema(2).SchemaHash, schema.SchemaHash);
        Assert.Equal(Schema(1).SchemaHash, schema.SchemaHash);

        using var a = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        Assert.NotSame(a.Extension<Counter>(), b.Extension<Counter>());
        Assert.Same(a, a.Extension<Counter>().World);
        Assert.Same(a.Extension<Counter>(), a.Extension<Counter>());
    }

    [Theory]
    [InlineData(WorldHashTree.TickGroup)]
    [InlineData(WorldHashTree.ComponentsGroup)]
    [InlineData(WorldHashTree.ResourcesGroup)]
    [InlineData(WorldHashTree.TablesGroup)]
    [InlineData(WorldHashTree.RandomGroup)]
    public void An_extension_may_not_take_a_hash_tree_group_name_core_reserves(string name)
    {
        var e = Assert.Throws<ArgumentException>(() => new WorldBuilder().AddExtension(new CounterDefinition(1, name)));
        Assert.Contains(name, e.Message, StringComparison.Ordinal);
        Assert.Contains("hash tree", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_state_is_part_of_the_world_hash()
    {
        var schema = Schema(1);
        using var a = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        Assert.Equal(a.Hash(), b.Hash());

        b.Extension<Counter>().State = 7;
        Assert.NotEqual(a.Hash(), b.Hash());
    }

    [Fact]
    public void Tick_boundaries_are_reported_in_order_and_dispose_follows_the_world()
    {
        var schema = Schema(1);
        var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var counter = world.Extension<Counter>();
        world.Tick();
        world.Tick();

        Assert.Equal(2, counter.BeginTicks);
        Assert.Equal(2, counter.EndTicks);
        Assert.Equal(schema.Phases.ToArray().Concat(schema.Phases.ToArray()), counter.PhasesEnded);

        world.Dispose();
        Assert.True(counter.Disposed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RunParallel_runs_every_index_exactly_once_before_returning(int workers)
    {
        var schema = Schema(1);
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = workers });
        var job = new SquareJob { Results = new long[1000] };
        Array.Fill(job.Results, -1L);

        world.RunParallel(job.Results.Length, job);

        for (int i = 0; i < job.Results.Length; i++)
        {
            Assert.Equal((long)i * i, job.Results[i]);
        }

        world.RunParallel(0, job);
    }

    [Fact]
    public void RunParallel_from_a_serial_system_hook_works_and_from_a_chunk_job_faults()
    {
        var job = new SquareJob { Results = new long[64] };
        var builder = new WorldBuilder().AddComponent<int>("int").DebugValidation(false);
        var component = builder.Component<int>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "FromBegin",
                Begin = (in SystemContext context) => context.World.RunParallel(job.Results.Length, job),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "FromChunk",
                Phase = Phase.Late,
                Writes = ComponentMask.Of(component),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => context.World.RunParallel(1, job),
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        world.Tick();
        Assert.Equal(63L * 63, job.Results[63]);

        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(component));
        world.Playback(buffer);
        world.ResetScratch();

        var fault = Assert.Throws<SystemFaultException>(() => world.Tick());
        Assert.Equal("FromChunk", fault.SystemName);
        Assert.IsType<InvalidOperationException>(fault.InnerException);
    }
}
