using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using Colony.Scenario;

namespace Colony.Diagnostics.Tests;

/// <summary>
/// TASK-18 AC #1: the Meter bridge publishes per-Tick and per-System counters that dotnet-counters (through
/// the runtime's MetricsEventSource) and OpenTelemetry (through MeterListener) observe. These tests observe
/// them in-process with MeterListener and EventListener, the same channels dotnet-counters uses:
/// <c>dotnet-counters monitor --process-id &lt;pid&gt; --counters Colony,Colony-Diagnostics</c>.
/// </summary>
[Collection(EventSourceTestGroup.Name)]
public sealed class MeterBridgeTests
{
    private const int Colonists = 1_000;
    private const int Food = 100;

    [Fact]
    public void Per_tick_and_per_system_measurements_match_the_latest_record()
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 2, colonists: Colonists, food: Food);
        using var bridge = new MeterBridge(world, meterName: "Colony.Test.PerTick");
        using var observed = new Observed("Colony.Test.PerTick");

        world.Tick();
        world.Tick();
        bridge.Publish();
        observed.Collect();

        var record = world.Diagnostics.Latest;
        Assert.Equal(record.Tick.Jobs, observed.Long("colony.tick.jobs"));
        Assert.Equal(record.Tick.EntitiesTouched, observed.Long("colony.tick.entities_touched"));
        Assert.Equal(record.Tick.Commands, observed.Long("colony.tick.commands"));
        Assert.Equal(record.Tick.EntityCount, observed.Long("colony.world.entities"));
        Assert.Equal(record.Tick.ChunksInUse, observed.Long("colony.world.chunks_in_use"));
        Assert.Equal(1, observed.Long("colony.ticks"));
        Assert.Equal(record.Tick.WallTicks * 1000.0 / TickRecord.TicksPerSecond, observed.Double("colony.tick.duration"), 6);

        int wander = schema.System("Wander").Id;
        Assert.Equal(record.Systems[wander].Jobs, observed.Long("colony.system.jobs", ("system", "Wander")));
        Assert.Equal(record.Systems[wander].WallTicks * 1000.0 / TickRecord.TicksPerSecond, observed.Double("colony.system.duration", ("system", "Wander")), 6);
        Assert.Equal(record.Systems[wander].CpuTicks * 1000.0 / TickRecord.TicksPerSecond, observed.Double("colony.system.cpu", ("system", "Wander")), 6);
        foreach (ref readonly var descriptor in schema.Systems)
        {
            Assert.Equal(record.Systems[descriptor.Id].AllocatedBytes, observed.Long("colony.system.allocated_bytes", ("system", descriptor.Name)));
        }

        Assert.Equal(record.Workers[0].BusyTicks * 1000.0 / TickRecord.TicksPerSecond, observed.Double("colony.worker.busy", ("worker", "0")), 6);
        Assert.Equal(record.Workers[1].Jobs, observed.Long("colony.worker.jobs", ("worker", "1")));
    }

    [Fact]
    public void Registered_counters_are_published_by_name()
    {
        var builder = new WorldBuilder().AddComponent<Marker>("marker");
        var marker = builder.Component<Marker>();
        var visited = builder.AddCounter("test.visited");
        var schema = builder.AddSystem(new SystemDefinition
        {
            Name = "Visit",
            Phase = Phase.Simulate,
            Reads = ComponentMask.Of(marker),
            ForEachChunk = (in SystemContext context, in ChunkView view) => context.Count(visited, view.Count),
        }).Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 5; i++)
        {
            buffer.Create(ComponentMask.Of(marker));
        }

        world.Playback(buffer);
        world.ResetScratch();
        using var bridge = new MeterBridge(world, meterName: "Colony.Test.Counters");
        using var observed = new Observed("Colony.Test.Counters");

        world.Tick();
        bridge.Publish();
        observed.Collect();

        Assert.Equal(5, observed.Long("colony.counter", ("counter", "test.visited")));
    }

    [Fact]
    public void Publishing_before_any_tick_publishes_nothing()
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 100, food: 10);
        using var bridge = new MeterBridge(world, meterName: "Colony.Test.Empty");
        using var observed = new Observed("Colony.Test.Empty");

        Assert.False(bridge.Publish());
        observed.Collect();

        Assert.Equal(0, bridge.TicksPublished);
        Assert.Equal(0, observed.Long("colony.ticks"));
    }

    [Fact]
    public void The_event_source_reports_each_published_tick_and_polls_counters()
    {
        using var listener = new CountersListener();
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 2, colonists: Colonists, food: Food);
        using var bridge = new MeterBridge(world, meterName: "Colony.Test.EventSource");

        world.Tick();
        bridge.Publish();
        world.Tick();
        bridge.Publish();

        Assert.Equal([0L, 1L], listener.TicksCompleted.ToArray());

        // EventCounters are polled on the EventSource's timer (interval 1 s requested); wait for a full poll.
        Assert.True(bridge.PublishesEventCounters);
        string[] expected = ["ticks", "tick-duration", "tick-jobs", "system-Wander-duration", "system-Eat-jobs"];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !expected.All(listener.CounterNames.Contains))
        {
            Thread.Sleep(50);
        }

        var names = listener.CounterNames;
        foreach (string name in expected)
        {
            Assert.Contains(name, names);
        }
    }

    public struct Marker
    {
        public int Value;
    }

    /// <summary>Observes one Meter in-process the way an OpenTelemetry exporter or dotnet-counters does.</summary>
    private sealed class Observed : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Name, string Tags, long Value)> _longs = [];
        private readonly List<(string Name, string Tags, double Value)> _doubles = [];

        public Observed(string meterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _longs.Add((instrument.Name, TagText(tags), value)));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _doubles.Add((instrument.Name, TagText(tags), value)));
            _listener.Start();
        }

        public void Collect()
        {
            _longs.Clear();
            _doubles.Clear();
            _listener.RecordObservableInstruments();
        }

        public long Long(string name, params (string Key, string Value)[] tags)
        {
            string expected = TagText(tags);
            var matches = _longs.Where(m => m.Name == name && m.Tags == expected).ToList();
            Assert.True(matches.Count == 1, $"expected one measurement of {name} [{expected}], found {matches.Count} among: {string.Join(", ", _longs.Select(m => $"{m.Name} [{m.Tags}]").Distinct())}");
            return matches[0].Value;
        }

        public double Double(string name, params (string Key, string Value)[] tags)
        {
            string expected = TagText(tags);
            var matches = _doubles.Where(m => m.Name == name && m.Tags == expected).ToList();
            Assert.True(matches.Count == 1, $"expected one measurement of {name} [{expected}], found {matches.Count}");
            return matches[0].Value;
        }

        public void Dispose() => _listener.Dispose();

        private static string TagText(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var parts = new List<string>(tags.Length);
            foreach (var tag in tags)
            {
                parts.Add($"{tag.Key}={tag.Value}");
            }

            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        private static string TagText((string Key, string Value)[] tags)
        {
            var parts = tags.Select(t => $"{t.Key}={t.Value}").ToList();
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }
    }

    private sealed class CountersListener : EventListener
    {
        private readonly HashSet<string> _counterNames = new(StringComparer.Ordinal);

        public List<long> TicksCompleted { get; } = [];

        public IReadOnlySet<string> CounterNames
        {
            get
            {
                lock (_counterNames)
                {
                    return new HashSet<string>(_counterNames, StringComparer.Ordinal);
                }
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == MeterBridge.EventSourceName)
            {
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All, new Dictionary<string, string?>(StringComparer.Ordinal) { ["EventCounterIntervalSec"] = "1" });
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != MeterBridge.EventSourceName)
            {
                return;
            }

            if (eventData.EventName == "TickCompleted" && eventData.Payload is { Count: > 0 } && eventData.Payload[0] is long tick)
            {
                lock (TicksCompleted)
                {
                    TicksCompleted.Add(tick);
                }
            }
            else if (eventData.EventName == "EventCounters" && eventData.Payload is { Count: > 0 } && eventData.Payload[0] is IDictionary<string, object> payload && payload.TryGetValue("Name", out var name) && name is string text)
            {
                lock (_counterNames)
                {
                    _counterNames.Add(text);
                }
            }
        }
    }
}
