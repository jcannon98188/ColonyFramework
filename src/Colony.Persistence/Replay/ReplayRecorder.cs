namespace Colony.Persistence;

/// <summary>
/// Records a replay log (spec section 7.2) while a World runs: after every Tick it stores the canonical
/// input list the Tick consumed (<see cref="WorldInputs.Current"/>), a checkpoint World hash every
/// <see cref="ReplayOptions.CheckpointEvery"/> Ticks and a full save every <see cref="ReplayOptions.FullSaveEvery"/>.
/// Drive the World through <see cref="Tick"/>, or call <see cref="RecordTick"/> right after your own
/// <see cref="World.Tick"/>. Recording touches the World only through its public readers; nothing it does changes the simulation.
/// </summary>
public sealed class ReplayRecorder
{
    private readonly ReplayData _data;
    private long _recordedThrough;

    /// <summary>Starts recording <paramref name="world"/> at its current Tick: the start checkpoint (and the start save, by default) are taken now.</summary>
    public ReplayRecorder(World world, ReplayOptions? options = null)
        : this(world, options, null)
    {
    }

    internal ReplayRecorder(World world, ReplayOptions? options, ReplayData? prefix)
    {
        ArgumentNullException.ThrowIfNull(world);
        options ??= ReplayOptions.Default;
        options.Validate();
        World = world;
        Options = options;
        long tick = world.CurrentTick;
        if (prefix is not null)
        {
            _data = prefix;
            _data.EndTick = tick;
        }
        else
        {
            _data = new ReplayData { Seed = world.Seed, SchemaHash = world.Schema.SchemaHash, StartTick = tick, EndTick = tick, TickRate = options.TickRate };
            foreach (ref readonly var input in world.Schema.InputCommands)
            {
                _data.Inputs.Add(new SavedInputCommand(input.Name, input.Size, input.LayoutHash));
            }
        }

        _recordedThrough = tick - 1;
        if (!_data.Checkpoints.Exists(c => c.Tick == tick))
        {
            ReplayData.Put(_data.Checkpoints, tick, world.Hash());
        }

        if (options.EmbedStartSave && !_data.Saves.Exists(s => s.Tick == tick))
        {
            ReplayData.Put(_data.Saves, tick, world.SaveToBytes(options.SaveOptions));
        }
    }

    public World World { get; }

    public ReplayOptions Options { get; }

    public long StartTick => _data.StartTick;

    /// <summary>The last Tick recorded so far (the World's current Tick after the last <see cref="RecordTick"/>).</summary>
    public long EndTick => _data.EndTick;

    public int FrameCount => _data.Frames.Count;

    /// <summary>Runs one Tick of the World and records it. False when the Tick gate held the Tick (nothing recorded).</summary>
    public bool Tick()
    {
        if (!World.Tick())
        {
            return false;
        }

        RecordTick();
        return true;
    }

    /// <summary>
    /// Records the Tick that just completed: its canonical input list (when non-empty), the checkpoint hash and
    /// the full save when due. Call it once after every <see cref="World.Tick"/> that returned true; a repeated
    /// call for the same Tick records nothing more.
    /// </summary>
    public void RecordTick()
    {
        long tick = World.CurrentTick;
        long consumed = tick - 1;
        if (consumed <= _recordedThrough)
        {
            return;
        }

        var list = World.Inputs.Current;
        if (list.Tick == consumed && list.Count > 0)
        {
            ReplayData.Put(_data.Frames, consumed, InputFrame.Encode(in list));
        }

        _recordedThrough = consumed;
        _data.EndTick = tick;
        if (tick % Options.CheckpointEvery == 0)
        {
            ReplayData.Put(_data.Checkpoints, tick, World.Hash());
        }

        if (Options.FullSaveEvery > 0 && tick % Options.FullSaveEvery == 0)
        {
            ReplayData.Put(_data.Saves, tick, World.SaveToBytes(Options.SaveOptions));
        }
    }

    /// <summary>The log so far, ending at the World's current Tick with a checkpoint there. Recording may continue afterwards.</summary>
    public ReplayLog Finish()
    {
        long tick = World.CurrentTick;
        if (tick != _data.EndTick)
        {
            throw new InvalidOperationException($"The World is at Tick {tick} but the recorder last recorded Tick {_data.EndTick}; call RecordTick after every Tick.");
        }

        if (!_data.Checkpoints.Exists(c => c.Tick == tick))
        {
            ReplayData.Put(_data.Checkpoints, tick, World.Hash());
        }

        return new ReplayLog(_data.Copy());
    }

    public byte[] Encode() => Finish().Encode(Options.SaveOptions);

    public void Write(Stream destination) => Finish().Write(destination, Options.SaveOptions);

    public void WriteFile(string path) => Finish().WriteFile(path, Options.SaveOptions);
}
