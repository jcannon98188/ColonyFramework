using System.Collections.Immutable;
using System.Globalization;

namespace Colony.Persistence;

/// <summary>An Input Command as the replay header lists it: the registered name, size and layout hash, in the recording schema's id order.</summary>
public readonly record struct SavedInputCommand(string Name, int Size, ulong LayoutHash);

/// <summary>
/// The mutable contents of a replay log shared by the recorder, the log and the builder: header fields,
/// frames (ascending Tick, one per non-empty Tick), checkpoint hashes (ascending Tick) and embedded full
/// saves (ascending Tick).
/// </summary>
internal sealed class ReplayData
{
    public ulong Seed { get; set; }

    public ulong SchemaHash { get; set; }

    public long StartTick { get; set; }

    public long EndTick { get; set; }

    public uint TickRate { get; set; }

    public List<SavedInputCommand> Inputs { get; } = [];

    public List<string> Plugins { get; } = [];

    public SavedInputPermissions InputPermissions { get; set; } = SavedInputPermissions.Unrestricted;

    public List<(long Tick, byte[] Frame)> Frames { get; } = [];

    public List<(long Tick, ulong Hash)> Checkpoints { get; } = [];

    public List<(long Tick, byte[] Save)> Saves { get; } = [];

    public ReplayData Copy(long? upToTick = null)
    {
        var copy = new ReplayData { Seed = Seed, SchemaHash = SchemaHash, StartTick = StartTick, EndTick = upToTick ?? EndTick, TickRate = TickRate, InputPermissions = InputPermissions };
        copy.Inputs.AddRange(Inputs);
        copy.Plugins.AddRange(Plugins);
        foreach (var f in Frames)
        {
            // The frame of Tick t is applied by Tick t, so a log truncated at T holds frames below T.
            if (upToTick is null || f.Tick < upToTick)
            {
                copy.Frames.Add((f.Tick, (byte[])f.Frame.Clone()));
            }
        }

        foreach (var c in Checkpoints)
        {
            if (upToTick is null || c.Tick <= upToTick)
            {
                copy.Checkpoints.Add(c);
            }
        }

        foreach (var s in Saves)
        {
            if (upToTick is null || s.Tick <= upToTick)
            {
                copy.Saves.Add((s.Tick, (byte[])s.Save.Clone()));
            }
        }

        return copy;
    }

