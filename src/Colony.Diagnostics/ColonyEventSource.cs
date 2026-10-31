using System.Diagnostics.Tracing;

namespace Colony.Diagnostics;

/// <summary>
/// The <c>Colony-Diagnostics</c> EventSource: a <c>TickCompleted</c> event per published Tick and
/// EventCounters (polled on the EventSource timer) mirroring the <see cref="MeterBridge"/> snapshot, for
/// tools that speak EventPipe but not System.Diagnostics.Metrics
/// (<c>dotnet-counters monitor --counters Colony-Diagnostics</c>, <c>dotnet-trace</c>). Process-wide:
/// the counters follow the first <see cref="MeterBridge"/> attached.
/// </summary>
[EventSource(Name = SourceName)]
internal sealed class ColonyEventSource : EventSource
{
    public const string SourceName = "Colony-Diagnostics";

    public static ColonyEventSource Log { get; } = new();

    private readonly Lock _lock = new();
    private readonly List<DiagnosticCounter> _counters = [];
    private MeterBridge? _bridge;

    private ColonyEventSource()
    {
    }

    /// <summary>Makes <paramref name="bridge"/> the source of the counters unless another bridge already is; returns whether it did.</summary>
    [NonEvent]
    public bool TryAttach(MeterBridge bridge)
    {
        lock (_lock)
        {
            if (_bridge is not null)
            {
                return false;
            }

            _bridge = bridge;
            CreateCounters(bridge);
            return true;
        }
    }

    [NonEvent]
    public void Detach(MeterBridge bridge)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_bridge, bridge))
            {
                return;
            }

            _bridge = null;
            foreach (var counter in _counters)
            {
                counter.Dispose();
            }

            _counters.Clear();
        }
    }

    /// <summary>One published Tick: its number, wall time (Stopwatch ticks) and managed bytes allocated on Tick threads.</summary>
    [Event(1, Level = EventLevel.Informational)]
    public void TickCompleted(long tick, long wallTicks, long allocatedBytes)
    {
        if (IsEnabled())
        {
            WriteEvent(1, tick, wallTicks, allocatedBytes);
        }
    }

    [NonEvent]
    private void CreateCounters(MeterBridge bridge)
    {
        _counters.Add(new IncrementingPollingCounter("ticks", this, () => bridge.TicksPublished) { DisplayName = "Ticks", DisplayRateTimeScale = TimeSpan.FromSeconds(1) });
        _counters.Add(new PollingCounter("tick-duration", this, () => MeterBridge.Millis(bridge.Snapshot().WallTicks)) { DisplayName = "Tick wall time", DisplayUnits = "ms" });
        _counters.Add(new PollingCounter("tick-playback", this, () => MeterBridge.Millis(bridge.Snapshot().PlaybackTicks)) { DisplayName = "Tick playback time", DisplayUnits = "ms" });
        _counters.Add(new PollingCounter("tick-jobs", this, () => bridge.Snapshot().Jobs) { DisplayName = "Jobs per Tick" });
        _counters.Add(new PollingCounter("tick-chunks-visited", this, () => bridge.Snapshot().ChunksVisited) { DisplayName = "Chunks visited per Tick" });
        _counters.Add(new PollingCounter("tick-entities-touched", this, () => bridge.Snapshot().EntitiesTouched) { DisplayName = "Entities touched per Tick" });
        _counters.Add(new PollingCounter("tick-commands", this, () => bridge.Snapshot().Commands) { DisplayName = "Commands per Tick" });
        _counters.Add(new PollingCounter("tick-allocated-bytes", this, () => bridge.Snapshot().AllocatedBytes) { DisplayName = "Allocated on Tick threads", DisplayUnits = "B" });
        _counters.Add(new PollingCounter("world-entities", this, () => bridge.Snapshot().EntityCount) { DisplayName = "Entities" });
        _counters.Add(new PollingCounter("world-chunks-in-use", this, () => bridge.Snapshot().ChunksInUse) { DisplayName = "Chunks in use" });
        _counters.Add(new PollingCounter("world-reserved-bytes", this, () => bridge.Snapshot().ReservedBytes) { DisplayName = "Reserved native bytes", DisplayUnits = "B" });
        for (int s = 0; s < bridge.SystemCount; s++)
        {
            int id = s;
            string name = bridge.SystemName(s);
            _counters.Add(new PollingCounter($"system-{name}-duration", this, () => MeterBridge.Millis(bridge.System(id).WallTicks)) { DisplayName = $"{name} wall time", DisplayUnits = "ms" });
            _counters.Add(new PollingCounter($"system-{name}-cpu", this, () => MeterBridge.Millis(bridge.System(id).CpuTicks)) { DisplayName = $"{name} job time", DisplayUnits = "ms" });
            _counters.Add(new PollingCounter($"system-{name}-jobs", this, () => bridge.System(id).Jobs) { DisplayName = $"{name} jobs" });
        }
    }
}
