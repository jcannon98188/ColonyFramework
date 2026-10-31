namespace Colony.Core.Tests.Scheduling;

/// <summary>AC #2: declared access sets, waves, stable topological order and build-time failures.</summary>
public sealed class SystemScheduleTests
{
    private static readonly ComponentType Position = new(0);
    private static readonly ComponentType Hunger = new(1);
    private static readonly ComponentType Health = new(2);

    private static WorldBuilder Builder() => new WorldBuilder()
        .AddComponent<Position>("position")
        .AddComponent<Hunger>("hunger")
        .AddComponent<Health>("health");

    private static SystemDefinition System(string name, ComponentMask reads = default, ComponentMask writes = default, string[]? after = null, string[]? before = null, Phase? phase = null)
        => new()
        {
            Name = name,
            Phase = phase ?? Phase.Simulate,
            Reads = reads,
            Writes = writes,
            After = after ?? [],
            Before = before ?? [],
            Begin = (in SystemContext _) => { },
        };

    [Fact]
    public void Unconstrained_non_conflicting_systems_keep_registration_order_and_share_one_wave()
    {
        var schema = Builder()
            .AddSystem(System("A", writes: ComponentMask.Of(Position)))
            .AddSystem(System("B", writes: ComponentMask.Of(Hunger)))
            .AddSystem(System("C", reads: ComponentMask.Of(Health)))
            .Build();

        Assert.Equal([0, 1, 2], schema.Systems.ToArray().Select(s => s.Order));
        Assert.All(schema.Systems.ToArray(), s => Assert.Equal(0, s.Wave));
        Assert.Equal(Phase.Simulate, schema.System("B").Phase);
    }

    [Fact]
    public void After_and_before_constraints_order_systems_with_ties_broken_by_system_id()
    {
        var schema = Builder()
            .AddSystem(System("C"))
            .AddSystem(System("A", before: ["C", "B"]))
            .AddSystem(System("B"))
            .Build();

        // A must precede both; C (id 0) wins the tie against B (id 2).
        Assert.Equal(0, schema.System("A").Order);
        Assert.Equal(1, schema.System("C").Order);
        Assert.Equal(2, schema.System("B").Order);
        Assert.Equal(0, schema.System("A").Wave);
        Assert.Equal(1, schema.System("C").Wave);
        Assert.Equal(1, schema.System("B").Wave);
    }

    [Fact]
    public void Conflicting_systems_never_share_a_wave_and_non_conflicting_ones_do()
    {
        var schema = Builder()
            .AddSystem(System("Hunger", writes: ComponentMask.Of(Hunger)))
            .AddSystem(System("Move", writes: ComponentMask.Of(Position)))
            .AddSystem(System("Eat", reads: ComponentMask.Of(Position), writes: ComponentMask.Of(Hunger), after: ["Hunger", "Move"]))
            .AddSystem(System("Starve", reads: ComponentMask.Of(Hunger), writes: ComponentMask.Of(Health), after: ["Eat"]))
            .AddSystem(System("Wander", reads: ComponentMask.Of(Position, Hunger), writes: ComponentMask.Of(Position), after: ["Eat"]))
            .Build();

        Assert.Equal(0, schema.System("Hunger").Wave);
        Assert.Equal(0, schema.System("Move").Wave);
        Assert.Equal(1, schema.System("Eat").Wave);
        Assert.Equal(2, schema.System("Starve").Wave);
        Assert.Equal(2, schema.System("Wander").Wave);
    }

    [Fact]
    public void Transitive_constraints_resolve_a_conflict()
    {
        var schema = Builder()
            .AddSystem(System("A", writes: ComponentMask.Of(Hunger)))
            .AddSystem(System("B", after: ["A"]))
            .AddSystem(System("C", reads: ComponentMask.Of(Hunger), after: ["B"]))
            .Build();

        Assert.Equal(2, schema.System("C").Wave);
    }

