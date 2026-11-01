using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Colony.Plugins.Native;

namespace Colony.Native.Tests;

/// <summary>
/// AC #1 on Native AOT: the standalone C host loads the published Colony.Native shared library, takes the table
/// from colony_get_api and runs the same suite; its hash must equal the JIT run's. Skips unless
/// <c>dotnet publish src/Colony.Native -c Release -r win-x64</c> (or linux-x64) has run.
/// </summary>
[Collection(NativeTestGroup.Name)]
public sealed partial class AotTests
{
    /// <summary>The newest published Colony.Native shared library under <c>artifacts/publish/Colony.Native/</c>, or null.</summary>
    public static string? FindPublishedLibrary()
    {
        string publish = Path.Combine(CCompiler.RepositoryRoot(), "artifacts", "publish", "Colony.Native");
        if (!Directory.Exists(publish))
        {
            return null;
        }

        string file = OperatingSystem.IsWindows() ? "Colony.Native.dll" : "Colony.Native.so";
        return Directory.EnumerateFiles(publish, file, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [Fact]
    public void The_standalone_C_host_runs_against_the_published_Native_AOT_library_and_matches_JIT()
    {
        string library = ConformanceTests.RequireLibrary();
        string? executable = CCompiler.BuildExecutable("conformance");
        Assert.NotNull(executable);
        string? aot = FindPublishedLibrary();
        if (aot is null)
        {
            Assert.Skip("Colony.Native is not published; run: dotnet publish src/Colony.Native -c Release -r win-x64");
        }

        string jitSave = Path.Combine(Path.GetTempPath(), $"colony-conformance-jit-{Environment.ProcessId}.clnysave");
        string aotSave = Path.Combine(Path.GetTempPath(), $"colony-conformance-aot-{Environment.ProcessId}.clnysave");
        try
        {
            var jit = ConformanceTests.RunInProcess(library, workers: 1, jitSave);
            Assert.True(jit.Status == ColonyAbi.Ok, jit.Error);

            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(aot);
            start.ArgumentList.Add(aotSave);
            start.ArgumentList.Add("1");
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"standalone host exited {process.ExitCode}: {error}{output}");

            var match = OutputLine().Match(output);
            Assert.True(match.Success, "unexpected host output: " + output);
            ulong hash = ulong.Parse(match.Groups["hash"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Assert.Equal(AbiDefinition.Entries.Length, int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture));
            Assert.Equal(AbiDefinition.Entries.Length, int.Parse(match.Groups["covered"].Value, CultureInfo.InvariantCulture));
            Assert.Equal(jit.Hash, hash);
        }
        finally
        {
            File.Delete(jitSave);
            File.Delete(aotSave);
        }
    }

    [GeneratedRegex(@"hash 0x(?<hash>[0-9A-Fa-f]{16}) covered (?<covered>\d+)/(?<count>\d+)")]
    private static partial Regex OutputLine();
}