    public static int IndexOfTick<T>(List<(long Tick, T Value)> list, long tick)
    {
        int lo = 0;
        int hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long t = list[mid].Tick;
            if (t == tick)
            {
                return mid;
            }

            if (t < tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    /// <summary>Inserts or replaces the entry for <paramref name="tick"/>, keeping the list sorted.</summary>
    public static void Put<T>(List<(long Tick, T Value)> list, long tick, T value)
    {
        int index = IndexOfTick(list, tick);
        if (index >= 0)
        {
            list[index] = (tick, value);
        }
        else
        {
            list.Insert(~index, (tick, value));
        }
    }

    public static bool TryGet<T>(List<(long Tick, T Value)> list, long tick, out T value)
    {
        int index = IndexOfTick(list, tick);
        if (index >= 0)
        {
            value = list[index].Value;
            return true;
        }

        value = default!;
        return false;
    }

    public byte[] Encode(SaveOptions options)
    {
        var sections = new List<ContainerSection>();
        {
            var w = new ByteWriter(256);
            w.WriteInt32(ReplayLog.Version);
            w.WriteUInt64(Seed);
            w.WriteUInt64(SchemaHash);
            w.WriteInt64(StartTick);
            w.WriteInt64(EndTick);
            w.WriteUInt32(TickRate);
            w.WriteInt32(Inputs.Count);
            foreach (var input in Inputs)
            {
                w.WriteString(input.Name);
                w.WriteInt32(input.Size);
                w.WriteUInt64(input.LayoutHash);
            }

            w.WriteInt32(Plugins.Count);
            foreach (var plugin in Plugins)
            {
                w.WriteString(plugin);
            }

            // The Input permission table (TASK-35) trails the header only when one was declared, so unrestricted
            // logs encode exactly as before.
            if (!InputPermissions.IsUnrestricted)
            {
                InputPermissions.Write(w);
            }

            sections.Add(new ContainerSection(ReplayLog.HeaderSection, w.ToArray()));
        }

        {
            var w = new ByteWriter(1024);
            w.WriteInt32(Frames.Count);
            foreach (var (tick, frame) in Frames)
            {
                w.WriteInt64(tick);
                w.WriteInt32(frame.Length);
                w.WriteBytes(frame);
            }

            sections.Add(new ContainerSection(ReplayLog.FramesSection, w.ToArray()));
        }

        {
            var w = new ByteWriter(16 + (Checkpoints.Count * 16));
            w.WriteInt32(Checkpoints.Count);
            foreach (var (tick, hash) in Checkpoints)
            {
                w.WriteInt64(tick);
                w.WriteUInt64(hash);
            }

            sections.Add(new ContainerSection(ReplayLog.CheckpointsSection, w.ToArray()));
        }

        foreach (var (tick, save) in Saves)
        {
            sections.Add(new ContainerSection(ReplayLog.SavePrefix + tick.ToString(CultureInfo.InvariantCulture), save));
        }

        return SaveContainer.Encode(sections, options, SaveContainer.ReplayFlag);
    }

    public static ReplayData Decode(SaveContainer.Reader reader)
    {
        if (!reader.Has(ReplayLog.HeaderSection))
        {
            throw new SaveFormatException("The container is not a replay log: it has no 'replay' section.");
        }

        var data = new ReplayData();
        {
            var r = new ByteReader(reader.Section(ReplayLog.HeaderSection), ReplayLog.HeaderSection);
            int version = r.ReadInt32();
            if (version != ReplayLog.Version)
            {
                throw new SaveFormatException($"Replay log version {version} is not supported by this build (version {ReplayLog.Version}).");
            }

            data.Seed = r.ReadUInt64();
            data.SchemaHash = r.ReadUInt64();
            data.StartTick = r.ReadInt64();
            data.EndTick = r.ReadInt64();
            data.TickRate = r.ReadUInt32();
            int inputs = r.ReadCount(1);
            for (int i = 0; i < inputs; i++)
            {
                string name = r.ReadString();
                int size = r.ReadInt32();
                data.Inputs.Add(new SavedInputCommand(name, size, r.ReadUInt64()));
            }

            int plugins = r.ReadCount(1);
            for (int i = 0; i < plugins; i++)
            {
                data.Plugins.Add(r.ReadString());
            }

            if (r.Remaining > 0)
            {
                data.InputPermissions = SavedInputPermissions.Read(ref r);
            }

            r.ExpectEnd();
        }

        {
            var r = new ByteReader(reader.Section(ReplayLog.FramesSection), ReplayLog.FramesSection);
            int count = r.ReadCount(12);
            long previous = long.MinValue;
            for (int i = 0; i < count; i++)
            {
                long tick = r.ReadInt64();
                int length = r.ReadInt32();
                var frame = r.ReadBytes(length).ToArray();
                if (tick <= previous)
                {
                    throw new SaveFormatException($"The replay's frames are not in ascending Tick order (Tick {tick} after {previous}).");
                }

                _ = new InputFrameView(frame);
                data.Frames.Add((tick, frame));
                previous = tick;
            }

            r.ExpectEnd();
        }

        {
            var r = new ByteReader(reader.Section(ReplayLog.CheckpointsSection), ReplayLog.CheckpointsSection);
            int count = r.ReadCount(16);
            long previous = long.MinValue;
            for (int i = 0; i < count; i++)
            {
                long tick = r.ReadInt64();
                ulong hash = r.ReadUInt64();
                if (tick <= previous)
                {
                    throw new SaveFormatException($"The replay's checkpoints are not in ascending Tick order (Tick {tick} after {previous}).");
                }

                data.Checkpoints.Add((tick, hash));
                previous = tick;
            }

            r.ExpectEnd();
        }

        foreach (string name in reader.NamesWithPrefix(ReplayLog.SavePrefix))
        {
            if (!long.TryParse(name.AsSpan(ReplayLog.SavePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long tick))
            {
                throw new SaveFormatException($"Replay section '{name}' does not name a Tick.");
            }

            Put(data.Saves, tick, reader.Section(name));
        }

        return data;
    }
}

/// <summary>
/// A recorded replay (spec section 7.2): header (seed, schema hash, Input Command table, Tick range, tick
/// rate, Plugin set), the canonical input frame of every non-empty Tick, periodic checkpoint World hashes
/// and optional embedded full saves. Stored in the save container with the <c>replay</c>, <c>frames</c>,
/// <c>checkpoints</c> and <c>save/&lt;tick&gt;</c> sections. Produced by <see cref="ReplayRecorder.Finish"/>
/// or <see cref="Open(ReadOnlyMemory{byte})"/>; played by <see cref="ReplayPlayer"/>. Immutable; edit a copy through <see cref="ToBuilder"/>.
/// </summary>
public sealed class ReplayLog
{
    public const int Version = 1;
    internal const string HeaderSection = "replay";
    internal const string FramesSection = "frames";
    internal const string CheckpointsSection = "checkpoints";
    internal const string SavePrefix = "save/";

    private readonly ReplayData _data;
    private readonly long[] _frameTicks;
    private readonly long[] _checkpointTicks;
    private readonly long[] _saveTicks;

    internal ReplayLog(ReplayData data)
    {
        _data = data;
        _frameTicks = data.Frames.Select(static f => f.Tick).ToArray();
        _checkpointTicks = data.Checkpoints.Select(static c => c.Tick).ToArray();
        _saveTicks = data.Saves.Select(static s => s.Tick).ToArray();
        InputCommands = [.. data.Inputs];
        Plugins = [.. data.Plugins];
    }

    public static ReplayLog Open(ReadOnlyMemory<byte> image) => new(ReplayData.Decode(new SaveContainer.Reader(image)));

    public static ReplayLog Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Open(File.ReadAllBytes(path));
    }

    public static ReplayLog Open(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        return Open(memory.ToArray());
    }

    /// <summary>The World seed of the recording.</summary>
    public ulong Seed => _data.Seed;

    /// <summary>The recording schema's hash (Components, Resources, extensions and Input Commands).</summary>
    public ulong SchemaHash => _data.SchemaHash;

    /// <summary>The Tick the recording started at (its World hash is the first checkpoint).</summary>
    public long StartTick => _data.StartTick;

    /// <summary>The Tick the recording ended at (its World hash is the last checkpoint).</summary>
    public long EndTick => _data.EndTick;

    public uint TickRate => _data.TickRate;

    public ImmutableArray<SavedInputCommand> InputCommands { get; }

    /// <summary>The Plugin set of the recording; empty until the Plugins package exists.</summary>
    public ImmutableArray<string> Plugins { get; }

    /// <summary>The Input Command permission table of the recording schema (TASK-35); denied commands stay in the frames.</summary>
    public SavedInputPermissions InputPermissions => _data.InputPermissions;

    public int FrameCount => _frameTicks.Length;

    /// <summary>Ticks that had input, ascending.</summary>
    public ReadOnlySpan<long> FrameTicks => _frameTicks;

    public bool TryGetFrame(long tick, out InputFrameView frame)
    {
        if (ReplayData.TryGet(_data.Frames, tick, out var bytes))
        {
            frame = new InputFrameView(bytes);
            return true;
        }

        frame = default;
        return false;
    }

    /// <summary>The canonical input frame of <paramref name="tick"/>; an empty frame when the Tick had no input.</summary>
    public InputFrameView Frame(long tick) => TryGetFrame(tick, out var frame) ? frame : new InputFrameView([0, 0, 0, 0]);

    /// <summary>Ticks that carry a checkpoint hash, ascending.</summary>
    public ReadOnlySpan<long> CheckpointTicks => _checkpointTicks;

    public bool TryGetCheckpoint(long tick, out ulong hash) => ReplayData.TryGet(_data.Checkpoints, tick, out hash);

    /// <summary>Ticks that carry an embedded full save, ascending.</summary>
    public ReadOnlySpan<long> FullSaveTicks => _saveTicks;

    /// <summary>The latest embedded full save at or before <paramref name="tick"/>, or null.</summary>
    public long? LatestFullSaveAtOrBefore(long tick)
    {
        long? best = null;
        foreach (long t in _saveTicks)
        {
            if (t <= tick)
            {
                best = t;
            }
        }

        return best;
    }

    /// <summary>
    /// Builds a World positioned at the latest embedded full save at or before <paramref name="tick"/> (the seek
    /// entry point, spec section 7.2); attach a <see cref="ReplayPlayer"/> and <see cref="ReplayPlayer.SeekTo"/> the exact Tick.
    /// Throws when the log embeds no save that early.
    /// </summary>
    public World CreateWorld(long tick, WorldSchema schema, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (LatestFullSaveAtOrBefore(tick) is not { } at)
        {
            throw new InvalidOperationException($"The replay embeds no full save at or before Tick {tick}; record with ReplayOptions.EmbedStartSave or FullSaveEvery, or position a World yourself.");
        }

        ReplayData.TryGet(_data.Saves, at, out var save);
        return WorldPersistence.Load(save, schema, options);
    }

    /// <summary>The embedded save at exactly <paramref name="tick"/>, as container bytes.</summary>
    public bool TryGetFullSave(long tick, out ReadOnlyMemory<byte> save)
    {
        if (ReplayData.TryGet(_data.Saves, tick, out var bytes))
        {
            save = bytes;
            return true;
        }

        save = default;
        return false;
    }

    public byte[] Encode(SaveOptions? options = null) => _data.Encode(options ?? SaveOptions.Default);

    public void Write(Stream destination, SaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(options));
    }

    /// <summary>Writes the log atomically (temp file, flush, backup rotation, rename), like a save.</summary>
    public void WriteFile(string path, SaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= SaveOptions.Default;
        AtomicFile.Write(path, Encode(options), options.BackupGenerations);
    }

    /// <summary>A mutable deep copy, for tooling (trimming, patching) and tests.</summary>
    public ReplayLogBuilder ToBuilder() => new(_data.Copy());

    /// <summary>The recording up to and including <paramref name="tick"/>: frames below it, checkpoints and saves at or before it.</summary>
    internal ReplayData Prefix(long tick) => _data.Copy(tick);
}

/// <summary>Edits a copy of a replay log; <see cref="Build"/> or <see cref="Encode"/> produce the result.</summary>
public sealed class ReplayLogBuilder
{
    private readonly ReplayData _data;

