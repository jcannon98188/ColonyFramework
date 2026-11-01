namespace Colony.Persistence;

/// <summary>
/// Plays a <see cref="ReplayLog"/> into a World (spec section 7.2): before each Tick it submits the Tick's
/// recorded input frame, after it compares the World hash with the recorded checkpoint when there is one and
/// throws <see cref="ReplayDivergenceException"/> on a mismatch. Attach it to a World positioned at a Tick of
/// the log (<see cref="ReplayLog.CreateWorld"/> loads an embedded save), <see cref="SeekTo"/> a Tick, and
/// <see cref="Fork"/> to continue with live input while recording. Do not submit live input while playing.
/// </summary>
public sealed class ReplayPlayer
{
    private readonly int[] _typeMap;

    public ReplayPlayer(ReplayLog log, World world)
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
