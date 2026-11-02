using Colony.Diagnostics.Inspection;
using Colony.Fields;
using Colony.Persistence;
using Colony.Plugins;
using Colony.Scenario;

namespace Colony.Diagnostics.Tests.Inspection;

/// <summary>TASK-29: colony-inspect against a running host over loopback TCP, and its offline commands.</summary>
public sealed class InspectCliTests
{
    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = InspectCli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Colony_inspect_reads_steps_mutates_rewinds_and_diffs_a_running_host()
    {
        using var fx = InspectFixture.Create(allowMutation: true, entities: 3);
        fx.Run(1); // the spatial index updates in the Late Phase
        fx.Gate.Hold();
        fx.Server.ListenTcp();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var host = fx.StartHostLoop(cts.Token);
        string dir = Directory.CreateTempSubdirectory("colony-inspect-").FullName;
        try
        {
            string port = fx.Server.Endpoint!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string[] at = ["--port", port, "--token", InspectFixture.Token];
            string[] Args(params string[] command) => [.. command, .. at];

            var schema = Cli(Args("schema"));
            Assert.Equal(0, schema.Exit);
            Assert.Contains("name: vitals", schema.Out, StringComparison.Ordinal);

            var json = Cli(Args("entities", "--component", "vitals", "--json"));
            var entities = InspectJson.Read(json.Out);
            Assert.Equal(3, entities["total"].AsWhole);
            string first = entities["entities"][0].AsWhole.ToString(System.Globalization.CultureInfo.InvariantCulture);

            Assert.Contains("Health: 10", Cli(Args("entity", first)).Out, StringComparison.Ordinal);
            Assert.Contains("[20, 20]", Cli(Args("field", "temperature", "0", "0", "--w", "2", "--int")).Out, StringComparison.Ordinal);
            Assert.Contains("entity: " + first, Cli(Args("spatial", "tile", "2", "3")).Out, StringComparison.Ordinal);
            Assert.Contains("ticks", Cli(Args("inspect", "tally")).Out, StringComparison.Ordinal);
            Assert.Equal(0, Cli(Args("resources")).Exit);
            Assert.Equal(0, Cli(Args("events", "ping")).Exit);
            Assert.Equal(0, Cli(Args("diagnostics")).Exit);

            // Paused by the host: step through the gate, then a debug mutation lands on the next Tick.
            Assert.Contains("paused: true", Cli(Args("status")).Out, StringComparison.Ordinal);
            Assert.Equal(0, Cli(Args("step", "2")).Exit);
            using (var client = InspectionClient.ConnectTcp(fx.Server.Endpoint.Port, InspectFixture.Token))
            {
                client.WaitForTick(3);
            }

            string save = Path.Combine(dir, "before.save");
            Assert.Equal(0, Cli(Args("snapshot", save)).Exit);
            Assert.Equal(0, Cli(Args("diff", save)).Exit);

            Assert.Equal(0, Cli(Args("mutate", "destroy", first)).Exit);
            Assert.Equal(0, Cli(Args("step")).Exit);
            using (var client = InspectionClient.ConnectTcp(fx.Server.Endpoint.Port, InspectFixture.Token))
            {
                client.WaitForTick(4);
                Assert.Equal(2, client.Request("status")["entities"].AsWhole);
            }

            var diff = Cli(Args("diff", save));
            Assert.Equal(1, diff.Exit);
            Assert.Contains("entity-only-right", diff.Out, StringComparison.Ordinal);

            var rewind = Cli(Args("rewind", "2"));
            Assert.Equal(0, rewind.Exit);
            Assert.Contains("tick: 2", rewind.Out, StringComparison.Ordinal);
            Assert.Equal(3, InspectJson.Read(Cli(Args("entities", "--json")).Out)["total"].AsWhole);
            Assert.Equal(0, Cli(Args("live")).Exit);

            var raw = Cli(Args("raw", """{"op":"status"}""", "--json"));
            Assert.Equal(4, InspectJson.Read(raw.Out)["tick"].AsWhole);

            string after = Path.Combine(dir, "after.save");
            Assert.Equal(0, Cli(Args("snapshot", after)).Exit);
            var offline = Cli("diff", save, after);
            Assert.Equal(1, offline.Exit);
            Assert.Contains("worldHash", offline.Out, StringComparison.Ordinal);

            Assert.Equal(0, Cli(Args("shutdown")).Exit);
            host.Join();
        }
        finally
        {
            cts.Cancel();
            host.Join();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Colony_inspect_reports_refusals_and_bad_usage_with_exit_codes()
    {
        using var fx = InspectFixture.Create(allowMutation: false);
        fx.Server.ListenTcp();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var host = fx.StartHostLoop(cts.Token);
        try
        {
            string port = fx.Server.Endpoint!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var refused = Cli("schema", "--port", port, "--token", "wrong");
            Assert.Equal(3, refused.Exit);
            Assert.Contains("unauthorized", refused.Err, StringComparison.Ordinal);

            var forbidden = Cli("mutate", "destroy", "1", "--port", port, "--token", InspectFixture.Token);
            Assert.Equal(3, forbidden.Exit);
            Assert.Contains("forbidden", forbidden.Err, StringComparison.Ordinal);

            Assert.Equal(2, Cli().Exit);
            Assert.Equal(2, Cli("schema").Exit);
            Assert.Equal(2, Cli("frobnicate", "--port", port, "--token", InspectFixture.Token).Exit);
            Assert.Equal(2, Cli("entity", "--port", port, "--token", InspectFixture.Token).Exit);
            Assert.Equal(0, Cli("--help", "x").Exit);
            Cli("shutdown", "--port", port, "--token", InspectFixture.Token);
        }
        finally
        {
            cts.Cancel();
            host.Join();
        }
    }

    [Fact]
    public void Colony_inspect_desync_names_the_first_diverging_system_from_two_trail_files()
    {
        string dir = Directory.CreateTempSubdirectory("colony-desync-").FullName;
        try
        {
            var schema = ColonistScenario.BuildSchema(sabotage: true, 500);
            string Trail(int workers)
            {
                var recorder = new HashTrailRecorder();
                using var world = ColonistScenario.CreateWorld(schema, workers, colonists: 5_000, food: 500, options: new DiagnosticsOptions { HashTrail = true, Listener = recorder });
                world.Tick();
                world.Tick();
                string path = Path.Combine(dir, $"w{workers}.trail");
                HashTrailFile.From(schema, recorder.Ticks).WriteFile(path);
                return path;
            }

            string one = Trail(1);
            string four = Trail(4);

            var same = Cli("desync", one, one);
            Assert.Equal(0, same.Exit);
            Assert.Contains("agree over 2 Ticks", same.Out, StringComparison.Ordinal);

            var diverged = Cli("desync", one, four);
            Assert.Equal(1, diverged.Exit);
            Assert.StartsWith("diverged: Tick 0, Phase Simulate, System Wander", diverged.Out, StringComparison.Ordinal);

            var json = InspectJson.Read(Cli("desync", one, four, "--json").Out);
            Assert.Equal(schema.System("Wander").Id, json["systemId"].AsWhole);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Inspectors_a_plugin_registers_appear_in_colony_inspect()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var host = new PluginHost(new PluginHostOptions { Fields = builder.AddFields(new WorldSize(16, 16, 1)) });
        host.Add(new InspectorPlugin());
        var schema = host.Build(builder);
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var server = new InspectionServer(world, new InspectionServerOptions { Token = "plugin-token" });
        server.ListenTcp();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var loop = new Thread(() =>
        {
            while (!server.ShutdownRequested && !cts.IsCancellationRequested)
            {
                server.WaitForRequest(TimeSpan.FromMilliseconds(20));
                server.Pump();
            }
        });
        loop.Start();
        try
        {
            string port = server.Endpoint!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var list = Cli("inspectors", "--port", port, "--token", "plugin-token");
            Assert.Equal(0, list.Exit);
            Assert.Contains("- test.economy.prices", list.Out, StringComparison.Ordinal);

            var run = InspectJson.Read(Cli("inspect", "test.economy.prices", "--port", port, "--token", "plugin-token", "--json").Out);
            Assert.Equal("bread", run["entries"][0]["name"].AsText);
            Assert.Equal(3, run["entries"][0]["value"].AsWhole);
            Cli("shutdown", "--port", port, "--token", "plugin-token");
        }
        finally
        {
            cts.Cancel();
            loop.Join();
        }
    }

    private sealed class InspectorPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = new() { Id = "test.economy", Version = "1.0.0" };

        public void Register(PluginRegistration registration)
            => registration.AddInspector("prices", (_, output) => output.Add("bread", 3L));
    }
}