    internal ReplayLogBuilder(ReplayData data)
    {
        _data = data;
    }

    /// <summary>Replaces the payload of command <paramref name="index"/> in the frame of <paramref name="tick"/> (same length).</summary>
    public ReplayLogBuilder ReplaceCommand(long tick, int index, ReadOnlySpan<byte> payload)
    {
        if (!ReplayData.TryGet(_data.Frames, tick, out var bytes))
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "The replay has no frame at this Tick.");
        }

        var view = new InputFrameView(bytes);
        if ((uint)index >= (uint)view.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The frame has no such command.");
        }

        var existing = view.Payload(index);
        if (existing.Length != payload.Length)
        {
            throw new ArgumentException($"The payload must keep its length ({existing.Length} bytes).", nameof(payload));
        }

        var copy = (byte[])bytes.Clone();
        var w = new ByteWriter(copy.Length);
        w.WriteInt32(view.Count);
        for (int i = 0; i < view.Count; i++)
        {
            var (source, sequence, command) = view.Record(i);
            var p = i == index ? payload : view.Payload(i);
            w.WriteUInt32(source);
            w.WriteUInt32(sequence);
            w.WriteInt32(command);
            w.WriteInt32(p.Length);
            w.WriteBytes(p);
        }

        ReplayData.Put(_data.Frames, tick, w.ToArray());
        return this;
    }

    /// <summary>Drops every frame, checkpoint and save after <paramref name="tick"/> and ends the log there.</summary>
    public ReplayLogBuilder TruncateAt(long tick)
    {
        _data.Frames.RemoveAll(f => f.Tick >= tick);
        _data.Checkpoints.RemoveAll(c => c.Tick > tick);
        _data.Saves.RemoveAll(s => s.Tick > tick);
        _data.EndTick = tick;
        return this;
    }

    public ReplayLog Build() => new(_data.Copy());

    public byte[] Encode(SaveOptions? options = null) => _data.Encode(options ?? SaveOptions.Default);
}
