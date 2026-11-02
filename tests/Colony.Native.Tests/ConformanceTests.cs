using System.Runtime.InteropServices;
using Colony.Persistence;
using Colony.Plugins;
using Colony.Plugins.Native;

namespace Colony.Native.Tests;

/// <summary>The native tests share process-wide state (the C Plugin's ids, the handle table), so they never run concurrently.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeTestGroup
{
    public const string Name = "native";
}

/// <summary>
/// AC #1 and #2 on JIT: the C host (Conformance/conformance.c) loaded in-process registers its Native Plugin, builds
/// a schema, drives Worlds through the table and reports which entries it called.
/// </summary>
[Collection(NativeTestGroup.Name)]
public sealed unsafe class ConformanceTests
{
    public static string RequireLibrary()
    {
        string? library = CCompiler.BuildSharedLibrary("conformance");
        if (library is null)
        {
            Assert.Skip("No C compiler found (MSVC Build Tools with vcvars64, or cc/gcc/clang on PATH); the C conformance host cannot be built.");
        }

        return library;
    }

    /// <summary>Runs <c>colony_conformance_run</c> of the in-process C host against Colony.Plugins' table.</summary>
    public static (int Status, ulong Hash, byte[] Covered, string Error) RunInProcess(string library, int workers, string savePath)
    {
        nint module = NativeLibrary.Load(library);
        var run = (delegate* unmanaged<nint, int, byte*, ulong*, byte*, byte*, int, int>)NativeLibrary.GetExport(module, "colony_conformance_run");
        var covered = new byte[AbiDefinition.Entries.Length];
        var error = new byte[1024];
        byte[] path = System.Text.Encoding.UTF8.GetBytes(savePath + "\0");
        ulong hash = 0;
        int status;
        fixed (byte* c = covered, e = error, p = path)
        {
            status = run(ColonyAbi.Table, workers, p, &hash, c, e, error.Length);
        }

        return (status, hash, covered, System.Text.Encoding.UTF8.GetString(error).TrimEnd('\0'));
    }

    [Fact]
    public void The_C_host_runs_the_whole_suite_on_JIT_and_exercises_every_table_entry()
    {
        string library = RequireLibrary();
        string save = Path.Combine(Path.GetTempPath(), $"colony-conformance-{Environment.ProcessId}.clnysave");
        try
        {
            var (status, hash, covered, error) = RunInProcess(library, workers: 1, save);
            Assert.True(status == ColonyAbi.Ok, $"conformance status {status}: {error}");
            Assert.NotEqual(0UL, hash);

            var missed = AbiDefinition.Entries.Where((e, i) => covered[i] == 0).Select(e => e.Name).ToArray();
            Assert.True(missed.Length == 0, "entries never called by the conformance host: " + string.Join(", ", missed));
        }
        finally
        {
            File.Delete(save);
        }
    }

    [Fact]
    public void The_C_host_hashes_identically_with_one_and_four_workers()
    {
        string library = RequireLibrary();
        string save = Path.Combine(Path.GetTempPath(), $"colony-conformance-workers-{Environment.ProcessId}.clnysave");
        try
        {
            var one = RunInProcess(library, workers: 1, save);
            var four = RunInProcess(library, workers: 4, save);
            Assert.True(one.Status == ColonyAbi.Ok, one.Error);
            Assert.True(four.Status == ColonyAbi.Ok, four.Error);
            Assert.Equal(one.Hash, four.Hash);
        }
        finally
        {
            File.Delete(save);
        }
    }

    [Fact]
    public void The_save_written_by_the_C_host_records_the_Native_Plugin_with_its_ABI_version()
    {
        string library = RequireLibrary();
        string save = Path.Combine(Path.GetTempPath(), $"colony-conformance-manifest-{Environment.ProcessId}.clnysave");
        try
        {
            var run = RunInProcess(library, workers: 1, save);
            Assert.True(run.Status == ColonyAbi.Ok, run.Error);
            var manifest = WorldPersistence.ReadManifest(save);
            var plugin = Assert.Single(manifest.Plugins);
            Assert.Equal("conformance.native", plugin.Id);
            Assert.Equal(ColonyAbi.Version, plugin.AbiVersion);
            Assert.True(plugin.Deterministic);
            Assert.Contains("native.position", plugin.Components);
            Assert.Contains("native.summary", plugin.Resources);
        }
        finally
        {
            File.Delete(save);
        }
    }

    [Fact]
    public void A_Native_Plugin_built_against_a_newer_ABI_is_refused_at_add()
    {
        var id = "newer\0"u8.ToArray();
        fixed (byte* p = id)
        {
            var desc = new ColonyPluginDesc { AbiVersion = ColonyAbi.Version + 1, Id = p };
            var e = Assert.Throws<PluginException>(() => new NativePlugin(in desc));
            Assert.Contains("C ABI version", e.Message, StringComparison.Ordinal);
        }
    }
}
