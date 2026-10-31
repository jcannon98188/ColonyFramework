using System.Diagnostics.Metrics;
using System.Globalization;

namespace Colony.Diagnostics;

/// <summary>
/// Publishes a World's per-Tick and per-System diagnostics as <see cref="System.Diagnostics.Metrics.Meter"/>
/// instruments and as EventCounters on the <c>Colony-Diagnostics</c> EventSource, so
/// <c>dotnet-counters monitor --process-id &lt;pid&gt; --counters Colony,Colony-Diagnostics</c> and any
/// OpenTelemetry/MeterListener consumer see them without the host writing glue (spec section 10.1).
/// </summary>
/// <remarks>
/// The host calls <see cref="Publish"/> between Ticks; it copies the latest record into preallocated arrays
/// under a lock and allocates nothing. Observable instruments and polling counters read that snapshot on the
/// collector's own thread when a listener asks, never on a Framework thread, so simulation state is never
/// touched and the World hash is unaffected. Durations are milliseconds. Per-System and per-worker instruments
/// carry a <c>system</c> or <c>worker</c> tag; registered counters (<see cref="WorldBuilder.AddCounter"/>)
/// carry a <c>counter</c> tag. EventCounters cannot carry tags, so they are flattened to
/// <c>system-&lt;name&gt;-duration</c> etc.; the EventSource is process-wide and follows the first bridge
/// created (later bridges still publish their Meter).
/// </remarks>
public sealed class MeterBridge : IDisposable
{
    public const string DefaultMeterName = "Colony";

    /// <summary>The EventSource name to pass to <c>dotnet-counters --counters</c> for the EventCounters view.</summary>
    public const string EventSourceName = ColonyEventSource.SourceName;

    private readonly World _world;
    private readonly Meter _meter;
    private readonly Lock _lock = new();
    private readonly TickRecord[] _tick = new TickRecord[1];
    private readonly SystemRecord[] _systems;
    private readonly WorkerRecord[] _workers;
    private readonly long[] _counters;
    private readonly KeyValuePair<string, object?>[] _systemTags;
    private readonly KeyValuePair<string, object?>[] _workerTags;
    private readonly KeyValuePair<string, object?>[] _counterTags;
    private readonly string[] _systemNames;
    private readonly string[] _counterNames;
    private readonly bool _ownsEventSource;
    private long _ticksPublished;
    private long _lastTick = -1;
    private bool _disposed;

    public MeterBridge(World world, string meterName = DefaultMeterName)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrEmpty(meterName);
        RecordLayout.Verify(world, "Meter bridge");
        _world = world;
        var schema = world.Schema;
        var diagnostics = world.Diagnostics;
        _systems = new SystemRecord[diagnostics.SystemCount];
        _workers = new WorkerRecord[diagnostics.WorkerCount];
        _counters = new long[diagnostics.CounterCount];
        _systemNames = new string[_systems.Length];
        _systemTags = new KeyValuePair<string, object?>[_systems.Length];
        for (int s = 0; s < _systems.Length; s++)
        {
            _systemNames[s] = schema.Systems[s].Name;
            _systemTags[s] = new("system", _systemNames[s]);
        }

        _workerTags = new KeyValuePair<string, object?>[_workers.Length];
        for (int w = 0; w < _workers.Length; w++)
        {
            _workerTags[w] = new("worker", w.ToString(CultureInfo.InvariantCulture));
        }

        _counterNames = new string[_counters.Length];
        _counterTags = new KeyValuePair<string, object?>[_counters.Length];
        for (int c = 0; c < _counters.Length; c++)
        {
            _counterNames[c] = schema.Counters[c];
            _counterTags[c] = new("counter", _counterNames[c]);
        }

