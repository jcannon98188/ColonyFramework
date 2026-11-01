using System.Buffers.Binary;
using System.Globalization;

namespace Colony.Persistence;

/// <summary>Options of <see cref="JoinPackage.Join"/>.</summary>
public sealed class JoinOptions
{
    public static JoinOptions Default { get; } = new();

    /// <summary>
    /// Host settings of the joining World (threads, diagnostics, gate). The seed and the Entity capacity come from
    /// the save. A held <see cref="WorldOptions.TickGate"/> is stepped once per catch-up Tick.
    /// </summary>
    public WorldOptions WorldOptions { get; init; } = WorldOptions.Default;

    /// <summary>
    /// The joining peer's identity, compared with the host's by <see cref="PeerHandshake.Compare"/> before anything
    /// loads. Null compares only what the schema itself fixes (schema hash and format versions).
    /// </summary>
    public PeerIdentity? Local { get; init; }
}

/// <summary>One pending or recorded Input Command inside a join package, as the wire form carries it.</summary>
internal readonly record struct JoinInput(long Tick, uint Source, uint Sequence, int Type, byte[] Payload);

/// <summary>
/// Join-in-progress for lockstep peers (spec section 7.4): one verifiable bundle of a save at Tick S, the input
/// log from S to the host's current Tick E (the canonical input frame of every non-empty Tick, plus checkpoint
/// World hashes, at least at S and E), the commands already submitted for E and later (pending input, including
/// input-delayed commands near the save point), and the host's <see cref="PeerIdentity"/>.
/// <para>
/// Host: <see cref="Capture(World, PeerIdentityOptions?)"/> (save now, S = E) or
/// <see cref="Capture(ReplayRecorder, PeerIdentityOptions?)"/> (latest full save of a recording, S &lt;= E),
/// then <see cref="Encode"/> or <see cref="ToChunks"/>. New peer: <see cref="JoinPackageAssembler"/> (or
/// <see cref="Open"/>), then <see cref="Join"/>, which verifies the identity, loads the save (verifying its
/// World hash), plays forward to E verifying every checkpoint and leaves the pending commands submitted, so
/// the World continues in step with the host once it receives the commands submitted after the capture.
/// </para>
/// Stored in the save container (header flag <see cref="SaveContainer.JoinFlag"/>) with the sections
/// <c>join</c>, <c>identity</c>, <c>save</c>, <c>frames</c>, <c>checkpoints</c> and <c>pending</c>.
/// </summary>
public sealed class JoinPackage
{
    /// <summary>Format version of the package sections and of the chunk header.</summary>
    public const int Version = 1;

    /// <summary>Default chunk payload size of <see cref="ToChunks"/> (64 KiB).</summary>
    public const int DefaultChunkSize = 64 * 1024;

    internal const string HeaderSection = "join";
    internal const string IdentitySection = "identity";
    internal const string SaveSection = "save";
    internal const string FramesSection = "frames";
    internal const string CheckpointsSection = "checkpoints";
    internal const string PendingSection = "pending";

    private readonly byte[] _save;
    private readonly (long Tick, byte[] Frame)[] _frames;
    private readonly (long Tick, ulong Hash)[] _checkpoints;
    private readonly JoinInput[] _pending;

    internal JoinPackage(PeerIdentity identity, long saveTick, long endTick, byte[] save, (long Tick, byte[] Frame)[] frames, (long Tick, ulong Hash)[] checkpoints, JoinInput[] pending)
    {
        Identity = identity;
        SaveTick = saveTick;
        EndTick = endTick;
        _save = save;
        _frames = frames;
        _checkpoints = checkpoints;
        _pending = pending;
        Validate();
    }

    /// <summary>The host's identity at capture.</summary>
    public PeerIdentity Identity { get; }

    /// <summary>The Tick of the embedded save (S).</summary>
    public long SaveTick { get; }

    /// <summary>The host's Tick at capture (E): <see cref="Join"/> returns a World at this Tick.</summary>
    public long EndTick { get; }

    /// <summary>The World hash the save records (the checkpoint at <see cref="SaveTick"/>).</summary>
    public ulong SaveWorldHash => CheckpointAt(SaveTick);

