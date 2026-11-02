namespace Colony.Persistence;

/// <summary>How a <see cref="ReplayRecorder"/> records (spec section 7.2).</summary>
public sealed class ReplayOptions
{
    public static ReplayOptions Default { get; } = new();

    /// <summary>Record the World hash every N Ticks (on Ticks divisible by N); the start and end of the log always carry one.</summary>
    public int CheckpointEvery { get; init; } = 10;

    /// <summary>Embed a full save every N Ticks (on Ticks divisible by N) so playback can seek to it; 0 embeds none besides the start.</summary>
    public int FullSaveEvery { get; init; }

    /// <summary>Embed a full save at the start Tick, so playback can start from the log alone. Off, the host must position the World itself.</summary>
    public bool EmbedStartSave { get; init; } = true;

    /// <summary>Ticks per second the host runs at; stored in the header and compared by <see cref="ReplayPlayer"/> when the player is given the host's identity.</summary>
    public uint TickRate { get; init; }

    /// <summary>The Game build (version, commit, platform tag) stored in the header, as in <see cref="PeerIdentityOptions.Build"/>.</summary>
    public string Build { get; init; } = "";

    /// <summary>Container options for the embedded saves and the log file.</summary>
    public SaveOptions SaveOptions { get; init; } = SaveOptions.Default;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(CheckpointEvery, 1, nameof(CheckpointEvery));
        ArgumentOutOfRangeException.ThrowIfNegative(FullSaveEvery, nameof(FullSaveEvery));
        ArgumentNullException.ThrowIfNull(Build, nameof(Build));
    }
}

/// <summary>Playback reached a checkpoint whose recorded World hash differs from the World's (spec section 7.2): the inputs, build or Plugin set differ from the recording.</summary>
public sealed class ReplayDivergenceException : InvalidOperationException
{
    public ReplayDivergenceException()
    {
    }

    public ReplayDivergenceException(string message)
        : base(message)
    {
    }

    public ReplayDivergenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ReplayDivergenceException(long tick, ulong expected, ulong actual)
        : base($"Replay diverged at Tick {tick}: the log recorded World hash 0x{expected:X16} but the World hashes to 0x{actual:X16}.")
    {
        Tick = tick;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The checkpoint Tick (the World's <see cref="World.CurrentTick"/> when the mismatch was found).</summary>
    public long Tick { get; }

    public ulong Expected { get; }

    public ulong Actual { get; }
}