        _meter = new Meter(meterName);
        _meter.CreateObservableCounter("colony.ticks", () => Volatile.Read(ref _ticksPublished), "{tick}", "Ticks published to the bridge.");
        _meter.CreateObservableGauge("colony.tick.duration", () => Millis(Snapshot().WallTicks), "ms", "Wall time of the latest Tick.");
        _meter.CreateObservableGauge("colony.tick.playback", () => Millis(Snapshot().PlaybackTicks), "ms", "Command playback time of the latest Tick.");
        _meter.CreateObservableGauge("colony.tick.jobs", () => (long)Snapshot().Jobs, "{job}", "Chunk jobs run in the latest Tick.");
        _meter.CreateObservableGauge("colony.tick.chunks_visited", () => (long)Snapshot().ChunksVisited, "{chunk}");
        _meter.CreateObservableGauge("colony.tick.chunks_skipped", () => (long)Snapshot().ChunksSkipped, "{chunk}");
        _meter.CreateObservableGauge("colony.tick.entities_touched", () => Snapshot().EntitiesTouched, "{entity}");
        _meter.CreateObservableGauge("colony.tick.commands", () => Snapshot().Commands, "{command}");
        _meter.CreateObservableGauge("colony.tick.allocated_bytes", () => Snapshot().AllocatedBytes, "By", "Managed bytes allocated on Tick threads during the latest Tick (0 in steady state).");
        _meter.CreateObservableGauge("colony.world.entities", () => (long)Snapshot().EntityCount, "{entity}");
        _meter.CreateObservableGauge("colony.world.archetypes", () => (long)Snapshot().ArchetypeCount, "{archetype}");
        _meter.CreateObservableGauge("colony.world.chunks_in_use", () => (long)Snapshot().ChunksInUse, "{chunk}");
        _meter.CreateObservableGauge("colony.world.reserved_bytes", () => Snapshot().ReservedBytes, "By", "Native bytes reserved in slabs.");
        _meter.CreateObservableGauge("colony.world.scratch_bytes", () => Snapshot().ScratchUsedBytes, "By", "Scratch arena high-water mark of the latest Tick.");
        _meter.CreateObservableGauge("colony.system.duration", SystemDurations, "ms", "Wall time of each System in the latest Tick.");
        _meter.CreateObservableGauge("colony.system.cpu", SystemCpu, "ms", "Summed job time of each System in the latest Tick.");
        _meter.CreateObservableGauge("colony.system.jobs", SystemJobs, "{job}");
        _meter.CreateObservableGauge("colony.system.entities_touched", SystemEntities, "{entity}");
        _meter.CreateObservableGauge("colony.system.commands", SystemCommands, "{command}");
        _meter.CreateObservableGauge("colony.system.allocated_bytes", SystemAllocated, "By");
        _meter.CreateObservableGauge("colony.worker.busy", WorkerBusy, "ms", "Time each worker spent running jobs in the latest Tick.");
        _meter.CreateObservableGauge("colony.worker.spin", WorkerSpin, "ms");
        _meter.CreateObservableGauge("colony.worker.park", WorkerPark, "ms");
        _meter.CreateObservableGauge("colony.worker.jobs", WorkerJobs, "{job}");
        _meter.CreateObservableGauge("colony.counter", UserCounters, "{count}", "Registered counters of the latest Tick, by name.");

