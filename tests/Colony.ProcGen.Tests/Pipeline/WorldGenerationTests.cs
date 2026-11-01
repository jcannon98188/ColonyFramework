using Colony.Fields;
using Colony.Persistence;

namespace Colony.ProcGen.Tests.Pipeline;

/// <summary>AC #4 (pre-pass Resource and placement ids) and AC #6 (generation version in the save) through the World.</summary>
public sealed class WorldGenerationTests
{
    private const ulong Seed = 0x5EED_0001UL;

    [Fact]
    public void A_pre_pass_is_computed_once_per_provider_and_is_the_same_for_the_same_seed()
    {
        var counter = new CountingPrePass();
        var pipeline = TerrainPipeline.Compose(c => c.Generation.AddPrePass(counter)).Generation.Pipeline;

        var first = pipeline.CreateProvider(Seed);
        foreach (var coord in TerrainPipeline.AllChunks())
        {
            first.Generate(coord);
        }

        Assert.Equal(1, counter.Calls);
        var second = pipeline.CreateProvider(Seed);
        Assert.Equal(2, counter.Calls);
        Assert.Equal(first.PrePass<TerrainPipeline.Climate>(), second.PrePass<TerrainPipeline.Climate>());
        Assert.Equal(first.PrePass<Tally>(), second.PrePass<Tally>());
        Assert.NotEqual(first.PrePass<TerrainPipeline.Climate>(), pipeline.CreateProvider(Seed + 1).PrePass<TerrainPipeline.Climate>());
    }

    [Fact]
    public void The_pre_pass_value_is_cached_in_its_generated_Resource()
    {
        var composition = TerrainPipeline.Compose();
        using var world = composition.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed });
        var generation = world.Generation();

        var resource = world.Schema.Resource(GenerationBuilder.PrePassResourcePrefix + "climate");
        Assert.Equal(generation.Provider.PrePass<TerrainPipeline.Climate>(), world.Resource<TerrainPipeline.Climate>(resource));
        Assert.NotEqual(default, world.Resource<TerrainPipeline.Climate>(resource));
    }

    [Fact]
    public void Placement_ids_derive_from_seed_stage_coordinate_and_index()
    {
        var pipeline = TerrainPipeline.Compose().Generation.Pipeline;
        var provider = pipeline.CreateProvider(Seed);
        int total = 0;
        foreach (var coord in TerrainPipeline.AllChunks())
        {
            var chunk = provider.Generate(coord);
            foreach (var placement in chunk.Placements)
            {
                Assert.Equal(Placement.DeriveId(Seed, pipeline.Stages[placement.Stage].Name, coord, placement.Index), placement.Id);
                total++;
            }
        }

        Assert.True(total > 0, "the tree stage placed nothing");
    }

    [Fact]
    public void Generating_the_World_writes_the_Fields_and_creates_one_Entity_per_placement()
    {
        var composition = TerrainPipeline.Compose();
        using var world = composition.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed });
        var generation = world.Generation();

        generation.GenerateAll();

        var expected = new List<GeneratedEntity>();
        var height = world.Fields().Accessor(composition.Height);
        foreach (var coord in TerrainPipeline.AllChunks())
        {
            var chunk = generation.Provider.Generate(coord);
            var cells = chunk.Cells(composition.Height);
            for (int y = 0; y < FieldChunk.Size; y++)
            {
                for (int x = 0; x < FieldChunk.Size; x++)
                {
                    Assert.Equal(cells[FieldChunk.CellIndex(x, y, 0)], height[coord.Origin.X + x, coord.Origin.Y + y, 0]);
                }
            }

            foreach (var p in chunk.Placements)
            {
                expected.Add(new GeneratedEntity(p.Id, p.Kind, p.Data));
            }
        }

        var actual = new List<GeneratedEntity>();
        var type = composition.Generation.GeneratedEntity;
        foreach (var view in world.Query(type))
        {
            actual.AddRange(view.Column<GeneratedEntity>(type).ToArray());
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(e => e.Id), actual.OrderBy(e => e.Id));
    }

    [Fact]
    public void The_save_manifest_records_the_generation_version()
    {
        var composition = TerrainPipeline.Compose(c => c.Generation.GenerationVersion = 3);
        using var world = composition.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed });

        var manifest = WorldPersistence.ReadManifest(world.SaveToBytes());

        var extension = Assert.Single(manifest.Extensions, e => e.Name == GenerationBuilder.ExtensionName);
        Assert.Equal("3", extension.Version);
    }

    [Fact]
    public void Regeneration_is_refused_when_the_saved_generation_version_differs()
    {
        var saved = TerrainPipeline.Compose();
        using var world = saved.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed });
        byte[] bytes = world.SaveToBytes();

        var newer = TerrainPipeline.Compose(c => c.Generation.GenerationVersion = 2);
        using var loaded = WorldPersistence.Load(bytes, newer.Builder.Build());
        var generation = loaded.Generation();

        Assert.Equal(1, generation.SavedVersion);
        Assert.False(generation.CanRegenerate);
        var error = Assert.Throws<GenerationVersionMismatchException>(() => generation.Generate(new ChunkCoord(0, 0, 0)));
        Assert.Equal(1, error.SavedVersion);
        Assert.Equal(2, error.CurrentVersion);
        var chunk = generation.Provider.Generate(new ChunkCoord(0, 0, 0));
        Assert.Throws<GenerationVersionMismatchException>(() => generation.Apply(chunk));
    }

    [Fact]
    public void Regeneration_is_allowed_when_the_saved_generation_version_matches()
    {
        var saved = TerrainPipeline.Compose();
        using var world = saved.Builder.Build().CreateWorld(new WorldOptions { Seed = Seed });
        byte[] bytes = world.SaveToBytes();

        using var loaded = WorldPersistence.Load(bytes, TerrainPipeline.Compose().Builder.Build());
        var generation = loaded.Generation();

        Assert.True(generation.CanRegenerate);
        Assert.Equal(world.Generation().Pipeline.Fingerprint, generation.SavedFingerprint);
        Assert.Equal(world.Generation().Provider.Generate(new ChunkCoord(1, 1, 0)).Hash(), generation.Generate(new ChunkCoord(1, 1, 0)).Hash());
    }

    public struct Tally
    {
        public long Value;
    }

    private sealed class CountingPrePass : GenerationPrePass<Tally>
    {
        private int _calls;

        public int Calls => _calls;

        public override string Name => "tally";

        public override Tally Compute(in PrePassContext context)
        {
            Interlocked.Increment(ref _calls);
            return new Tally { Value = (long)context.Random.Int32At(0, 0, 1000) + context.PrePass<TerrainPipeline.Climate>().SeaLevel };
        }
    }
}
