using System.Diagnostics;
using Colony.Scenario;

namespace Colony.Core.Tests.Scenario;

/// <summary>
/// AC #1 and #7: the prototype's colonist scenario (100k colonists, 5k food, 1% churn) hashes identically
/// on 1, 2, 4 and N threads with zero steady-state allocation, and a thread-order dependency breaks it.
/// The Native AOT comparison runs the published tests/Colony.Core.AotHost when it exists.
/// </summary>
public sealed class ColonistScenarioTests
{
    private const int Ticks = 60;
    private static readonly int[] Checkpoints = [1, 10, 30, 60];

    [Fact]
    public void The_colonist_scenario_hashes_identically_on_1_2_4_and_N_threads()
    {
        int n = Math.Max(4, Environment.ProcessorCount);
        ulong[]? baseline = null;
        foreach (int workers in new[] { 1, 2, 4, n })
        {
            using var world = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), workers);
            Assert.Equal(workers, world.WorkerCount);
            var hashes = ColonistScenario.Run(world, Ticks, Checkpoints);

            Assert.Equal(ColonistScenario.DefaultColonists + ColonistScenario.DefaultFood, world.EntityCount);
            Assert.True(world.Counters.Destroys > Ticks * ColonistScenario.DefaultColonists / 100, "the churn and the eating must have happened");
            if (baseline is null)
            {
                baseline = hashes;
            }
            else
            {
                Assert.Equal(baseline, hashes);
            }
        }
    }

    [Fact]
    public void A_thread_order_dependency_fails_the_hash_comparison()
    {
        using var one = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(sabotage: true), 1);
        using var four = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(sabotage: true), 4);

        var a = ColonistScenario.Run(one, 10, [1]);
        var b = ColonistScenario.Run(four, 10, [1]);

        Assert.NotEqual(a[^1], b[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_steady_state_tick_allocates_nothing_on_any_tick_thread(int workers)
    {
        using var world = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), workers);
        for (int i = 0; i < 30; i++)
        {
            world.Tick();
        }

        world.Hash();
        long mainBefore = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        for (int i = 0; i < 10; i++)
        {
            world.Tick();
        }

        world.Hash();
        long mainAllocated = GC.GetAllocatedBytesForCurrentThread() - mainBefore;
        long workersAllocated = world.WorkerAllocatedBytes - workersBefore;

        Assert.Equal(0, mainAllocated);
        Assert.Equal(0, workersAllocated);
    }

    [Fact]
    public void The_native_aot_host_produces_the_same_hashes_as_the_jit_run()
    {
        string? exe = FindAotHost();
        if (exe is null)
        {
            Assert.Skip("Publish the AOT host first: dotnet publish tests/Colony.Core.AotHost -c Release -r <rid> (see CONVENTIONS, TASK-7).");
        }

        using var world = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), 1);
        var expected = ColonistScenario.Run(world, 30, [1, 10, 30]);

        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "--ticks", "30", "--workers", "1,4", "--checkpoints", "1,10,30" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        foreach (string line in lines)
        {
            var parts = line.Split(' ');
            var hashes = parts[1..].Select(p => Convert.ToUInt64(p, 16)).ToArray();
            Assert.Equal(expected, hashes);
        }
    }

    private static string? FindAotHost()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Colony.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            return null;
        }

        string publish = Path.Combine(dir, "artifacts", "publish", "Colony.Core.AotHost");
        if (!Directory.Exists(publish))
        {
            return null;
        }

        string name = OperatingSystem.IsWindows() ? "Colony.Core.AotHost.exe" : "Colony.Core.AotHost";
        return Directory.EnumerateFiles(publish, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
