using System.Globalization;
using System.Text;

namespace Colony.Testing;

/// <summary>
/// One stretch of a Tick the allocation harness measured: a wave of Systems in a Phase (named), a Phase
/// boundary (command playback and extension hooks), the Tick start/end or the World hash. Bytes are
/// totals over every measured Tick.
/// </summary>
public sealed class AllocationSite
{
    internal AllocationSite(string label, Phase? phase, IReadOnlyList<string> systems)
    {
        Label = label;
        Phase = phase;
        Systems = systems;
    }

    public string Label { get; }

    /// <summary>The Phase the stretch belongs to, or null for the driver's own code between Ticks.</summary>
    public Phase? Phase { get; }

    /// <summary>The Systems that ran during the stretch: one name pins the System exactly; several share a wave.</summary>
    public IReadOnlyList<string> Systems { get; }

    /// <summary>Bytes the Tick thread allocated here (it also runs chunk jobs as worker 0).</summary>
    public long MainThreadBytes { get; internal set; }

    /// <summary>Bytes the dedicated worker threads allocated here.</summary>
    public long WorkerBytes { get; internal set; }

    public long TotalBytes => MainThreadBytes + WorkerBytes;
}

/// <summary>
/// One System the World's diagnostics allocation check caught allocating during the measured Ticks
/// (<see cref="Colony.Diagnostics.SystemRecord.AllocatedBytes"/>, summed): exact even when the System shares a wave.
/// </summary>
/// <param name="Name">The System name.</param>
/// <param name="Phase">The Phase it runs in.</param>
/// <param name="Bytes">Managed bytes it allocated, on any thread, over all measured Ticks.</param>
public sealed record SystemAllocation(string Name, Phase Phase, long Bytes);

/// <summary>What <see cref="AllocationHarness"/> measured over the steady-state Ticks of one scenario.</summary>
public sealed class AllocationResult
{
    internal AllocationResult(string scenarioName, int workers, int measuredTicks, long tolerance, IReadOnlyList<AllocationSite> sites, long diagnosticsBytes, IReadOnlyList<SystemAllocation> systems)
    {
        ScenarioName = scenarioName;
        Workers = workers;
        MeasuredTicks = measuredTicks;
        ToleranceBytes = tolerance;
        Sites = sites;
        DiagnosticsBytes = diagnosticsBytes;
        Systems = systems;
        foreach (var site in sites)
        {
            MainThreadBytes += site.MainThreadBytes;
            WorkerBytes += site.WorkerBytes;
            FirstOffender ??= site.TotalBytes > 0 ? site : null;
        }
    }

    public string ScenarioName { get; }

    public int Workers { get; }

    public int MeasuredTicks { get; }

    public long ToleranceBytes { get; }

    /// <summary>Every measured stretch of the Tick in execution order.</summary>
    public IReadOnlyList<AllocationSite> Sites { get; }

    /// <summary>Bytes the Tick thread allocated over all measured Ticks, including the harness's own loop.</summary>
    public long MainThreadBytes { get; }

    /// <summary>Bytes the worker threads allocated over all measured Ticks.</summary>
    public long WorkerBytes { get; }

    public long TotalBytes => MainThreadBytes + WorkerBytes;

    /// <summary>The first stretch, in Tick order, that allocated anything.</summary>
    public AllocationSite? FirstOffender { get; }

    /// <summary>
    /// Bytes the World's diagnostics allocation check recorded inside the measured Ticks (Systems and Tick-thread
    /// code outside them; not the harness loop or <see cref="World.Hash"/>). A second, independent count of
    /// <see cref="TotalBytes"/>.
    /// </summary>
    public long DiagnosticsBytes { get; }

    /// <summary>Systems the diagnostics allocation check caught allocating, in System id order.</summary>
    public IReadOnlyList<SystemAllocation> Systems { get; }

    /// <summary>Both counts must stay within the tolerance.</summary>
    public bool Passed => TotalBytes <= ToleranceBytes && DiagnosticsBytes <= ToleranceBytes;

    public void ThrowIfFailed()
    {
        if (!Passed)
        {
            throw new HarnessException(Describe());
        }
    }

    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Scenario '{ScenarioName}' on {Workers} workers over {MeasuredTicks} steady-state Ticks: {(Passed ? "PASSED" : "FAILED")} ({MainThreadBytes} bytes on the Tick thread, {WorkerBytes} on workers, {DiagnosticsBytes} seen by the diagnostics allocation check, tolerance {ToleranceBytes}).");
        foreach (var system in Systems)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  System {system.Name} (Phase {system.Phase.Name}) allocated {system.Bytes} bytes");
        }

        if (FirstOffender is { } offender)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  First allocation: {offender.Label} ({offender.MainThreadBytes} main / {offender.WorkerBytes} worker bytes)");
        }

        foreach (var site in Sites)
        {
            if (site.TotalBytes > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {site.TotalBytes,10} bytes  {site.Label}");
            }
        }

        return text.ToString();
    }
}
