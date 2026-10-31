using System.Globalization;
using System.Text;

namespace Colony.Testing;

public enum GoldenStatus
{
    /// <summary>Every checkpoint hash equals the committed one.</summary>
    Match,

    /// <summary>A checkpoint differs (or the checkpoint set changed); a <c>.received.golden</c> file was written beside the golden file.</summary>
    Mismatch,

    /// <summary>No golden file is committed for the scenario yet.</summary>
    Missing,

    /// <summary>The result was written as the new golden file (accept mode).</summary>
    Accepted,

    /// <summary>The determinism result itself failed, so there was nothing trustworthy to compare.</summary>
    NotRun,
}

/// <summary>The outcome of comparing a <see cref="DeterminismResult"/> with the committed golden hashes.</summary>
public readonly record struct GoldenComparison(GoldenStatus Status, long? FirstDivergingTick, string Path, string Message)
{
    public bool Passed => Status is GoldenStatus.Match or GoldenStatus.Accepted;

    public void ThrowIfFailed()
    {
        if (!Passed)
        {
            throw new HarnessException(Message);
        }
    }
}

/// <summary>
/// Committed golden hashes, one text file per scenario (<c>&lt;name&gt;.golden</c>: a header with the
/// scenario name and seed, then <c>&lt;tick&gt; &lt;hash&gt;</c> per checkpoint). Compared on every run
/// and on every CI platform, so a hash change is a deliberate, reviewed commit (spec section 10.3).
/// Accept new output like the snapshot convention: <c>COLONY_ACCEPT_GOLDEN=1</c> or <c>accept: true</c>.
/// </summary>
public sealed class GoldenHashStore
{
    public const string AcceptVariable = "COLONY_ACCEPT_GOLDEN";

    public GoldenHashStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary>True when <c>COLONY_ACCEPT_GOLDEN=1</c> is set in the environment.</summary>
    public static bool AcceptFromEnvironment => string.Equals(Environment.GetEnvironmentVariable(AcceptVariable), "1", StringComparison.Ordinal);

    public string PathFor(string scenarioName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
        return System.IO.Path.Combine(Directory, scenarioName + ".golden");
    }

    /// <summary>Compares the baseline hashes of a passed result with the committed file; <paramref name="accept"/> defaults to <see cref="AcceptFromEnvironment"/>.</summary>
    public GoldenComparison Compare(DeterminismResult result, bool? accept = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        string path = PathFor(result.ScenarioName);
        if (!result.Passed)
        {
            return new GoldenComparison(GoldenStatus.NotRun, null, path, "The determinism run failed, so its hashes were not compared with the golden file:\n" + result.Describe());
        }

        string content = Format(result);
        if (accept ?? AcceptFromEnvironment)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, content);
            return new GoldenComparison(GoldenStatus.Accepted, null, path, $"Accepted {result.Checkpoints.Length} golden hashes for scenario '{result.ScenarioName}' into {path}.");
        }

        if (!File.Exists(path))
        {
            return new GoldenComparison(GoldenStatus.Missing, null, path, $"No golden hashes for scenario '{result.ScenarioName}' at {path}. Run once with {AcceptVariable}=1 (or accept: true) to create it, then commit the file.");
        }

        var expected = Parse(File.ReadAllText(path), path);
        var checkpoints = result.Checkpoints;
        var hashes = result.Hashes.Span;
        long? diverging = null;
        for (int i = 0; i < checkpoints.Length; i++)
        {
            if (i >= expected.Count || expected[i].Tick != checkpoints[i] || expected[i].Hash != hashes[i])
            {
                diverging = checkpoints[i];
                break;
            }
        }

        if (diverging is null && expected.Count != checkpoints.Length)
        {
            diverging = expected[checkpoints.Length].Tick;
        }

        if (diverging is null)
        {
            return new GoldenComparison(GoldenStatus.Match, null, path, $"Scenario '{result.ScenarioName}' matches its {checkpoints.Length} golden hashes.");
        }

        string received = System.IO.Path.ChangeExtension(path, ".received.golden");
        File.WriteAllText(received, content);
        var message = new StringBuilder();
        message.AppendLine(CultureInfo.InvariantCulture, $"Scenario '{result.ScenarioName}' differs from its golden hashes at Tick {diverging} ({path}).");
        int index = checkpoints.IndexOf((int)diverging.Value);
        string expectedText = index >= 0 && index < expected.Count && expected[index].Tick == checkpoints[index]
            ? expected[index].Hash.ToString("x16", CultureInfo.InvariantCulture)
            : "(no such checkpoint)";
        string actualText = index >= 0 ? hashes[index].ToString("x16", CultureInfo.InvariantCulture) : "(no such checkpoint)";
        message.AppendLine(CultureInfo.InvariantCulture, $"  expected {expectedText}, got {actualText}");
        message.AppendLine(CultureInfo.InvariantCulture, $"  Received hashes written to {received}. If the change is intended, set {AcceptVariable}=1 and rerun, then commit the golden file.");
        return new GoldenComparison(GoldenStatus.Mismatch, diverging, path, message.ToString());
    }

    internal static string Format(DeterminismResult result)
    {
        var text = new StringBuilder();
        text.Append("# Colony golden World hashes (XXH64, hex); regenerate with ").Append(AcceptVariable).Append("=1\n");
        text.Append(CultureInfo.InvariantCulture, $"scenario {result.ScenarioName}\n");
        text.Append(CultureInfo.InvariantCulture, $"seed {result.Seed}\n");
        var hashes = result.Hashes.Span;
        for (int i = 0; i < result.Checkpoints.Length; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{result.Checkpoints[i]} {hashes[i]:x16}\n");
        }

        return text.ToString();
    }

    internal static List<(int Tick, ulong Hash)> Parse(string content, string path)
    {
        var entries = new List<(int, ulong)>();
        foreach (var rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || !char.IsAsciiDigit(line[0]))
            {
                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0
                || !int.TryParse(line.AsSpan(0, space), NumberStyles.None, CultureInfo.InvariantCulture, out int tick)
                || !ulong.TryParse(line.AsSpan(space + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
            {
                throw new FormatException($"Malformed golden hash line '{line}' in {path}.");
            }

            entries.Add((tick, hash));
        }

        return entries;
    }
}