        _ownsEventSource = ColonyEventSource.Log.TryAttach(this);
    }

    /// <summary>Ticks published so far.</summary>
    public long TicksPublished => Volatile.Read(ref _ticksPublished);

    public string MeterName => _meter.Name;

    /// <summary>True when this bridge feeds the process-wide <see cref="EventSourceName"/> EventCounters (the first live bridge does).</summary>
    public bool PublishesEventCounters => _ownsEventSource;

    /// <summary>
    /// Snapshots the World's latest Tick record for the instruments and raises the <c>TickCompleted</c> event.
    /// Returns false when no Tick has run or the latest Tick was already published. Call between Ticks on the
    /// host thread; it allocates nothing.
    /// </summary>
    public bool Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var diagnostics = _world.Diagnostics;
        if (diagnostics.Count == 0)
        {
            return false;
        }

        var record = diagnostics.Latest;
        if (record.Tick.Tick == _lastTick)
        {
            return false;
        }

        RecordLayout.Verify(record.Tick.Version, "Meter bridge");
        lock (_lock)
        {
            _tick[0] = record.Tick;
            record.Systems.CopyTo(_systems);
            record.Workers.CopyTo(_workers);
            record.Counters.CopyTo(_counters);
        }

        _lastTick = record.Tick.Tick;
        Interlocked.Increment(ref _ticksPublished);
        ColonyEventSource.Log.TickCompleted(record.Tick.Tick, record.Tick.WallTicks, record.Tick.AllocatedBytes);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsEventSource)
        {
            ColonyEventSource.Log.Detach(this);
        }

        _meter.Dispose();
    }

    // ---- Snapshot readers; run on the collector's thread under the lock. ----

    internal TickRecord Snapshot()
    {
        lock (_lock)
        {
            return _tick[0];
        }
    }

    internal int SystemCount => _systems.Length;

    internal string SystemName(int system) => _systemNames[system];

    internal SystemRecord System(int system)
    {
        lock (_lock)
        {
            return _systems[system];
        }
    }

    internal static double Millis(long ticks) => ticks * 1000.0 / TickRecord.TicksPerSecond;

    private IEnumerable<Measurement<double>> SystemDurations() => SystemMeasurements(static (in SystemRecord s) => Millis(s.WallTicks));

    private IEnumerable<Measurement<double>> SystemCpu() => SystemMeasurements(static (in SystemRecord s) => Millis(s.CpuTicks));

    private IEnumerable<Measurement<long>> SystemJobs() => SystemMeasurements(static (in SystemRecord s) => (long)s.Jobs);

    private IEnumerable<Measurement<long>> SystemEntities() => SystemMeasurements(static (in SystemRecord s) => s.EntitiesTouched);

    private IEnumerable<Measurement<long>> SystemCommands() => SystemMeasurements(static (in SystemRecord s) => s.Commands);

    private IEnumerable<Measurement<long>> SystemAllocated() => SystemMeasurements(static (in SystemRecord s) => s.AllocatedBytes);

    private IEnumerable<Measurement<double>> WorkerBusy() => WorkerMeasurements(static (in WorkerRecord w) => Millis(w.BusyTicks));

    private IEnumerable<Measurement<double>> WorkerSpin() => WorkerMeasurements(static (in WorkerRecord w) => Millis(w.SpinTicks));

    private IEnumerable<Measurement<double>> WorkerPark() => WorkerMeasurements(static (in WorkerRecord w) => Millis(w.ParkTicks));

    private IEnumerable<Measurement<long>> WorkerJobs() => WorkerMeasurements(static (in WorkerRecord w) => (long)w.Jobs);

    private IEnumerable<Measurement<long>> UserCounters()
    {
        var values = new long[_counters.Length];
        lock (_lock)
        {
            _counters.CopyTo(values, 0);
        }

        for (int c = 0; c < values.Length; c++)
        {
            yield return new Measurement<long>(values[c], _counterTags[c]);
        }
    }

    private IEnumerable<Measurement<T>> SystemMeasurements<T>(SystemSelector<T> select)
        where T : struct
    {
        var copy = new SystemRecord[_systems.Length];
        lock (_lock)
        {
            _systems.CopyTo(copy, 0);
        }

        for (int s = 0; s < copy.Length; s++)
        {
            yield return new Measurement<T>(select(in copy[s]), _systemTags[s]);
        }
    }

    private IEnumerable<Measurement<T>> WorkerMeasurements<T>(WorkerSelector<T> select)
        where T : struct
    {
        var copy = new WorkerRecord[_workers.Length];
        lock (_lock)
        {
            _workers.CopyTo(copy, 0);
        }

        for (int w = 0; w < copy.Length; w++)
        {
            yield return new Measurement<T>(select(in copy[w]), _workerTags[w]);
        }
    }

    private delegate T SystemSelector<T>(in SystemRecord record);

    private delegate T WorkerSelector<T>(in WorkerRecord record);
}
