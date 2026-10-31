using Colony.Fields;

namespace Colony.ProcGen.Tests.Pipeline;

/// <summary>AC #2 and the pipeline build rules: ordering by name, declared Fields, Overridable replacement.</summary>
public sealed class PipelineTests
{
    private const ulong Seed = 0xC0FFEEUL;

    private static readonly ChunkProviderOptions Hashes = new() { RecordStageHashes = true };

    [Fact]
    public void Adding_a_stage_does_not_change_the_random_numbers_other_stages_draw()
    {
        var baseline = TerrainPipeline.Compose().Generation.Pipeline;
        var extended = TerrainPipeline.Compose(c => c.Generation.AddStage(new TerrainPipeline.ExtraStage(c.Extra))).Generation.Pipeline;
        Assert.Equal(baseline.Stages.Count + 1, extended.Stages.Count);

        var a = baseline.CreateProvider(Seed, Hashes);
        var b = extended.CreateProvider(Seed, Hashes);
        foreach (var coord in TerrainPipeline.AllChunks())
        {
            var left = a.Generate(coord);
            var right = b.Generate(coord);
            foreach (var stage in baseline.Stages)
            {
                Assert.Equal(left.StageHashes[stage.Order], right.StageHashes[extended.IndexOf(stage.Name)]);
            }

            Assert.Equal(left.Placements.ToArray(), right.Placements.ToArray().Select(p => p with { Stage = baseline.IndexOf(extended.Stages[p.Stage].Name) }).ToArray());
        }
    }

    [Fact]
    public void Stages_run_in_After_Before_order_with_registration_order_breaking_ties()
    {
        var pipeline = TerrainPipeline.Compose(c => c.Generation.AddStage(new TerrainPipeline.ExtraStage(c.Extra))).Generation.Pipeline;

        // Registered: height, lakes, smooth, biome, trees, extra; extra.noise is Before feature.lakes.
        Assert.Equal(
            ["terrain.height", "terrain.smooth", "terrain.biome", "extra.noise", "feature.lakes", "place.trees"],
            pipeline.Stages.Select(s => s.Name).ToArray());
        Assert.True(pipeline.Stages[0].IsBase);
        Assert.False(pipeline.Stages[2].IsBase);
    }

    [Fact]
    public void A_stage_writing_a_Field_another_reads_must_be_ordered()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TerrainPipeline.Compose(c => c.Generation.AddStage(new Unordered("rogue", [TerrainPipeline.WaterField], []))).Generation.Pipeline);

        Assert.Contains("rogue", error.Message, StringComparison.Ordinal);
        Assert.Contains(TerrainPipeline.WaterField, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_a_Field_no_earlier_stage_writes_fails_the_build()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TerrainPipeline.Compose(c => c.Generation.AddStage(new Unordered("early", [], [TerrainPipeline.ExtraField])), standardStages: false).Generation.Pipeline);

        Assert.Contains("no earlier stage writes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_stage_names_and_cycles_fail_the_build()
    {
        Assert.Throws<InvalidOperationException>(() => TerrainPipeline.Compose(c => c.Generation.AddStage(new Unordered("lost", [], [], after: ["nobody"]))).Generation.Pipeline);
        var cycle = Assert.Throws<InvalidOperationException>(() => TerrainPipeline.Compose(c =>
        {
            c.Generation.AddStage(new Unordered("a", [], [], after: ["b"]));
            c.Generation.AddStage(new Unordered("b", [], [], after: ["a"]));
        }).Generation.Pipeline);
        Assert.Contains("cycle", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undeclared_Field_access_throws_inside_the_stage()
    {
        var composition = TerrainPipeline.Compose(standardStages: false);
        composition.Generation.AddStage(new TerrainPipeline.HeightStage(composition.Height));
        composition.Generation.AddStage(new Sneaky(composition.Height));
        var provider = composition.Generation.Pipeline.CreateProvider(Seed);

        var error = Assert.Throws<InvalidOperationException>(() => provider.Generate(new ChunkCoord(0, 0, 0)));
        Assert.Contains("Writes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Overridable_stage_can_be_replaced_and_keeps_its_name_and_slot()
    {
        var composition = TerrainPipeline.Compose(standardStages: false);
        var generation = composition.Generation;
        composition.Builder.BeginPlugin("base", "1.0", deterministic: true);
        generation.AddStage(new TerrainPipeline.HeightStage(composition.Height), overridable: true);
        generation.AddStage(new TerrainPipeline.LakeStage(composition.Water));
        composition.Builder.EndPlugin();
        composition.Builder.BeginPlugin("mod", "1.0", deterministic: true);
        composition.Builder.Generation().ReplaceStage(new FlatHeight(composition.Height));
        var refused = Assert.Throws<InvalidOperationException>(() => generation.ReplaceStage(new TerrainPipeline.ChunkKeyedLakeStage(composition.Water)));
        composition.Builder.EndPlugin();

        var pipeline = generation.Pipeline;
        var chunk = pipeline.CreateProvider(Seed).Generate(new ChunkCoord(0, 0, 0));
        Assert.All(chunk.Cells(composition.Height).ToArray(), h => Assert.Equal(42, h));
        Assert.Equal("terrain.height", pipeline.Stages[0].Name);
        Assert.IsType<FlatHeight>(pipeline.Stages[0].Stage);
        Assert.Equal("base", pipeline.Stages[0].Owner);
        Assert.Equal([new StageOverride("terrain.height", "base", "mod")], generation.Overrides.ToArray());
        Assert.Contains("not Overridable", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => generation.AddStage(new TerrainPipeline.ExtraStage(composition.Extra)));
    }

    private sealed class Unordered(string name, string[] writes, string[] reads, string[]? after = null) : GenerationStage
    {
        public override string Name => name;

        public override IReadOnlyList<string> Writes => writes;

        public override IReadOnlyList<string> Reads => reads;

        public override IReadOnlyList<string> After => after ?? [];

        public override void Generate(ref ChunkGenerationContext context)
        {
        }
    }

    private sealed class Sneaky(Field<int> height) : GenerationStage
    {
        public override string Name => "sneaky";

        public override IReadOnlyList<string> Reads => [TerrainPipeline.HeightField];

        public override IReadOnlyList<string> After => ["terrain.height"];

        public override void Generate(ref ChunkGenerationContext context) => context.Write(height)[0] = 1;
    }

    private sealed class FlatHeight(Field<int> height) : GenerationStage
    {
        public override string Name => "terrain.height";

        public override IReadOnlyList<string> Writes => [TerrainPipeline.HeightField];

        public override void Generate(ref ChunkGenerationContext context) => context.Write(height).Fill(42);
    }
}
