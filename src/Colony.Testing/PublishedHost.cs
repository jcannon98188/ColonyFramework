using System.Diagnostics;

namespace Colony.Testing;

/// <summary>
/// Finds and runs a published <see cref="HarnessHost"/> executable (typically a Native AOT build) and
/// compares its hashes with an in-process run of the same scenario. The Framework's own host is
/// <c>tests/Colony.Testing.AotHost</c>, published with
/// <c>dotnet publish tests/Colony.Testing.AotHost -c Release -r win-x64</c> (or <c>linux-x64</c>) into
/// <c>artifacts/publish/Colony.Testing.AotHost/</c>.
/// </summary>
public static class PublishedHost
{
    /// <summary>
    /// The newest executable named <paramref name="hostName"/> under <c>artifacts/publish/&lt;hostName&gt;/</c>
    /// of the repository that contains <paramref name="startDirectory"/> (found by walking up to <paramref name="repositoryMarker"/>), or null.
    /// </summary>
    public static string? Find(string hostName, string? startDirectory = null, string repositoryMarker = "Colony.slnx")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryMarker);
        string? dir = startDirectory ?? AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, repositoryMarker)))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            return null;
        }

        string publish = Path.Combine(dir, "artifacts", "publish", hostName);
        if (!Directory.Exists(publish))
        {
            return null;
        }

        string file = OperatingSystem.IsWindows() ? hostName + ".exe" : hostName;
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string candidate in Directory.EnumerateFiles(publish, file, SearchOption.AllDirectories))
        {
            var time = File.GetLastWriteTimeUtc(candidate);
            if (newest is null || time > newestTime)
            {
                newest = candidate;
                newestTime = time;
            }
        }

        return newest;
    }

    /// <summary>Runs the host on <paramref name="scenario"/> with <paramref name="options"/> and parses its output; throws <see cref="HarnessException"/> when the host exits non-zero.</summary>
    public static DeterminismResult Run(string executable, Scenario scenario, DeterminismOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--scenario");
        start.ArgumentList.Add(scenario.Name);
        start.ArgumentList.Add("--ticks");
        start.ArgumentList.Add(options.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--every");
        start.ArgumentList.Add(options.CheckpointEvery.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--workers");
        start.ArgumentList.Add(string.Join(',', options.WorkerCounts));

        using var process = Process.Start(start) ?? throw new HarnessException($"Could not start {executable}.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new HarnessException($"{executable} exited with {process.ExitCode}:\n{error}{output}");
        }

        return HarnessHost.Parse(output, scenario.Name, scenario.Seed, options.Ticks, options.CheckpointEvery);
    }

    /// <summary>Runs the scenario in-process and in the host and returns the first Tick whose hashes differ, or null when identical.</summary>
    public static long? CompareWithJit(string executable, Scenario scenario, DeterminismOptions options, out DeterminismResult jit, out DeterminismResult host)
    {
        jit = DeterminismHarness.Run(scenario, options);
        jit.ThrowIfFailed();
        host = Run(executable, scenario, options);
        host.ThrowIfFailed();
        return jit.FirstDivergenceFrom(host);
    }
}
