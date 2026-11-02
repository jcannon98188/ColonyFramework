namespace Colony.Persistence;

/// <summary>
/// Plays a <see cref="ReplayLog"/> into a World (spec section 7.2): before each Tick it submits the Tick's
/// recorded input frame, after it compares the World hash with the recorded checkpoint when there is one and
/// throws <see cref="ReplayDivergenceException"/> on a mismatch. Attach it to a World positioned at a Tick of
/// the log (<see cref="ReplayLog.CreateWorld"/> loads an embedded save), <see cref="SeekTo"/> a Tick, and
/// <see cref="Fork"/> to continue with live input while recording. Do not submit live input while playing.
/// Attaching compares the recording's identity with the World's (spec section 7.2, through the lockstep
/// <see cref="PeerHandshake"/>): Framework and format versions, Plugin set (ids, versions, determinism, ABI,
/// order), schema hash, seed, the extensions' content versions (the generation version), and the build, tick
/// rate and settings when the host's <see cref="PeerIdentityOptions"/> are given. A log recorded with a
/// non-deterministic Plugin is refused (spec section 6: it carries no guarantee).
/// </summary>
public sealed class ReplayPlayer
{
    private readonly int[] _typeMap;

    /// <param name="log">The recording.</param>
    /// <param name="world">A World positioned at a Tick of the log.</param>
    /// <param name="host">The playing host's build, tick rate and settings; null takes the recording's (only the World's own identity is compared).</param>
    public ReplayPlayer(ReplayLog log, World world, PeerIdentityOptions? host = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(world);
        Log = log;
        World = world;
        long tick = world.CurrentTick;
        if (tick < log.StartTick || tick > log.EndTick)
        {
            throw new ArgumentException($"The World is at Tick {tick}; the replay covers Ticks {log.StartTick} to {log.EndTick}.", nameof(world));
        }

        _typeMap = new int[log.InputCommands.Length];
        for (int i = 0; i < _typeMap.Length; i++)
        {
            var saved = log.InputCommands[i];
            if (!world.Schema.TryGetInputCommand(saved.Name, out var type))
            {
                throw new InvalidOperationException($"The replay uses Input Command '{saved.Name}', which the schema does not register.");
            }

            var current = world.Schema.DescribeInputCommand(type);
            if (current.LayoutHash != saved.LayoutHash || current.Size != saved.Size)
            {
                throw new InvalidOperationException($"Input Command '{saved.Name}' was recorded with layout 0x{saved.LayoutHash:X16} ({saved.Size} bytes) but the schema has 0x{current.LayoutHash:X16} ({current.Size} bytes).");
            }

            _typeMap[i] = type.Id;
        }

        // Permissions decide which recorded commands reach handlers, so a different table would diverge (TASK-35).
        var permissions = SavedInputPermissions.Of(world.Schema);
        var differences = log.InputPermissions.Differences(permissions);
        if (!differences.IsEmpty)
        {
            throw new InvalidOperationException(
                "The replay was recorded with different Input permissions than the schema declares. Recorded: "
                + string.Join("; ", log.InputPermissions.Describe()) + ". Schema: " + string.Join("; ", permissions.Describe())
                + ". Differences (here = recording): " + string.Join("; ", differences) + ".");
        }

        // The precise Input checks above speak first; the identity then covers everything else that would diverge.
        VerifyIdentity(log, world, host);
        Verify();
    }

    public ReplayLog Log { get; }

    public World World { get; }

    /// <summary>Checkpoints compared so far, the attach Tick's included.</summary>
    public int VerifiedCheckpoints { get; private set; }

    /// <summary>True once the World reached the log's end Tick.</summary>
    public bool IsFinished => World.CurrentTick >= Log.EndTick;

    /// <summary>
    /// Submits the current Tick's recorded frame and runs the Tick, then verifies the checkpoint if the new Tick
    /// has one. False when the Tick gate held the Tick (the frame is submitted once; it stays pending). Throws
    /// <see cref="ReplayDivergenceException"/> on a hash mismatch and <see cref="InvalidOperationException"/> past the end.
    /// </summary>
    public bool Tick()
    {
        long tick = World.CurrentTick;
        if (tick >= Log.EndTick)
        {
            throw new InvalidOperationException($"The replay ended at Tick {Log.EndTick}; Fork to continue with live input.");
        }

        if (tick > _submittedFor && Log.TryGetFrame(tick, out var frame))
        {
            InputFrame.Submit(World, frame.Bytes, tick, _typeMap);
        }

        _submittedFor = tick;
        if (!World.Tick())
        {
            return false;
        }

        Verify();
        return true;
    }

    private long _submittedFor = long.MinValue;

    /// <summary>Plays forward to <paramref name="tick"/> (at or after the current Tick, at or before the end), verifying every checkpoint on the way.</summary>
    public void SeekTo(long tick)
    {
        if (tick < World.CurrentTick)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"The World is already at Tick {World.CurrentTick}; load an earlier full save with ReplayLog.CreateWorld to seek backwards.");
        }

        if (tick > Log.EndTick)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"The replay ends at Tick {Log.EndTick}.");
        }

        while (World.CurrentTick < tick)
        {
            if (!Tick())
            {
                throw new InvalidOperationException("The Tick gate held the Tick during a seek.");
            }
        }
    }

    public void RunToEnd() => SeekTo(Log.EndTick);

    /// <summary>
    /// Stops playing and returns a recorder that continues from the current Tick with live input: its log
    /// holds the recording up to here (frames, checkpoints and saves), so the result is a complete replay from the original start.
    /// </summary>
    public ReplayRecorder Fork(ReplayOptions? options = null)
        => new(World, options, Log.Prefix(World.CurrentTick));

    private static void VerifyIdentity(ReplayLog log, World world, PeerIdentityOptions? host)
    {
        var recorded = log.Identity;
        host ??= new PeerIdentityOptions { Build = recorded.Build, TickRate = recorded.TickRate, Settings = recorded.Settings };
        var here = PeerIdentity.Of(world, host);
        var problems = new List<string>();
        foreach (var mismatch in PeerHandshake.Compare(here, recorded).Mismatches)
        {
            // Both sides report a non-deterministic Plugin; say it once, about the recording.
            if (mismatch.Kind != HandshakeMismatchKind.NotDeterministic)
            {
                problems.Add(mismatch.Message);
            }
            else if (mismatch.Subject == "remote")
            {
                problems.Add($"The replay was recorded with non-deterministic Plugin(s) {mismatch.Remote}, so its checkpoints carry no guarantee.");
            }
        }

        var current = ReplayData.ExtensionVersionsOf(world.Schema);
        foreach (var saved in log.ExtensionVersions)
        {
            string? now = current.Find(c => c.Name == saved.Name).Version;
            if (now != saved.Version)
            {
                problems.Add($"Extension '{saved.Name}' was recorded at content version {saved.Version} but the schema has {now ?? "none"}.");
            }
        }

        foreach (var c in current)
        {
            if (!log.ExtensionVersions.Any(s => s.Name == c.Name))
            {
                problems.Add($"Extension '{c.Name}' has content version {c.Version}, which the recording does not have.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The replay cannot be played on this World (local = this World, remote = the recording): " + string.Join(" ", problems));
        }
    }

    private void Verify()
    {
        long tick = World.CurrentTick;
        if (Log.TryGetCheckpoint(tick, out ulong expected))
        {
            ulong actual = World.Hash();
            VerifiedCheckpoints++;
            if (actual != expected)
            {
                throw new ReplayDivergenceException(tick, expected, actual);
            }
        }
    }
}