    /// <summary>The host's World hash at <see cref="EndTick"/>.</summary>
    public ulong EndWorldHash => CheckpointAt(EndTick);

    /// <summary>The embedded save container.</summary>
    public ReadOnlyMemory<byte> Save => _save;

    /// <summary>Recorded input frames between the save and the capture (non-empty Ticks only).</summary>
    public int FrameCount => _frames.Length;

    /// <summary>Commands submitted on the host for <see cref="EndTick"/> or later and not yet consumed.</summary>
    public int PendingInputCount => _pending.Length;

    /// <summary>Ticks carrying a verification hash, ascending (always <see cref="SaveTick"/> and <see cref="EndTick"/>).</summary>
    public IReadOnlyList<long> CheckpointTicks => Array.ConvertAll(_checkpoints, static c => c.Tick);

    /// <summary>A package of <paramref name="host"/> as it is now: a save at the current Tick plus its pending commands. Call between Ticks.</summary>
    public static JoinPackage Capture(World host, PeerIdentityOptions? identity = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        long tick = host.CurrentTick;
        return new JoinPackage(PeerIdentity.Of(host, identity), tick, tick, host.SaveToBytes(), [], [(tick, host.Hash())], Pending(host));
    }

    /// <summary>
    /// A package built from a running recording: the latest full save the recorder embedded (its start save or a
    /// <see cref="ReplayOptions.FullSaveEvery"/> save), the frames and checkpoints recorded since, and the
    /// World's pending commands. Saves nothing new, so the host does not stall. Call between Ticks, after <see cref="ReplayRecorder.RecordTick"/>.
    /// </summary>
    public static JoinPackage Capture(ReplayRecorder recorder, PeerIdentityOptions? identity = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var world = recorder.World;
        var log = recorder.Finish();
        long end = log.EndTick;
        if (log.LatestFullSaveAtOrBefore(end) is not { } start || !log.TryGetFullSave(start, out var save))
        {
            throw new InvalidOperationException("The recording embeds no full save; record with ReplayOptions.EmbedStartSave or FullSaveEvery, or capture the World directly.");
        }

        var frames = new List<(long, byte[])>();
        foreach (long t in log.FrameTicks)
        {
            if (t >= start && t < end && log.TryGetFrame(t, out var frame))
            {
                frames.Add((t, frame.Bytes.ToArray()));
            }
        }

        var checkpoints = new List<(long Tick, ulong Hash)> { (start, WorldPersistence.ReadManifest(save).WorldHash) };
        foreach (long t in log.CheckpointTicks)
        {
            if (t > start && t <= end && log.TryGetCheckpoint(t, out ulong hash))
            {
                checkpoints.Add((t, hash));
            }
        }

        return new JoinPackage(PeerIdentity.Of(world, identity), start, end, save.ToArray(), [.. frames], [.. checkpoints], Pending(world));
    }

    private static JoinInput[] Pending(World world)
        => Array.ConvertAll(world.Inputs.CopyPending(), static p => new JoinInput(p.Tick, p.Source, p.Sequence, p.Type, p.Payload));

    /// <summary>The package as one container image.</summary>
    public byte[] Encode(SaveOptions? options = null)
    {
        var sections = new List<ContainerSection>(6);
        var w = new ByteWriter(64);
        w.WriteInt32(Version);
        w.WriteInt64(SaveTick);
        w.WriteInt64(EndTick);
        sections.Add(new ContainerSection(HeaderSection, w.ToArray()));

        w = new ByteWriter(256);
        Identity.Write(w);
        sections.Add(new ContainerSection(IdentitySection, w.ToArray()));
        sections.Add(new ContainerSection(SaveSection, _save));

        w = new ByteWriter(1024);
        w.WriteInt32(_frames.Length);
        foreach (var (tick, frame) in _frames)
        {
            w.WriteInt64(tick);
            w.WriteInt32(frame.Length);
            w.WriteBytes(frame);
        }

        sections.Add(new ContainerSection(FramesSection, w.ToArray()));

        w = new ByteWriter(16 + (_checkpoints.Length * 16));
        w.WriteInt32(_checkpoints.Length);
        foreach (var (tick, hash) in _checkpoints)
        {
            w.WriteInt64(tick);
            w.WriteUInt64(hash);
        }

        sections.Add(new ContainerSection(CheckpointsSection, w.ToArray()));

        w = new ByteWriter(16 + (_pending.Length * 32));
        w.WriteInt32(_pending.Length);
        foreach (var p in _pending)
        {
            w.WriteInt64(p.Tick);
            w.WriteUInt32(p.Source);
            w.WriteUInt32(p.Sequence);
            w.WriteInt32(p.Type);
            w.WriteInt32(p.Payload.Length);
            w.WriteBytes(p.Payload);
        }

        sections.Add(new ContainerSection(PendingSection, w.ToArray()));
        return SaveContainer.Encode(sections, options ?? SaveOptions.Default, SaveContainer.JoinFlag);
    }

