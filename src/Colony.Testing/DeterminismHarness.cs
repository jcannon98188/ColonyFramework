namespace Colony.Testing;

/// <summary>
/// The determinism harness (spec section 10.3 and 6): runs a <see cref="Scenario"/> once per configured
/// worker count, hashes the World at every checkpoint and compares the sequences, reporting the first
/// diverging Tick. A System fault or a leak at dispose fails the run too. Compare the result with the
/// committed hashes through the golden-hash store, or with a Native AOT build through the harness host.
/// </summary>
public static class DeterminismHarness
{
    public static DeterminismResult Run(Scenario scenario, DeterminismOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        options ??= new DeterminismOptions();
        options.Validate();
        var checkpoints = options.ComputeCheckpoints();
        var runs = new DeterminismRun[options.WorkerCounts.Count];
        for (int i = 0; i < runs.Length; i++)
        {
            runs[i] = RunOnce(scenario, options, checkpoints, options.WorkerCounts[i]);
        }

        return new DeterminismResult(scenario.Name, scenario.Seed, checkpoints, runs);
    }

    /// <summary>Builds the scenario's schema on a fresh builder, applying the harness's debug-validation override.</summary>
    internal static WorldSchema BuildSchema(Scenario scenario, bool? debugValidation, Action<WorldBuilder>? decorate = null)
    {
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        decorate?.Invoke(builder);
        if (debugValidation is { } debug)
        {
            builder.DebugValidation(debug);
        }

        return builder.Build();
    }

    internal static World CreateWorld(Scenario scenario, WorldSchema schema, int workers, IDebugListener? listener)
    {
        var world = schema.CreateWorld(new WorldOptions
        {
            Seed = scenario.Seed,
            WorkerThreads = workers,
            InitialEntityCapacity = scenario.InitialEntityCapacity,
            DebugListener = listener,
        });
        try
        {
            scenario.Populate(world);
        }
        catch
        {
            world.Dispose();
            throw;
        }

        return world;
    }

    private static DeterminismRun RunOnce(Scenario scenario, DeterminismOptions options, int[] checkpoints, int workers)
    {
        var hashes = new ulong[checkpoints.Length];
        var leaks = new LeakListener();
        int completed = 0;
        WorldFault? fault = null;
        Exception? error = null;
        World? world = null;
        try
        {
            var schema = BuildSchema(scenario, options.DebugValidation);
            world = CreateWorld(scenario, schema, workers, leaks);
            int next = 0;
            for (int tick = 1; tick <= options.Ticks && next < checkpoints.Length; tick++)
            {
                scenario.Advance(world);
                if (checkpoints[next] == tick)
                {
                    hashes[next++] = world.Hash();
                    completed = next;
                }
            }
        }
        catch (SystemFaultException e)
        {
            fault = world?.Fault;
            error = e;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e;
        }
        finally
        {
            world?.Dispose();
        }

        return new DeterminismRun(workers, hashes, completed, fault, error, leaks.Report);
    }

    internal sealed class LeakListener : IDebugListener
    {
        public LeakReport Report { get; private set; }

        public void OnLeak(in LeakReport report) => Report = report;
    }
}
