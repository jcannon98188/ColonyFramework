using Colony.Diagnostics.Inspection;
using Colony.Fields;
using Colony.Persistence;

namespace Colony.Diagnostics.Tests.Inspection;

/// <summary>TASK-29: the inspection server (spec section 10.2), in-process and over loopback TCP.</summary>
public sealed class InspectionServerTests
{
    private static KeyValuePair<string, InspectValue> P(string key, InspectValue value) => new(key, value);

    [Fact]
    public void A_client_reads_schema_entities_fields_resources_events_spatial_diagnostics_and_inspectors()
    {
        using var fx = InspectFixture.Create(entities: 3);
        fx.Run(2);
        using var client = fx.Server.Connect(InspectFixture.Token);

        Assert.Equal(2, client.Info["tick"].AsWhole);
        Assert.False(client.Info["allowMutation"].AsBoolean);

        var schema = client.Request("schema");
        var vitals = schema["components"].Items.Single(c => c["name"].AsText == "vitals");
        Assert.Equal(["Health", "Hunger"], vitals["fields"].Items.Select(f => f["name"].AsText));
        Assert.Contains(schema["systems"].Items, s => s["name"].AsText == "Count");
        Assert.Contains(schema["inspectors"].Items, i => i.AsText == "tally");

        var entities = client.Request("entities", P("component", "vitals"));
        Assert.Equal(3, entities["total"].AsWhole);
        long second = entities["entities"][1].AsWhole;

        var entity = client.Request("entity", P("entity", second));
        Assert.True(entity["alive"].AsBoolean);
        var value = entity["components"].Items.Single(c => c["name"].AsText == "vitals")["value"];
        Assert.Equal(11, value["Health"].AsWhole);
        Assert.Equal(0.5, value["Hunger"].AsNumber);

        var resources = client.Request("resources")["resources"].Items;
        var tally = resources.Single(r => r["name"].AsText == "tally");
        Assert.Equal(2L, BitConverter.ToInt64(tally["bytes"].AsBytes));

        var events = client.Request("events", P("name", "ping"));
        Assert.Equal(4, events["size"].AsWhole);

        var field = client.Request("field", P("name", "temperature"), P("x", 0), P("y", 0), P("w", 2), P("h", 2), P("as", "int"));
        Assert.Equal(20, field["rows"][1][1].AsWhole);
        Assert.True(client.Request("field", P("name", "temperature"), P("x", 40), P("y", 0))["rows"][0][0].IsNull);

        var near = client.Request("spatial", P("kind", "tile"), P("x", 3), P("y", 3));
        Assert.Equal(second, Assert.Single(near["hits"].Items)["entity"].AsWhole);
        var radius = client.Request("spatial", P("kind", "radius"), P("x", 3), P("y", 3), P("r", 1));
        Assert.Equal(3, radius["hits"].Items.Count);

        var diagnostics = client.Request("diagnostics");
        Assert.True(diagnostics["recorded"].AsBoolean);
        Assert.Equal(1, diagnostics["tick"].AsWhole);
        Assert.Contains(diagnostics["systems"].Items, s => s["name"].AsText == "Count");

        var inspected = client.Request("inspect", P("name", "tally"));
        Assert.Equal(2, inspected["entries"][0]["value"].AsWhole);
        Assert.Equal("fixture", inspected["entries"][1]["value"].AsText);
    }

    [Fact]
    public void Without_the_token_every_request_is_refused_and_the_tcp_connection_closed()
    {
        using var fx = InspectFixture.Create();
        var wrong = Assert.Throws<InspectionException>(() => fx.Server.Connect("not-the-token"));
        Assert.Equal("unauthorized", wrong.Code);

        fx.Server.ListenTcp();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = fx.StartHostLoop(cts.Token);
        try
        {
            var refused = Assert.Throws<InspectionException>(() => InspectionClient.ConnectTcp(fx.Server.Endpoint!.Port, "nope"));
            Assert.Equal("unauthorized", refused.Code);

            // A raw client that skips hello gets refused too, then the server hangs up.
            using var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect(System.Net.IPAddress.Loopback, fx.Server.Endpoint!.Port);
            var stream = tcp.GetStream();
            stream.ReadTimeout = 10_000;
            InspectCodec.WritePreamble(stream);
            InspectCodec.WriteFrame(stream, InspectValue.Map(P("op", "schema"), P("id", 1)));
            var reply = InspectCodec.ReadFrame(stream)!;
            Assert.False(reply["ok"].AsBoolean);
            Assert.Equal("unauthorized", reply["code"].AsText);
            Assert.Null(InspectCodec.ReadFrame(stream));
            Assert.Equal(3, fx.Server.ConnectionsRefused);

            using var ok = InspectionClient.ConnectTcp(fx.Server.Endpoint.Port, InspectFixture.Token);
            Assert.Single(ok.Request("schema")["inspectors"].Items);
            ok.Request("shutdown");
        }
        finally
        {
            cts.Cancel();
            host.Join();
        }
    }