    [Fact]
    public void A_cycle_fails_at_build_naming_the_systems()
    {
        var builder = Builder()
            .AddSystem(System("A", after: ["B"]))
            .AddSystem(System("B", after: ["A"]));

        var error = Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
        Assert.Contains("A", error.Message, StringComparison.Ordinal);
        Assert.Contains("B", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_writers_or_a_writer_and_a_reader_without_an_order_are_ambiguous_and_fail_at_build()
    {
        var writers = Builder()
            .AddSystem(System("A", writes: ComponentMask.Of(Hunger)))
            .AddSystem(System("B", writes: ComponentMask.Of(Hunger)));
        var readerWriter = Builder()
            .AddSystem(System("A", reads: ComponentMask.Of(Hunger)))
            .AddSystem(System("B", writes: ComponentMask.Of(Hunger)));
        var readers = Builder()
            .AddSystem(System("A", reads: ComponentMask.Of(Hunger)))
            .AddSystem(System("B", reads: ComponentMask.Of(Hunger)));

        var error = Assert.Throws<InvalidOperationException>(writers.Build);
        Assert.Contains("'A'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'B'", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(readerWriter.Build);
        Assert.Equal(2, readers.Build().Systems.Length);
    }

    [Fact]
    public void Constraints_must_name_registered_systems_in_the_same_phase()
    {
        var unknown = Builder().AddSystem(System("A", after: ["Nobody"]));
        var crossPhase = Builder()
            .AddSystem(System("A", phase: Phase.Begin))
            .AddSystem(System("B", after: ["A"], phase: Phase.Simulate));
        var self = Builder().AddSystem(System("A", after: ["A"]));

        Assert.Throws<InvalidOperationException>(unknown.Build);
        Assert.Throws<InvalidOperationException>(crossPhase.Build);
        Assert.Throws<InvalidOperationException>(self.Build);
    }

    [Fact]
    public void Registration_is_validated_eagerly()
    {
        var builder = Builder().AddSystem(System("A"));

        Assert.Throws<ArgumentException>(() => builder.AddSystem(System("A")));
        Assert.Throws<ArgumentException>(() => builder.AddSystem(new SystemDefinition { Name = "Empty" }));
        Assert.Throws<ArgumentNullException>(() => builder.AddSystem(null!));
        Assert.Throws<InvalidOperationException>(() => Builder().AddSystem(new SystemDefinition { Name = "Bad", Every = 0, Begin = (in SystemContext _) => { } }).Build());
        Assert.Throws<InvalidOperationException>(() => Builder().AddSystem(new SystemDefinition { Name = "Bad", Every = 2, Offset = 2, Begin = (in SystemContext _) => { } }).Build());
        Assert.Throws<InvalidOperationException>(() => Builder().AddSystem(new SystemDefinition { Name = "Bad", Writes = ComponentMask.Of(new ComponentType(9)), Begin = (in SystemContext _) => { } }).Build());
        Assert.Throws<InvalidOperationException>(() => Builder().AddSystem(System("Bad", phase: new WorldBuilder().AddPhase("Elsewhere", Phase.Begin))).Build());
    }

    [Fact]
    public void Every_n_ticks_runs_on_the_offset_tick_and_the_default_offset_comes_from_the_system_id()
    {
        var ran = new List<(string, long)>();
        var schema = Builder()
            .AddSystem(new SystemDefinition { Name = "Filler", Begin = (in SystemContext _) => { } })
            .AddSystem(new SystemDefinition { Name = "Derived", Every = 3, Begin = (in SystemContext c) => ran.Add(("Derived", c.Tick)) })
            .AddSystem(new SystemDefinition { Name = "Explicit", Every = 2, Offset = 1, Begin = (in SystemContext c) => ran.Add(("Explicit", c.Tick)) })
            .Build();

        Assert.Equal(1, schema.System("Derived").Offset);
        Assert.Equal(1, schema.System("Explicit").Offset);
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        for (int i = 0; i < 6; i++)
        {
            world.Tick();
        }

        Assert.Equal([("Derived", 1L), ("Explicit", 1L), ("Explicit", 3L), ("Derived", 4L), ("Explicit", 5L)], ran);
    }
}