    /// <summary>The encoded package split into transport chunks (see <see cref="JoinPackageChunks"/>).</summary>
    public IReadOnlyList<byte[]> ToChunks(int chunkSize = DefaultChunkSize, SaveOptions? options = null)
        => JoinPackageChunks.Split(Encode(options), chunkSize);

    /// <summary>Decodes a package image, verifying every section hash; failures throw <see cref="JoinPackageException"/>.</summary>
    public static JoinPackage Open(ReadOnlyMemory<byte> image)
    {
        string section = "container";
        try
        {
            var head = image.Span;
            if (head.Length >= SaveContainer.HeaderSize && SaveContainer.HasMagic(head)
                && BinaryPrimitives.ReadUInt64LittleEndian(head[24..]) + BinaryPrimitives.ReadUInt64LittleEndian(head[32..]) > (ulong)head.Length)
            {
                throw new JoinPackageException(JoinFailureReason.PackageCorrupt, $"The join package is truncated: its table of contents ends past the {head.Length} bytes received.") { Section = "container" };
            }

            var reader = new SaveContainer.Reader(image);
            if ((reader.Flags & SaveContainer.JoinFlag) == 0 || !reader.Has(HeaderSection))
            {
                throw new JoinPackageException(JoinFailureReason.NotAJoinPackage, "The container is not a join package.");
            }

            section = HeaderSection;
            var r = new ByteReader(reader.Section(HeaderSection), HeaderSection);
            int version = r.ReadInt32();
            if (version != Version)
            {
                throw new JoinPackageException(JoinFailureReason.UnsupportedVersion, $"Join package version {version} is not supported by this build (version {Version}).");
            }

            long saveTick = r.ReadInt64();
            long endTick = r.ReadInt64();
            r.ExpectEnd();

            section = IdentitySection;
            var ir = new ByteReader(reader.Section(IdentitySection), IdentitySection);
            var identity = PeerIdentity.Read(ref ir);
            ir.ExpectEnd();

            section = SaveSection;
            byte[] save = reader.Section(SaveSection);

            section = FramesSection;
            var fr = new ByteReader(reader.Section(FramesSection), FramesSection);
            var frames = new (long, byte[])[fr.ReadCount(12)];
            for (int i = 0; i < frames.Length; i++)
            {
                long tick = fr.ReadInt64();
                var frame = fr.ReadBytes(fr.ReadInt32()).ToArray();
                _ = new InputFrameView(frame);
                frames[i] = (tick, frame);
            }

            fr.ExpectEnd();

            section = CheckpointsSection;
            var cr = new ByteReader(reader.Section(CheckpointsSection), CheckpointsSection);
            var checkpoints = new (long, ulong)[cr.ReadCount(16)];
            for (int i = 0; i < checkpoints.Length; i++)
            {
                long tick = cr.ReadInt64();
                checkpoints[i] = (tick, cr.ReadUInt64());
            }

            cr.ExpectEnd();

            section = PendingSection;
            var pr = new ByteReader(reader.Section(PendingSection), PendingSection);
            var pending = new JoinInput[pr.ReadCount(24)];
            for (int i = 0; i < pending.Length; i++)
            {
                long tick = pr.ReadInt64();
                uint source = pr.ReadUInt32();
                uint sequence = pr.ReadUInt32();
                int type = pr.ReadInt32();
                pending[i] = new JoinInput(tick, source, sequence, type, pr.ReadBytes(pr.ReadInt32()).ToArray());
            }

            pr.ExpectEnd();
            section = HeaderSection;
            return new JoinPackage(identity, saveTick, endTick, save, frames, checkpoints, pending);
        }
        catch (SaveCorruptionException e)
        {
            throw new JoinPackageException(JoinFailureReason.PackageCorrupt, $"The join package is corrupt: {e.Message}", e) { Section = e.Section ?? section };
        }
        catch (SaveFormatException e)
        {
            throw new JoinPackageException(JoinFailureReason.Malformed, $"The join package is malformed ({section}): {e.Message}", e) { Section = section };
        }
    }