    [Fact]
    public void A_generated_token_is_random_and_an_empty_token_is_refused()
    {
        using var world = InspectFixture.BuildSchema().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var a = new InspectionServer(world);
        using var b = new InspectionServer(world);
        Assert.Equal(32, a.Token.Length);
        Assert.NotEqual(a.Token, b.Token);
        Assert.Throws<ArgumentException>(() => new InspectionServer(world, new InspectionServerOptions { Token = "" }));
    }

    [Fact]
    public void Mutation_is_forbidden_unless_the_host_allows_it()
    {
        using var fx = InspectFixture.Create(allowMutation: false);
        using var client = fx.Server.Connect(InspectFixture.Token);
        var e = Assert.Throws<InspectionException>(() => client.Request("mutate", P("action", "create"), P("components", InspectValue.List("vitals"))));
        Assert.Equal("forbidden", e.Code);
        fx.Run(1);
        Assert.Equal(3, fx.World.EntityCount);
    }

    [Fact]
    public void Mutation_needs_the_debug_commands_in_the_schema()
    {
        using var fx = InspectFixture.Create(allowMutation: true, debugCommands: false);
        using var client = fx.Server.Connect(InspectFixture.Token);
        var e = Assert.Throws<InspectionException>(() => client.Request("mutate", P("action", "destroy"), P("entity", 1)));
        Assert.Equal("not-available", e.Code);
    }

    [Fact]
    public void Debug_mutations_go_through_the_command_buffer_into_the_replay_log_and_replay_identically()
    {
        using var fx = InspectFixture.Create(allowMutation: true);
        using var client = fx.Server.Connect(InspectFixture.Token);
        long start = fx.World.CurrentTick;
        long target = client.Request("entities")["entities"][0].AsWhole;
        byte[] value = [99, 0, 0, 0, 0, 0, 0x80, 0x3F]; // Health 99, Hunger 1.0f

        var submitted = client.Request("mutate", P("action", "add"), P("entity", target), P("component", "vitals"), P("bytes", InspectValue.From(value)));
        Assert.Equal(start, submitted["tick"].AsWhole);
        client.Request("mutate", P("action", "create"), P("components", InspectValue.List("vitals", "tile.pos")));
        Assert.Equal(3, fx.World.EntityCount);

        fx.Run(1);

        Assert.Equal(4, fx.World.EntityCount);
        var read = client.Request("entity", P("entity", target))["components"].Items.Single(c => c["name"].AsText == "vitals")["value"];
        Assert.Equal(99, read["Health"].AsWhole);
        Assert.Equal(1.0, read["Hunger"].AsNumber);

        client.Request("mutate", P("action", "remove"), P("entity", target), P("component", "tile.pos"));
        fx.Run(1);
        client.Request("mutate", P("action", "destroy"), P("entity", target));
        fx.Run(2);
        Assert.Equal(3, fx.World.EntityCount);

        var log = fx.Recorder.Finish();
        Assert.True(log.TryGetFrame(start, out var frame));
        Assert.Equal(2, frame.Count);

        using var replayed = log.CreateWorld(log.StartTick, fx.Schema);
        new ReplayPlayer(log, replayed).SeekTo(log.EndTick);
        Assert.Equal(fx.World.Hash(), replayed.Hash());
    }

