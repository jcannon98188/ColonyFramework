using System.Globalization;
using System.Text;

namespace Colony.Testing;

/// <summary>
/// Two peers disagreed about a checkpoint hash (spec section 7.4): who noticed, at which checkpoint, and,
/// from the peers' hash trails, the first diverging Tick, Phase and System. <see cref="Phase"/> and
/// <see cref="System"/> are null when the trail was off; <see cref="System"/> alone is null when the Phase's
/// Systems all agreed and only its boundary (command playback) differed.
/// </summary>
public sealed record LockstepDesync(
    int DetectedBy,
    int Peer,
    long DetectedAtTick,
    long DetectedAtStep,
    long Tick,
    int PhaseId,
    string? Phase,
    int SystemId,
    string? System)
{
    public string Describe()
    {
        var where = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Tick {Tick}"));
        if (Phase is not null)
        {
            where.Append(", Phase ").Append(Phase).Append(", System ").Append(System ?? "(Phase boundary)");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"desync between peer {DetectedBy} and peer {Peer}, detected at the Tick {DetectedAtTick} checkpoint (step {DetectedAtStep}); first diverging at {where}");
    }
}

/// <summary>One peer's side of a <see cref="LockstepResult"/>.</summary>
public sealed record LockstepPeerResult(int Peer, int Workers, long TicksRun, long StallSteps, int ProceededWithoutFrames, IReadOnlyList<ulong> Hashes);

/// <summary>
/// The outcome of a <see cref="LockstepHarness"/> run. <see cref="Passed"/> means every peer ran every Tick, no
/// desync was detected and every peer's per-Tick World hash agrees; stalls are allowed.
/// </summary>
public sealed class LockstepResult
{
    internal LockstepResult(
        string scenario,
        LockstepOptions options,
        long steps,
        bool completed,
        LockstepDesync? desync,
        IReadOnlyList<LockstepPeerResult> peers,
        NetworkCounters network,
        int faultsApplied,
        Exception? error)
    {
        Scenario = scenario;
        Options = options;
        Steps = steps;
        Completed = completed;
        Desync = desync;
        Peers = peers;
        Network = network;
        FaultsApplied = faultsApplied;
        Error = error;
        FirstDivergingTick = FindFirstDivergingTick(peers);
    }

    public string Scenario { get; }

    public LockstepOptions Options { get; }

    /// <summary>Network steps the run took.</summary>
    public long Steps { get; }

    /// <summary>Every peer ran every Tick and holds every other peer's checkpoints.</summary>
    public bool Completed { get; }

    /// <summary>The run hit <see cref="LockstepOptions.MaxSteps"/> with neither completion, a desync nor an error.</summary>
    public bool Stalled => !Completed && Desync is null && Error is null;

    public LockstepDesync? Desync { get; }

    public IReadOnlyList<LockstepPeerResult> Peers { get; }

    public NetworkCounters Network { get; }

    /// <summary>Injected <see cref="LockstepFault"/>s that hit a frame.</summary>
    public int FaultsApplied { get; }

    /// <summary>A System fault or other exception on a peer, which ends the run.</summary>
    public Exception? Error { get; }

    /// <summary>The first Tick at which some peer's World hash differs from peer 0's, over the Ticks both ran.</summary>
    public long? FirstDivergingTick { get; }

    public bool Passed => Completed && Desync is null && Error is null && FirstDivergingTick is null;

    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"lockstep '{Scenario}': {Peers.Count} peers, {Options.Ticks} Ticks, network '{Options.Profile.Name}' seed 0x{Options.NetworkSeed:x}, {Steps} steps: ");
        if (Passed)
        {
            text.Append("passed");
        }
        else if (Error is not null)
        {
            text.Append("failed: ").Append(Error.GetType().Name).Append(": ").Append(Error.Message);
        }
        else if (Desync is not null)
        {
            text.Append("failed: ").Append(Desync.Describe());
        }
        else if (Stalled)
        {
            text.Append("stalled after ").Append(Steps.ToString(CultureInfo.InvariantCulture)).Append(" steps");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"failed: hashes diverge at Tick {FirstDivergingTick}");
        }

        text.AppendLine();
        text.Append(CultureInfo.InvariantCulture, $"  network: sent {Network.Sent}, delivered {Network.Delivered}, dropped {Network.Dropped}, duplicated {Network.Duplicated}, reordered {Network.Reordered}; faults applied {FaultsApplied}");
        foreach (var peer in Peers)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"  peer {peer.Peer} ({peer.Workers} workers): {peer.TicksRun} Ticks, {peer.StallSteps} stall steps, {peer.ProceededWithoutFrames} Ticks run without frames");
        }

        return text.ToString();
    }

    public void ThrowIfFailed()
    {
        if (!Passed)
        {
            throw new HarnessException(Describe());
        }
    }

    private static long? FindFirstDivergingTick(IReadOnlyList<LockstepPeerResult> peers)
    {
        long? first = null;
        for (int p = 1; p < peers.Count; p++)
        {
            var a = peers[0].Hashes;
            var b = peers[p].Hashes;
            int common = Math.Min(a.Count, b.Count);
            for (int t = 0; t < common; t++)
            {
                if (a[t] != b[t])
                {
                    if (first is null || t < first)
                    {
                        first = t;
                    }

                    break;
                }
            }
        }

        return first;
    }
}
