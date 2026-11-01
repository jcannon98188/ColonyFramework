using Colony.Fields;

namespace Colony.Fields.Tests;

/// <summary>TASK-26: the residency manager (spec section 5.3). Focus points arrive as Input Commands; transitions happen in the Streaming Phase in canonical chunk order.</summary>
public sealed class ResidencyTests
{
    // 6 x 4 x 1 chunks (96 x 64 x 16 tiles).
    private static readonly WorldSize Size = new(96, 64, 16);

    private static (WorldSchema Schema, Field<int> Height, ComponentType TilePos) BuildSchema(Action<WorldBuilder>? extra = null)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        builder.AddComponent<TilePos>("tile.pos");
        var fields = builder.AddFields(Size);
        var height = fields.AddField<int>("height");
        builder.AddResidency(fields, builder.Component<TilePos>());
        extra?.Invoke(builder);
        var schema = builder.Build();
        return (schema, height, schema.Component<TilePos>());
    }

    [Fact]
    public void A_focus_point_makes_chunks_within_its_radius_Active_a_ring_Hibernating_and_the_rest_Unloaded_after_the_next_Streaming_Phase()
    {
        var (schema, _, _) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var residency = world.Residency();

        // Without focus points everything is resident and simulated: the never-unload policy.
        Assert.All(Enumerable.Range(0, fields.ChunkCount), o => Assert.Equal(ChunkResidency.Active, residency.ResidencyOf(o)));

        // Focus on the chunk (1, 1, 0) with radius 1 chunk: Active 0..2 x 0..2, Hibernating ring up to 3, Unloaded beyond.
        world.Inputs.Submit(new InputSource(1), new FocusPoint { Id = 7, Center = new TilePos(20, 20, 3), Radius = 1 });
        world.Tick(); // Input Phase stores the focus; the Streaming Phase of this Tick ran before it.
        Assert.All(Enumerable.Range(0, fields.ChunkCount), o => Assert.Equal(ChunkResidency.Active, residency.ResidencyOf(o)));
        world.Tick(); // The Streaming Phase applies the transitions.

        for (int ordinal = 0; ordinal < fields.ChunkCount; ordinal++)
        {
            var c = fields.ChunkAt(ordinal);
            int distance = Math.Max(Math.Abs(c.X - 1), Math.Abs(c.Y - 1));
            var expected = distance <= 1 ? ChunkResidency.Active : distance == 2 ? ChunkResidency.Hibernating : ChunkResidency.Unloaded;
            Assert.Equal(expected, residency.ResidencyOf(ordinal));
            Assert.Equal(expected, fields.ResidencyOf(ordinal));
        }

        Assert.Equal(9, residency.Counts.Active);
        Assert.Equal(7, residency.Counts.Hibernating);
        Assert.Equal(24 - 16, residency.Counts.Unloaded);
        Assert.True(residency.RingInvariantHolds());
    }

    [Fact]
    public void Forcing_a_chunk_out_and_back_in_gives_the_hash_of_never_unloading_including_its_frozen_Entities()
    {
        var (schema, height, tilePos) = BuildSchema(WithHealthSystem);
        var health = schema.Component<Health>();
        using var reference = schema.CreateWorld(new WorldOptions { WorkerThreads = 1, Seed = 5 });
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1, Seed = 5 });
        foreach (var w in new[] { reference, world })
        {
            Populate(w, tilePos, health);
            w.Fields().Accessor(height)[20, 20, 3] = 99; // chunk (1, 1, 0) goes dense
            w.Tick();
            w.Tick();
        }

        Assert.Equal(reference.Hash(), world.Hash());

        var fields = world.Fields();
        var residency = world.Residency();
        int ordinal = fields.OrdinalOf(new ChunkCoord(1, 1, 0));
        var storage = fields.StorageOf(height);
        long allocatedBefore = storage.CellBytesAllocated;
        Assert.True(allocatedBefore > 0);

        residency.ForceResidency(ordinal, ChunkResidency.Unloaded);
        Assert.Equal(ChunkResidency.Unloaded, residency.ResidencyOf(ordinal));
        Assert.True(residency.HasStoredCells(ordinal)); // no chunk source: exactly its save section
        Assert.Equal(0, storage.CellBytesAllocated);
        var inside = EntitiesIn(world, tilePos, new ChunkCoord(1, 1, 0));
        Assert.NotEmpty(inside);
        Assert.All(inside, id => Assert.True(residency.IsFrozen(id)));
        Assert.All(inside, id => Assert.False(world.IsEnabled(id, health)));
        Assert.All(EntitiesIn(world, tilePos, new ChunkCoord(0, 0, 0)), id => Assert.False(residency.IsFrozen(id)));
        Assert.Throws<InvalidOperationException>(() => fields.Accessor(height)[20, 20, 3] = 1);
        Assert.Equal(FieldsHash(reference), FieldsHash(world)); // the cells are hashed from the stored section
        Assert.NotEqual(reference.Hash(), world.Hash()); // residency and the frozen Entities are state, so the World hash differs while unloaded

        residency.ForceResidency(ordinal, ChunkResidency.Active);
        Assert.Equal(ChunkResidency.Active, residency.ResidencyOf(ordinal));
        Assert.False(residency.HasStoredCells(ordinal));
        Assert.Equal(allocatedBefore, storage.CellBytesAllocated);
        Assert.Equal(99, fields.Accessor(height)[20, 20, 3]);
        Assert.All(inside, id => Assert.False(residency.IsFrozen(id)));
        Assert.All(inside, id => Assert.True(world.IsEnabled(id, health)));
        Assert.Equal(reference.Hash(), world.Hash());

        for (int t = 0; t < 3; t++)
        {
            reference.Tick();
            world.Tick();
            Assert.Equal(reference.Hash(), world.Hash());
        }
    }

    [Fact]
    public void Frozen_Entities_do_not_simulate_and_resume_exactly_when_their_chunk_comes_back()
    {
        var (schema, _, tilePos) = BuildSchema(WithHealthSystem);
        var health = schema.Component<Health>();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        Populate(world, tilePos, health);
        var residency = world.Residency();
        var far = EntitiesIn(world, tilePos, new ChunkCoord(5, 3, 0));
        var near = EntitiesIn(world, tilePos, new ChunkCoord(1, 1, 0));

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(20, 20, 3), 1));
        world.Tick(); // health 1 everywhere; focus stored
        world.Tick(); // Streaming freezes the far chunk, then health 2 near, 1 far
        world.Tick(); // health 3 near, 1 far
        Assert.Equal(ChunkResidency.Unloaded, residency.ResidencyOf(world.Fields().OrdinalOf(new ChunkCoord(5, 3, 0))));
        Assert.All(far, id => Assert.True(residency.IsFrozen(id)));
        Assert.All(far, id => Assert.Equal(1, world.Get<Health>(id, health).Value));
        Assert.All(near, id => Assert.Equal(3, world.Get<Health>(id, health).Value));

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, default, -1)); // remove the focus
        world.Tick(); // health 4 near; focus removed in the Input Phase
        world.Tick(); // Streaming thaws everything; health 5 near, 2 far
        Assert.All(far, id => Assert.False(residency.IsFrozen(id)));
        Assert.All(far, id => Assert.Equal(2, world.Get<Health>(id, health).Value));
        Assert.All(near, id => Assert.Equal(5, world.Get<Health>(id, health).Value));
        Assert.Equal(24, residency.Counts.Active);
    }

    [Fact]
    public void Pinned_Entities_keep_simulating_in_unloaded_space()
    {
        var (schema, _, tilePos) = BuildSchema(WithHealthSystem);
        var health = schema.Component<Health>();
        var pinned = schema.Component<Pinned>();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var commands = world.CreateCommandBuffer();
        var deferredWalker = commands.Create(ComponentMask.Of(tilePos, health));
        commands.Add(deferredWalker, tilePos, new TilePos(90, 60, 1));
        commands.Add(deferredWalker, pinned, new Pinned(1));
        var deferredVillager = commands.Create(ComponentMask.Of(tilePos, health));
        commands.Add(deferredVillager, tilePos, new TilePos(91, 60, 1));
        world.Playback(commands);
        world.ResetScratch();
        var farEntities = EntitiesIn(world, tilePos, new ChunkCoord(5, 3, 0));
        var walker = farEntities.Single(id => world.Has(id, pinned));
        var villager = farEntities.Single(id => !world.Has(id, pinned));

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(0, 0, 0), 0));
        world.Tick();
        world.Tick();
        world.Tick();
        var residency = world.Residency();
        Assert.Equal(ChunkResidency.Unloaded, residency.ResidencyOf(new ChunkCoord(5, 3, 0)));
        Assert.True(world.IsAlive(walker));
        Assert.False(residency.IsFrozen(walker));
        Assert.True(residency.IsFrozen(villager));
        Assert.Equal(3, world.Get<Health>(walker, health).Value);
        Assert.Equal(1, world.Get<Health>(villager, health).Value);
    }

    [Fact]
    public void Stencils_skip_chunks_that_are_not_Active_and_read_Hibernating_neighbours_as_a_fixed_boundary()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        builder.AddComponent<TilePos>("tile.pos");
        var fields = builder.AddFields(new WorldSize(64, 16, 16)); // chunks 0..3 along x
        var value = fields.AddField<int>("value");
        builder.AddResidency(fields, builder.Component<TilePos>());
        builder.AddSystem(Stencil.InPlace("Sweep", value, static (in StencilInput<int> input, Span<int> output) =>
        {
            for (int i = 0; i < FieldChunk.CellCount; i++)
            {
                var (x, y, z) = FieldChunk.CellCoordinates(i);
                output[i] = Math.Max(output[i], input.Read(x - 1, y, z));
            }
        }, boundary: 0));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var accessor = world.Fields().Accessor(value);
        var residency = world.Residency();

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(16, 0, 0), 0));
        world.Tick();
        world.Tick(); // chunk 1 Active, chunks 0 and 2 Hibernating, chunk 3 Unloaded
        Assert.Equal(ChunkResidency.Hibernating, residency.ResidencyOf(new ChunkCoord(0, 0, 0)));
        Assert.Equal(ChunkResidency.Active, residency.ResidencyOf(new ChunkCoord(1, 0, 0)));
        Assert.Equal(ChunkResidency.Unloaded, residency.ResidencyOf(new ChunkCoord(3, 0, 0)));

        accessor[0, 0, 0] = 1; // Hibernating chunk: the sweep must not run here
        world.Tick();
        Assert.Equal(0, accessor[15, 0, 0]);
        Assert.Equal(0, accessor[16, 0, 0]);

        accessor[15, 0, 0] = 1; // the frozen side is a fixed boundary the Active chunk reads
        world.Tick();
        Assert.Equal(1, accessor[16, 0, 0]);
        Assert.Equal(1, accessor[31, 0, 0]);
        Assert.Equal(0, accessor[32, 0, 0]); // Hibernating chunk 2 did not sweep
        Assert.Throws<InvalidOperationException>(() => world.Fields().Accessor(value)[48, 0, 0] = 1); // Unloaded: no resident cells
        Assert.True(residency.RingInvariantHolds());
    }

    [Fact]
    public void Saves_carry_residency_and_stored_sections_only_when_some_chunk_is_not_Active()
    {
        var (schema, height, tilePos) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        fields.Accessor(height)[85, 55, 2] = 7; // chunk (5, 3, 0)
        var plain = new MemoryWriter();
        ((IWorldExtension)fields).Save(plain);
        Assert.DoesNotContain("residency", plain.Sections.Keys);
        Assert.Equal(fields.ChunkCount, plain.Sections.Count);

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, new TilePos(0, 0, 0), 0));
        world.Tick();
        world.Tick();
        int far = fields.OrdinalOf(new ChunkCoord(5, 3, 0));
        Assert.Equal(ChunkResidency.Unloaded, fields.ResidencyOf(far));
        Assert.True(fields.HasStoredCells(far));

        var streaming = new MemoryWriter();
        ((IWorldExtension)fields).Save(streaming);
        var residencyState = new MemoryWriter();
        ((IWorldExtension)world.Residency()).Save(residencyState);
        Assert.Contains("residency", streaming.Sections.Keys);
        Assert.Equal(fields.ChunkCount + 1, streaming.Sections.Count); // stored unloaded chunks keep their section
        Assert.Equal(plain.Sections["height/" + far], streaming.Sections["height/" + far]);

        using var loaded = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        ((IWorldExtension)loaded.Fields()).Load(new MemoryReader("fields", streaming.Sections));
        ((IWorldExtension)loaded.Residency()).Load(new MemoryReader("residency", residencyState.Sections));
        Assert.Equal(ChunkResidency.Unloaded, loaded.Fields().ResidencyOf(far));
        Assert.True(loaded.Fields().HasStoredCells(far));
        Assert.Equal(ChunkResidency.Active, loaded.Fields().ResidencyOf(0));
        Assert.Equal(1, loaded.Residency().FocusPoints.Length);
        Assert.Equal(FieldsHash(world), FieldsHash(loaded));

        loaded.Residency().ForceResidency(far, ChunkResidency.Active);
        Assert.Equal(7, loaded.Fields().Accessor(height)[85, 55, 2]);
        _ = tilePos;
    }

    private static void WithHealthSystem(WorldBuilder builder)
    {
        var health = builder.AddComponent<Health>("health").Component<Health>();
        builder.AddSystem(new SystemDefinition
        {
            Name = "heal",
            Phase = Phase.Simulate,
            Writes = ComponentMask.Of(health),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var column = chunk.Column<Health>(health);
                foreach (int row in chunk.EnabledRows(health))
                {
                    column[row].Value++;
                }
            },
        });
    }

    /// <summary>One Entity per chunk at the chunk's (4, 4, 3) tile, plus a second one in chunk (1, 1, 0).</summary>
    private static void Populate(World world, ComponentType tilePos, ComponentType health)
    {
        var commands = world.CreateCommandBuffer();
        var fields = world.Fields();
        for (int ordinal = 0; ordinal < fields.ChunkCount; ordinal++)
        {
            var origin = fields.ChunkAt(ordinal).Origin;
            var e = commands.Create(ComponentMask.Of(tilePos, health));
            commands.Add(e, tilePos, origin + new TilePos(4, 4, 3));
        }

        var extra = commands.Create(ComponentMask.Of(tilePos, health));
        commands.Add(extra, tilePos, new TilePos(30, 17, 0));
        world.Playback(commands);
        world.ResetScratch();
    }

    private static ulong FieldsHash(World world)
    {
        var state = new Hashing.Hash64State(0);
        ((IWorldExtension)world.Fields()).Hash(ref state);
        return state.Finish();
    }

    private static List<EntityId> EntitiesIn(World world, ComponentType tilePos, ChunkCoord coord)
    {
        var result = new List<EntityId>();
        foreach (var chunk in world.Query(ComponentMask.Of(tilePos), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(tilePos);
            var ids = chunk.Entities;
            for (int row = 0; row < positions.Length; row++)
            {
                if (positions[row].Chunk == coord)
                {
                    result.Add(ids[row]);
                }
            }
        }

        return result;
    }

    private record struct Health(int Value);

    private sealed class MemoryWriter : ExtensionStateWriter
    {
        public Dictionary<string, byte[]> Sections { get; } = new(StringComparer.Ordinal);

        public override void WriteSection(string name, ReadOnlySpan<byte> payload) => Sections.Add(name, payload.ToArray());
    }

    private sealed class MemoryReader(string extensionName, Dictionary<string, byte[]> sections) : ExtensionStateReader
    {
        public override string ExtensionName => extensionName;

        public override int SectionCount => sections.Count;

        public override string SectionName(int index) => sections.Keys.ElementAt(index);

        public override bool TryGetSection(string name, out ReadOnlySpan<byte> payload)
        {
            if (sections.TryGetValue(name, out var bytes))
            {
                payload = bytes;
                return true;
            }

            payload = default;
            return false;
        }
    }
}