    [Fact]
    public void Pause_step_and_resume_go_through_the_shared_tick_gate()
    {
        using var fx = InspectFixture.Create();
        fx.Server.ListenTcp();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var host = fx.StartHostLoop(cts.Token);
        try
        {
            using var client = InspectionClient.ConnectTcp(fx.Server.Endpoint!.Port, InspectFixture.Token);
            var paused = client.Request("pause");
            Assert.True(paused["paused"].AsBoolean);
            long at = client.Request("status")["tick"].AsWhole;
            Thread.Sleep(50);
            Assert.Equal(at, client.Request("status")["tick"].AsWhole);

            Assert.Equal(at + 3, client.StepAndWait(3));
            Thread.Sleep(50);
            Assert.Equal(at + 3, client.Request("status")["tick"].AsWhole);
            Assert.True(fx.Gate.IsHeld);

            client.Request("resume");
            client.WaitForTick(at + 10);
            Assert.False(fx.Gate.IsHeld);
            client.Request("shutdown");
        }
        finally
        {
            cts.Cancel();
            host.Join();
        }

        Assert.True(fx.Server.ShutdownRequested);
    }

    [Fact]
    public void Rewind_rebuilds_a_past_tick_from_the_replay_and_live_returns_without_touching_the_world()
    {
        using var fx = InspectFixture.Create(allowMutation: true);
        var hashes = new List<ulong> { fx.World.Hash() };
        for (int i = 0; i < 6; i++)
        {
            fx.Run(1);
            hashes.Add(fx.World.Hash());
        }

        using var client = fx.Server.Connect(InspectFixture.Token);
        var rewound = client.Request("rewind", P("tick", 2));
        Assert.Equal(2, rewound["tick"].AsWhole);
        Assert.Equal(hashes[2], (ulong)rewound["hash"].AsWhole);
        Assert.True(fx.Gate.IsHeld);
        Assert.Equal(2L, BitConverter.ToInt64(client.Request("resources")["resources"].Items.Single(r => r["name"].AsText == "tally")["bytes"].AsBytes));
        Assert.Equal("forbidden", Assert.Throws<InspectionException>(() => client.Request("mutate", P("action", "destroy"), P("entity", 1))).Code);

        client.Request("step", P("count", 2));
        Assert.Equal(4, client.Request("status")["rewound"].AsWhole);
        Assert.Equal(hashes[4], fx.Server.InspectedWorld.Hash());

        var status = client.Request("live");
        Assert.True(status["rewound"].IsNull);
        Assert.Equal(6, status["tick"].AsWhole);
        Assert.Equal(hashes[6], fx.World.Hash());

        Assert.Equal("bad-request", Assert.Throws<InspectionException>(() => client.Request("rewind", P("tick", 99))).Code);
    }

    [Fact]
    public void Diff_compares_the_world_with_a_save_entity_by_entity()
    {
        using var fx = InspectFixture.Create(allowMutation: true);
        using var client = fx.Server.Connect(InspectFixture.Token);
        var save = client.Request("snapshot")["save"];
        Assert.True(client.Request("diff", P("save", save))["identical"].AsBoolean);

        long target = client.Request("entities")["entities"][0].AsWhole;
        client.Request("mutate", P("action", "destroy"), P("entity", target));
        fx.Run(1);

        var diff = client.Request("diff", P("save", save));
        Assert.False(diff["identical"].AsBoolean);
        var kinds = diff["differences"].Items.Select(d => d["kind"].AsText).ToList();
        Assert.Contains("tick", kinds);
        Assert.Contains("entity-only-right", kinds);
        Assert.Contains("resource", kinds);
        Assert.Equal(target, diff["differences"].Items.Single(d => d["kind"].AsText == "entity-only-right")["entity"].AsWhole);
    }

    [Fact]
    public void Unknown_ops_bad_arguments_and_missing_features_report_their_codes()
    {
        using var fx = InspectFixture.Create();
        using var client = fx.Server.Connect(InspectFixture.Token);
        Assert.Equal("unknown-op", Assert.Throws<InspectionException>(() => client.Request("frobnicate")).Code);
        Assert.Equal("bad-request", Assert.Throws<InspectionException>(() => client.Request("entity")).Code);
        Assert.Equal("bad-request", Assert.Throws<InspectionException>(() => client.Request("inspect", P("name", "nope"))).Code);
        Assert.False(client.Request("entity", P("entity", 123456789))["alive"].AsBoolean);

        using var bare = InspectFixture.BuildSchema().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var server = new InspectionServer(bare, new InspectionServerOptions { Token = "t" });
        using var plain = server.Connect("t");
        Assert.Equal("not-available", Assert.Throws<InspectionException>(() => plain.Request("pause")).Code);
        Assert.Equal("not-available", Assert.Throws<InspectionException>(() => plain.Request("rewind", P("tick", 0))).Code);
    }
}
