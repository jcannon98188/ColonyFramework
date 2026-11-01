using Colony.Fields;
using Colony.ProcGen.Tests.Pipeline;
using Colony.ProcGen.Validation;

namespace Colony.ProcGen.Tests.Validation;

/// <summary>AC #3 and #5: the validation tooling reports golden drift, order dependence and seam mismatches per stage, with timing.</summary>
public sealed class ValidatorTests
{
    private static readonly ValidationOptions TwoSeeds = new() { Seeds = [1, 2] };

    [Fact]
    public void The_standard_pipeline_passes_order_and_seam_checks_and_times_every_stage()
    {
        var pipeline = TerrainPipeline.Compose().Generation.Pipeline;

        var report = GenerationValidator.Validate(pipeline, TwoSeeds);

        Assert.True(report.Passed, report.Describe());
        Assert.Equal(pipeline.Stages.Select(s => s.Name), report.Stages.Select(s => s.Name));
        Assert.All(report.Stages, s => Assert.Equal(2 * TerrainPipeline.AllChunks().Length, s.Chunks));
        Assert.All(report.Stages, s => Assert.True(s.Total > TimeSpan.Zero, s.Name));
        Assert.True(report.SeamsChecked > 0);
        Assert.Contains("terrain.smooth", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_keyed_features_crossing_the_chunk_edge_fail_the_seam_check_at_their_stage()
    {
        var pipeline = TerrainPipeline.Compose(standardStages: false, stages: c =>
        {
            c.Generation.AddStage(new TerrainPipeline.HeightStage(c.Height));
            c.Generation.AddStage(new TerrainPipeline.ChunkKeyedLakeStage(c.Water));
            c.Generation.AddStage(new TerrainPipeline.SmoothStage(c.Height, c.Smooth));
        }).Generation.Pipeline;

        var report = GenerationValidator.Validate(pipeline, TwoSeeds);

        Assert.False(report.Passed);
        var seams = report.Issues.Where(i => i.Kind == GenerationIssueKind.SeamMismatch).ToArray();
        Assert.NotEmpty(seams);
        Assert.All(seams, i => Assert.Equal("feature.lakes", i.Stage));
        Assert.DoesNotContain(report.Issues, i => i.Kind != GenerationIssueKind.SeamMismatch);
        Assert.Contains("feature.lakes", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_stage_whose_output_depends_on_generation_order_is_reported()
    {
        var pipeline = TerrainPipeline.Compose(c => c.Generation.AddStage(new OrderDependentStage(c.Extra))).Generation.Pipeline;

        var report = GenerationValidator.Validate(pipeline, new ValidationOptions { Seeds = [7], CheckSeams = false });

        var issue = Assert.Single(report.Issues);
        Assert.Equal(GenerationIssueKind.OrderDependence, issue.Kind);
        Assert.Equal("leaky.counter", issue.Stage);
        Assert.Equal(7UL, issue.Seed);
    }

    [Fact]
    public void Golden_drift_is_reported_for_exactly_the_stages_whose_output_changed()
    {
        var golden = GenerationValidator.Validate(TerrainPipeline.Compose().Generation.Pipeline, TwoSeeds).Hashes;
        var reparsed = GoldenHashes.Parse(golden.ToText());
        Assert.Equal(golden.ToText(), reparsed.ToText());

        var changed = TerrainPipeline.Compose(c => c.Generation.AddStage(new TerrainPipeline.ExtraStage(c.Extra))).Generation.Pipeline;
        var unchanged = GenerationValidator.Validate(changed, TwoSeeds with { Golden = reparsed, CheckOrder = false, CheckSeams = false });
        var missing = Assert.Single(unchanged.Issues.Select(i => (i.Kind, i.Stage)).Distinct());
        Assert.Equal((GenerationIssueKind.GoldenDrift, "extra.noise"), missing);

        var composition = TerrainPipeline.Compose(standardStages: false);
        var generation = composition.Generation;
        generation.AddPrePass(new TerrainPipeline.ClimatePrePass());
        generation.AddStage(new TerrainPipeline.HeightStage(composition.Height));
        generation.AddStage(new ShiftedLakes(composition.Water));
        generation.AddStage(new TerrainPipeline.SmoothStage(composition.Height, composition.Smooth));
        generation.AddStage(new TerrainPipeline.BiomeStage(composition.Height, composition.Biome));
        generation.AddStage(new TerrainPipeline.TreeStage(composition.Water));
        var drift = GenerationValidator.Validate(generation.Pipeline, TwoSeeds with { Golden = golden, CheckOrder = false, CheckSeams = false });

        Assert.Equal(["feature.lakes", "place.trees"], drift.Issues.Where(i => i.Kind == GenerationIssueKind.GoldenDrift).Select(i => i.Stage).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, drift.Issues.Count(i => i.Stage == "feature.lakes"));
    }

    [Fact]
    public void The_terrain_pipeline_matches_its_checked_in_golden_hashes()
    {
        var pipeline = TerrainPipeline.Compose().Generation.Pipeline;
        string path = Path.Combine(GoldenDirectory(), "terrain.golden");
        var actual = GenerationValidator.Validate(pipeline, new ValidationOptions { Seeds = [1, 2, 3], CheckOrder = false, CheckSeams = false }).Hashes;
        if (Environment.GetEnvironmentVariable("COLONY_ACCEPT_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual.ToText());
        }

        Assert.True(File.Exists(path), $"Golden file {path} is missing; run with COLONY_ACCEPT_GOLDEN=1 to create it.");

        var report = GenerationValidator.Validate(pipeline, new ValidationOptions { Seeds = [1, 2, 3], Golden = GoldenHashes.Parse(File.ReadAllText(path)), CheckOrder = false, CheckSeams = false });

        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void A_connectivity_check_reports_passable_tiles_split_into_regions()
    {
        var connected = Validate(wallAt: -1);
        var split = Validate(wallAt: 20);

        Assert.True(connected.Passed, connected.Describe());
        var issue = Assert.Single(split.Issues);
        Assert.Equal(GenerationIssueKind.Check, issue.Kind);
        Assert.Equal("walkable", issue.Stage);
        Assert.Contains("2 regions", issue.Message, StringComparison.Ordinal);

        static GenerationReport Validate(int wallAt)
        {
            var composition = TerrainPipeline.Compose(standardStages: false);
            composition.Generation.AddStage(new Wall(composition.Extra, wallAt));
            return GenerationValidator.Validate(composition.Generation.Pipeline, new ValidationOptions
            {
                Seeds = [1],
                Checks = [new ConnectivityCheck<int>("walkable", composition.Extra, v => v == 0)],
            });
        }
    }

    private static string GoldenDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Colony.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string golden = Path.Combine(directory.FullName, "tests", "Colony.ProcGen.Tests", "Golden");
        Directory.CreateDirectory(golden);
        return golden;
    }

    /// <summary>Breaks the ChunkProvider contract: shared mutable state makes the output depend on what was generated before.</summary>
    private sealed class OrderDependentStage(Field<int> extra) : GenerationStage
    {
        private int _generated;

        public override string Name => "leaky.counter";

        public override IReadOnlyList<string> Writes => [TerrainPipeline.ExtraField];

        public override bool ChunkLocal => true;

        public override void Generate(ref ChunkGenerationContext context) => context.Write(extra).Fill(Interlocked.Increment(ref _generated));
    }

    /// <summary>Region-keyed lakes like the standard stage, drawn one tile further east: changes the lakes and the trees on them.</summary>
    private sealed class ShiftedLakes(Field<byte> water) : GenerationStage
    {
        public override string Name => "feature.lakes";

        public override IReadOnlyList<string> Writes => [TerrainPipeline.WaterField];

        public override IReadOnlyList<string> After => ["terrain.height"];

        public override void Generate(ref ChunkGenerationContext context)
        {
            new TerrainPipeline.LakeStage(water).Generate(ref context);
            var cells = context.Write(water);
            Span<byte> copy = stackalloc byte[cells.Length];
            cells.CopyTo(copy);
            for (int i = 0; i < cells.Length; i++)
            {
                var (x, y, z) = FieldChunk.CellCoordinates(i);
                cells[i] = x == 0 ? (byte)0 : copy[FieldChunk.CellIndex(x - 1, y, z)];
            }
        }
    }

    /// <summary>Writes 1 (impassable) on the column x == wallAt across the whole World.</summary>
    private sealed class Wall(Field<int> extra, int wallAt) : GenerationStage
    {
        public override string Name => "wall";

        public override IReadOnlyList<string> Writes => [TerrainPipeline.ExtraField];

        public override void Generate(ref ChunkGenerationContext context)
        {
            var cells = context.Write(extra);
            for (int i = 0; i < cells.Length; i++)
            {
                var (x, _, _) = FieldChunk.CellCoordinates(i);
                cells[i] = context.Origin.X + x == wallAt ? 1 : 0;
            }
        }
    }
}