    /// <summary>
    /// Builds the joining peer's World: compares identities (failing with <see cref="JoinFailureReason.IdentityMismatch"/>),
    /// loads the save with its World hash verified (<see cref="JoinFailureReason.SaveCorrupt"/>), submits every
    /// recorded and pending command, and Ticks forward to <see cref="EndTick"/> comparing each checkpoint
    /// (<see cref="JoinFailureReason.Diverged"/>). The returned World is at <see cref="EndTick"/> with the pending
    /// commands submitted; feed it the commands the host received after the capture and it stays in step.
    /// </summary>
    public World Join(WorldSchema schema, JoinOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        options ??= JoinOptions.Default;
        var local = options.Local ?? (PeerIdentity.Of(schema, Identity.Seed) with { Build = Identity.Build, TickRate = Identity.TickRate, Settings = Identity.Settings, Plugins = Identity.Plugins });
        var handshake = PeerHandshake.Compare(local, Identity);
        if (local.SchemaHash != schema.SchemaHash)
        {
            throw new ArgumentException("The local identity describes another schema than the one joining.", nameof(options));
        }

        if (!handshake.IsMatch)
        {
            throw new JoinPackageException(JoinFailureReason.IdentityMismatch, "This peer does not match the host:\n" + handshake.Describe()) { Handshake = handshake };
        }

        World world;
        try
        {
            var manifest = WorldPersistence.ReadManifest(_save);
            if (manifest.Tick != SaveTick || manifest.WorldHash != SaveWorldHash || manifest.SchemaHash != Identity.SchemaHash || manifest.Seed != Identity.Seed)
            {
                throw new JoinPackageException(JoinFailureReason.SaveCorrupt, $"The embedded save (Tick {manifest.Tick}, World hash 0x{manifest.WorldHash:X16}) is not the save the package describes (Tick {SaveTick}, World hash 0x{SaveWorldHash:X16}).") { Section = SaveSection };
            }

            world = WorldPersistence.Load(_save, schema, new LoadOptions { VerifyWorldHash = true, WorldOptions = options.WorldOptions });
        }
        catch (SaveFormatException e)
        {
            throw new JoinPackageException(JoinFailureReason.SaveCorrupt, $"The embedded save does not load: {e.Message}", e) { Section = SaveSection };
        }

        try
        {
            SubmitAll(world);
            var gate = options.WorldOptions.TickGate;
            while (world.CurrentTick < EndTick)
            {
                if (gate is { IsHeld: true })
                {
                    gate.Step();
                }

                if (!world.Tick())
                {
                    throw new InvalidOperationException("The Tick gate held a catch-up Tick.");
                }

                Verify(world);
            }

            return world;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Submits the recorded frames and the pending commands in (source, sequence) order: the order the host
    /// accepted them in, which the per-source sequence rule requires (a later command may take effect earlier).
    /// </summary>
    private void SubmitAll(World world)
    {
        var all = new List<JoinInput>(_pending.Length + (_frames.Length * 8));
        foreach (var (tick, frame) in _frames)
        {
            var view = new InputFrameView(frame);
            for (int i = 0; i < view.Count; i++)
            {
                var (source, sequence, type) = view.Record(i);
                all.Add(new JoinInput(tick, source, sequence, type, view.Payload(i).ToArray()));
            }
        }

        all.AddRange(_pending);
        all.Sort(static (a, b) => a.Source != b.Source ? a.Source.CompareTo(b.Source) : a.Sequence.CompareTo(b.Sequence));
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if ((uint)e.Type >= (uint)world.Schema.InputCommandCount)
            {
                throw new JoinPackageException(JoinFailureReason.Malformed, $"The package names Input Command {e.Type}, which the schema does not register.");
            }

            if (i > 0 && all[i - 1].Source == e.Source && all[i - 1].Sequence == e.Sequence)
            {
                throw new JoinPackageException(JoinFailureReason.Malformed, $"The package lists command {e.Sequence} of source {e.Source} twice.");
            }

            try
            {
                world.Inputs.Submit(new InputSource(e.Source), e.Sequence, new InputCommandType(e.Type), e.Payload, e.Tick);
            }
            catch (ArgumentException ex)
            {
                throw new JoinPackageException(JoinFailureReason.Malformed, $"The package's command {e.Sequence} of source {e.Source} cannot be submitted: {ex.Message}", ex);
            }
        }
    }

    private void Verify(World world)
    {
        long tick = world.CurrentTick;
        int index = Array.FindIndex(_checkpoints, c => c.Tick == tick);
        if (index < 0)
        {
            return;
        }

        ulong expected = _checkpoints[index].Hash;
        ulong actual = world.Hash();
        if (actual != expected)
        {
            throw new JoinPackageException(JoinFailureReason.Diverged, $"Playing the package's input log forward diverged from the host at Tick {tick}: expected World hash 0x{expected:X16}, got 0x{actual:X16}.") { Tick = tick };
        }
    }

    private ulong CheckpointAt(long tick)
    {
        foreach (var (t, hash) in _checkpoints)
        {
            if (t == tick)
            {
                return hash;
            }
        }

        throw new InvalidOperationException("Unreachable: the package always has checkpoints at its save and end Ticks.");
    }

    private void Validate()
    {
        if (SaveTick < 0 || EndTick < SaveTick)
        {
            throw Malformed($"Ticks out of order: save at {SaveTick}, end at {EndTick}.");
        }

        long previous = long.MinValue;
        foreach (var (tick, _) in _frames)
        {
            if (tick < SaveTick || tick >= EndTick || tick <= previous)
            {
                throw Malformed($"Frame Tick {tick} is out of order or outside [{SaveTick}, {EndTick}).");
            }

            previous = tick;
        }

        previous = long.MinValue;
        bool hasStart = false;
        bool hasEnd = false;
        foreach (var (tick, _) in _checkpoints)
        {
            if (tick < SaveTick || tick > EndTick || tick <= previous)
            {
                throw Malformed($"Checkpoint Tick {tick} is out of order or outside [{SaveTick}, {EndTick}].");
            }

            hasStart |= tick == SaveTick;
            hasEnd |= tick == EndTick;
            previous = tick;
        }

        if (!hasStart || !hasEnd)
        {
            throw Malformed($"The package needs checkpoint hashes at its save Tick {SaveTick} and its end Tick {EndTick}.");
        }

        foreach (var p in _pending)
        {
            if (p.Tick < EndTick)
            {
                throw Malformed(string.Create(CultureInfo.InvariantCulture, $"Pending command {p.Sequence} of source {p.Source} takes effect at Tick {p.Tick}, before the package's end Tick {EndTick}."));
            }
        }
    }

    private static JoinPackageException Malformed(string message) => new(JoinFailureReason.Malformed, "The join package is malformed: " + message);

    /// <summary>Test seam: the same package with other save bytes.</summary>
    internal JoinPackage WithSave(byte[] save) => new(Identity, SaveTick, EndTick, save, _frames, _checkpoints, _pending);

    /// <summary>Test seam: the same package with another frame at <paramref name="tick"/>.</summary>
    internal JoinPackage WithFrame(long tick, byte[] frame)
        => new(Identity, SaveTick, EndTick, _save, Array.ConvertAll(_frames, f => f.Tick == tick ? (tick, frame) : f), _checkpoints, _pending);

    internal ReadOnlySpan<(long Tick, byte[] Frame)> Frames => _frames;
}
