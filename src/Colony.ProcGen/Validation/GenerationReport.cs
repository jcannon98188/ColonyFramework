using System.Globalization;
using System.Text;
using Colony.Fields;

namespace Colony.ProcGen.Validation;

/// <summary>What a <see cref="GenerationIssue"/> is about.</summary>
public enum GenerationIssueKind
{
    /// <summary>A stage's hash over the validated chunks differs from (or is missing in) the golden hashes.</summary>
    GoldenDrift,

    /// <summary>A stage's output for a chunk changed when chunks were generated in another order or on other threads.</summary>
    OrderDependence,

    /// <summary>A stage's output across a chunk face differs from a window generated straddling that face.</summary>
    SeamMismatch,

    /// <summary>A Game-supplied <see cref="GenerationCheck"/> failed.</summary>
    Check,
}

/// <summary>One finding of <see cref="GenerationValidator"/>, attributed to the stage (or check) that caused it.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Stage">The stage name, or the check name for <see cref="GenerationIssueKind.Check"/>.</param>
/// <param name="Seed">The seed it was found for.</param>
/// <param name="Chunk">The first chunk it was found at, when it is about chunks.</param>
/// <param name="Message">A readable description.</param>
public sealed record GenerationIssue(GenerationIssueKind Kind, string Stage, ulong Seed, ChunkCoord? Chunk, string Message);

/// <summary>Timing of one stage over every chunk the validator generated in its baseline pass.</summary>
/// <param name="Name">Stage name.</param>
/// <param name="Chunks">Chunks generated (seeds x chunks).</param>
/// <param name="Total">Summed stage time.</param>
public sealed record StageReport(string Name, int Chunks, TimeSpan Total)
{
    public TimeSpan MeanPerChunk => Chunks == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Total.Ticks / Chunks);
}

/// <summary>The result of <see cref="GenerationValidator.Validate"/>.</summary>
public sealed class GenerationReport
{
    internal GenerationReport(GenerationPipeline pipeline, IReadOnlyList<ulong> seeds, int chunks, IReadOnlyList<StageReport> stages, IReadOnlyList<GenerationIssue> issues, GoldenHashes hashes, int seamsChecked)
    {
        Pipeline = pipeline;
        Seeds = seeds;
        ChunkCount = chunks;
        Stages = stages;
        Issues = issues;
        Hashes = hashes;
        SeamsChecked = seamsChecked;
    }

    public GenerationPipeline Pipeline { get; }

    public IReadOnlyList<ulong> Seeds { get; }

    /// <summary>Chunks validated per seed.</summary>
    public int ChunkCount { get; }

    /// <summary>Per-stage timing, in execution order.</summary>
    public IReadOnlyList<StageReport> Stages { get; }

    public IReadOnlyList<GenerationIssue> Issues { get; }

    /// <summary>The per-(seed, stage) hashes of this run; save them as the golden file with <see cref="GoldenHashes.ToText"/>.</summary>
    public GoldenHashes Hashes { get; }

    /// <summary>Chunk faces the seam check compared (over all seeds).</summary>
    public int SeamsChecked { get; }

    public bool Passed => Issues.Count == 0;

    /// <summary>A readable report: verdict, per-stage timing, then every issue.</summary>
    public string Describe()
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(culture, $"Generation validation {(Passed ? "PASSED" : "FAILED")}: {Issues.Count} issue(s); version {Pipeline.Version}, fingerprint {Pipeline.Fingerprint:x16}; {Seeds.Count} seed(s) x {ChunkCount} chunk(s); {SeamsChecked} seam(s) checked.").AppendLine();
        text.AppendLine("stage                            total ms   mean us/chunk");
        foreach (var stage in Stages)
        {
            text.Append(culture, $"  {stage.Name,-30} {stage.Total.TotalMilliseconds,9:F2}   {stage.MeanPerChunk.TotalMicroseconds,13:F1}").AppendLine();
        }

        foreach (var issue in Issues)
        {
            text.Append(culture, $"[{issue.Kind}] '{issue.Stage}' seed 0x{issue.Seed:x}");
            if (issue.Chunk is { } chunk)
            {
                text.Append(culture, $" chunk ({chunk.X}, {chunk.Y}, {chunk.Z})");
            }

            text.Append(": ").AppendLine(issue.Message);
        }

        return text.ToString();
    }
}
