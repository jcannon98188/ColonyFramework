using Colony.Fields;

namespace Colony.Fields.Tests;

/// <summary>AC #4: per-chunk per-Field change versions advance only on writes; the change feed lists changed chunks per Tick in deterministic order.</summary>
public sealed class ChangeFeedTests
{
    private static (WorldSchema Schema, Field<int> Height, Field<byte> Soil) BuildSchema()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(new WorldSize(64, 64, 16)); // 4 x 4 x 1 chunks
        var height = fields.AddField<int>("height");
        var soil = fields.AddField<byte>("soil");
        return (builder.Build(), height, soil);
    }

    [Fact]
    public void Versions_start_at_zero_and_advance_only_when_a_chunk_is_written()
    {
        var (schema, height, _) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var accessor = fields.Accessor(height);

        Assert.Equal(0u, fields.VersionOf(height, 0));
        Assert.Equal(1u, fields.CurrentVersion); // before Tick 0 runs, writes belong to Tick 0

        accessor[0, 0, 0] = 5;
        Assert.Equal(1u, fields.VersionOf(height, 0));
        Assert.Equal(0u, fields.VersionOf(height, 1));
        _ = accessor[20, 0, 0]; // a read does not count
        Assert.Equal(0u, fields.VersionOf(height, fields.OrdinalOf(new ChunkCoord(1, 0, 0))));

        world.Tick();
        world.Tick();
        Assert.Equal(3u, fields.CurrentVersion);
        Assert.Equal(1u, fields.VersionOf(height, 0)); // untouched since
        accessor.WriteChunk(0);
        Assert.Equal(3u, fields.VersionOf(height, 0));

        // Storing the uniform value into a uniform chunk changes nothing, so it is not a write.
        int ordinal = fields.OrdinalOf(new ChunkCoord(2, 2, 0));
        accessor[32, 32, 0] = 0;
        Assert.Equal(0u, fields.VersionOf(height, ordinal));
        Assert.True(accessor.IsUniform(ordinal));
    }

    [Fact]
    public void The_feed_lists_the_chunks_changed_in_a_tick_by_field_then_morton_order()
    {
        var (schema, height, soil) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var heights = fields.Accessor(height);
        var soils = fields.Accessor(soil);

        // Write in a scrambled order: the feed must still come out sorted.
        soils[0, 0, 0] = 1;
        heights[50, 50, 0] = 7;
        heights[0, 20, 0] = 3;
        heights[17, 0, 0] = 9;
        world.Tick();

        var expected = new[]
        {
            new FieldChange(height.Id, fields.OrdinalOf(new ChunkCoord(1, 0, 0)), 1),
            new FieldChange(height.Id, fields.OrdinalOf(new ChunkCoord(0, 1, 0)), 1),
            new FieldChange(height.Id, fields.OrdinalOf(new ChunkCoord(3, 3, 0)), 1),
            new FieldChange(soil.Id, 0, 1),
        };
        Assert.Equal(expected, fields.LastTickChanges.ToArray());
        Assert.Equal(expected, Collect(fields.ChangedSince(0)));
        Assert.Equal(new ChunkCoord(1, 0, 0), fields.ChunkAt(fields.LastTickChanges[0].Ordinal));

        // A quiet Tick yields an empty feed; versions from earlier Ticks are still visible through ChangedSince.
        world.Tick();
        Assert.Equal(0, fields.LastTickChanges.Length);
        Assert.Equal(expected, Collect(fields.ChangedSince(0)));
        Assert.Empty(Collect(fields.ChangedSince(1)));

        heights.FillChunk(5, 1);
        world.Tick();
        Assert.Equal([new FieldChange(height.Id, 5, 3)], fields.LastTickChanges.ToArray());
        Assert.Equal([new FieldChange(height.Id, 5, 3)], Collect(fields.ChangedSince(2)));
    }

    [Fact]
    public void Writes_from_parallel_system_jobs_produce_the_same_feed_on_1_and_4_threads()
    {
        FieldChange[]? baseline = null;
        foreach (int workers in new[] { 1, 4 })
        {
            var builder = new WorldBuilder().DebugValidation(false);
            var registration = builder.AddFields(new WorldSize(128, 128, 16));
            var height = registration.AddField<int>("height");
            builder.AddSystem(new SystemDefinition
            {
                Name = "Carve",
                Begin = (in SystemContext context) =>
                {
                    var fields = context.World.Fields();
                    context.World.RunParallel(fields.ChunkCount, new CarveJob(fields, height, context.Tick));
                },
            });
            using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = workers });
            world.Tick();
            world.Tick();
            var changes = world.Fields().LastTickChanges.ToArray();
            Assert.NotEmpty(changes);
            Assert.True(changes.Length < world.Fields().ChunkCount);
            baseline ??= changes;
            Assert.Equal(baseline, changes);
        }
    }

    private sealed class CarveJob : IIndexedJob
    {
        private readonly FieldSet _fields;
        private readonly Field<int> _height;
        private readonly long _tick;

        public CarveJob(FieldSet fields, Field<int> height, long tick)
        {
            _fields = fields;
            _height = height;
            _tick = tick;
        }

        public void Execute(int index)
        {
            // Every third chunk (by Morton ordinal) is written, offset by the Tick.
            if ((index + _tick) % 3 == 0)
            {
                _fields.Accessor(_height).WriteChunk(index)[index % FieldChunk.CellCount] = index;
            }
        }
    }

    private static List<FieldChange> Collect(FieldChangeFeed feed)
    {
        var list = new List<FieldChange>();
        foreach (var change in feed)
        {
            list.Add(change);
        }

        return list;
    }
}
