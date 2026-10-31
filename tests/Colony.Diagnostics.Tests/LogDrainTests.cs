namespace Colony.Diagnostics.Tests;

/// <summary>TASK-17 AC #3: text is resolved only on drain, lines carry the Tick and System, and the order is thread-independent.</summary>
public sealed class LogDrainTests
{
    public struct Counter
    {
        public int Value;
    }

    [Fact]
    public void Entries_are_formatted_on_drain_in_tick_system_job_order_regardless_of_thread_count()
    {
        string one = Run(workers: 1);
        string four = Run(workers: 4);

        Assert.Equal(one, four);
        var lines = one.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("[tick 0] Count: tick 0 has 3 jobs", lines[0]);
        Assert.Equal("[tick 0] Count#0: chunk 0 holds 1 entities, first 0", lines[1]);
        Assert.Equal("[tick 0] Count#2: chunk 2 holds 1 entities, first 2", lines[3]);
        Assert.Equal("[tick 0] Report: total 3", lines[4]);
        Assert.Equal("[tick 1] Count: tick 1 has 3 jobs", lines[5]);
        Assert.Equal("[tick 2] host: host note 42", lines[^1]);
        Assert.Equal(11, lines.Length);
    }

    /// <summary>
    /// Workers run their own job stripe and then steal, so a worker's ring is not in job order; the drain
    /// must sort and merge, not append. Repeated so a lucky schedule cannot hide a bad order.
    /// </summary>
    [Fact]
    public void Drained_text_is_identical_for_1_and_8_workers_over_repeated_runs()
    {
        string expected = Run(workers: 1, chunks: 24);
        for (int repeat = 0; repeat < 6; repeat++)
        {
            Assert.Equal(expected, Run(workers: 8, chunks: 24));
            Assert.Equal(expected, Run(workers: 3, chunks: 24));
        }
    }

    [Fact]
    public void An_unregistered_message_id_is_still_printable()
    {
        var schema = new WorldBuilder().AddComponent<Counter>("counter").AddSystem(new SystemDefinition { Name = "S", Begin = (in SystemContext _) => { } }).Build();
        string text = LogDrain.Format(schema, new LogEntry(0, 0, -1, new LogMessageId(7), 1, 2, 3, 4));
        Assert.Contains("unregistered message 7", text, StringComparison.Ordinal);
    }

    private static string Run(int workers, int chunks = 3)
    {
        var builder = new WorldBuilder().AddComponent<Counter>("counter").AddComponent<Position>("position").AddComponent<Tag>("tag");
        var counter = builder.Component<Counter>();
        var position = builder.Component<Position>();
        var tag = builder.Component<Tag>();
        var jobs = builder.AddLogMessage("tick {0} has {1} jobs");
        var chunk = builder.AddLogMessage("chunk {0} holds {1} entities, first {2}");
        var total = builder.AddLogMessage("total {0}");
        var note = builder.AddLogMessage("host note {0}");
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Count",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(counter),
                Begin = (in SystemContext context) => context.Log(jobs, context.Tick, context.JobCount),
                ForEachChunk = (in SystemContext context, in ChunkView view) => context.Log(chunk, context.JobIndex, view.Count, view.Entities[0].Index),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Report",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(counter),
                Begin = (in SystemContext context) => context.Log(total, context.World.EntityCount),
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = workers });

        // Three one-Entity archetypes make three chunk jobs that land on different workers; more chunks
        // (Counter.Value is the archetype's per-chunk capacity) come from filling the first archetype.
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(counter));
        buffer.Create(ComponentMask.Of(counter, position));
        buffer.Create(ComponentMask.Of(counter, position, tag));
        world.Playback(buffer);
        int perChunk = 0;
        foreach (var view in world.Query(ComponentMask.Of(counter), ComponentMask.Of(position)))
        {
            perChunk = view.Capacity;
        }

        for (int i = 0; i < (chunks - 3) * perChunk; i++)
        {
            buffer.Create(ComponentMask.Of(counter));
        }

        world.Playback(buffer);
        world.ResetScratch();

        world.Tick();
        world.Tick();
        world.Diagnostics.Log(note, 42);

        var writer = new StringWriter();
        int drained = new LogDrain(world).Drain(writer);
        Assert.Equal(2 * (chunks + 2) + 1, drained);
        return writer.ToString();
    }

    public struct Position
    {
        public int X;
    }

    public struct Tag
    {
        public byte Value;
    }
}
