using System.Runtime.InteropServices;
using Colony.Numerics;
using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>TASK-33 AC #1-#3 and #5 (spec section 7.4): join-in-progress via a verifiable save plus input log package.</summary>
public sealed class JoinPackageTests
{
    private static readonly ReplayOptions Recording = new() { CheckpointEvery = 5, FullSaveEvery = 10 };

    [Fact]
    public void A_package_bundles_the_latest_save_the_input_log_since_pending_commands_identity_and_hashes()
    {
        using var host = new JoinHost(workers: 2);
        host.RunTo(23);

        var package = JoinPackage.Capture(host.Recorder, new PeerIdentityOptions { Build = "test-build", TickRate = 30 });

        Assert.Equal(20, package.SaveTick);
        Assert.Equal(23, package.EndTick);
        Assert.Equal(3, package.FrameCount); // Ticks 20, 21, 22 all had input
        Assert.Equal(host.World.Inputs.PendingCount, package.PendingInputCount);
        Assert.True(package.PendingInputCount > 0, "input delay leaves commands pending at the capture");
        Assert.Equal([20L, 23L], package.CheckpointTicks);
        Assert.Equal(host.Hashes[20], package.SaveWorldHash);
        Assert.Equal(host.Hashes[23], package.EndWorldHash);
        Assert.Equal("test-build", package.Identity.Build);
        Assert.Equal(host.World.Schema.SchemaHash, package.Identity.SchemaHash);

        byte[] image = package.Encode();
        Assert.Equal(["join", "identity", "save", "frames", "checkpoints", "pending"], WorldPersistence.SectionNames(image));
        var reopened = JoinPackage.Open(image);
        Assert.Equal(image, reopened.Encode());

        // Chunked: each chunk carries the format version; together they rebuild the exact image.
        var chunks = package.ToChunks(chunkSize: 512);
        Assert.True(chunks.Count > 2);
        Assert.All(chunks, c => Assert.Equal(JoinPackage.Version, BitConverter.ToInt32(c, 8)));
        var assembler = new JoinPackageAssembler();
        foreach (var chunk in chunks.Reverse())
        {
            assembler.Add(chunk);
        }

        Assert.Equal(image, assembler.Image());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(0)] // N = physical cores
    public void A_peer_joining_mid_run_from_chunks_hashes_like_the_host_every_tick(int workers)
    {
        using var host = new JoinHost(workers: 2);
        host.RunTo(23);
        var chunks = JoinPackage.Capture(host.Recorder).ToChunks(chunkSize: 700);
        host.StartRelaying();
        host.RunTo(27); // the package is in transit while the host keeps going
        host.RunTo(45);

        var assembler = new JoinPackageAssembler();
        foreach (int i in Shuffled(chunks.Count))
        {
            assembler.Add(chunks[i]);
            assembler.Add(chunks[i]); // duplicates are harmless
        }

        using var joiner = assembler.Complete().Join(host.Schema, new JoinOptions { WorldOptions = new WorldOptions { WorkerThreads = workers } });
        Assert.Equal(23, joiner.CurrentTick);
        Assert.Equal(host.Hashes[23], joiner.Hash());

        for (long tick = 23; tick < 45; tick++)
        {
            host.Relay(joiner, tick);
            Assert.True(joiner.Tick());
            Assert.Equal(host.Hashes[(int)tick + 1], joiner.Hash());
        }

        Assert.Equal(0, joiner.Inputs.PendingCount - host.World.Inputs.PendingCount);
    }

    [Fact]
    public void A_package_captured_straight_from_a_world_saves_now_and_carries_its_pending_commands()
    {
        using var host = new JoinHost(workers: 1);
        host.RunTo(7);
        var package = JoinPackage.Capture(host.World);
        host.StartRelaying();
        host.RunTo(15);

        Assert.Equal(7, package.SaveTick);
        Assert.Equal(7, package.EndTick);
        Assert.Equal(0, package.FrameCount);
        Assert.True(package.PendingInputCount > 0);

        using var joiner = JoinPackage.Open(package.Encode()).Join(host.Schema);
        for (long tick = 7; tick < 15; tick++)
        {
            host.Relay(joiner, tick);
            joiner.Tick();
            Assert.Equal(host.Hashes[(int)tick + 1], joiner.Hash());
        }
    }

    [Fact]
    public void Corrupt_truncated_foreign_and_missing_chunks_fail_with_specific_reasons()
    {
        using var host = new JoinHost(workers: 1);
        host.RunTo(12);
        var chunks = JoinPackage.Capture(host.Recorder).ToChunks(chunkSize: 400);
        host.RunTo(13);
        var other = JoinPackage.Capture(host.World).ToChunks(chunkSize: 400);

        var flipped = (byte[])chunks[1].Clone();
        flipped[^1] ^= 0x40;
        Assert.Equal(JoinFailureReason.ChunkCorrupt, Fails(() => new JoinPackageAssembler().Add(flipped)).Reason);

        var header = (byte[])chunks[1].Clone();
        header[13] ^= 0x01; // the chunk index is covered by the chunk hash
        Assert.Equal(JoinFailureReason.ChunkCorrupt, Fails(() => new JoinPackageAssembler().Add(header)).Reason);

        Assert.Equal(JoinFailureReason.ChunkTruncated, Fails(() => new JoinPackageAssembler().Add(chunks[1].AsSpan(0, chunks[1].Length - 3))).Reason);
        Assert.Equal(JoinFailureReason.ChunkTruncated, Fails(() => new JoinPackageAssembler().Add(chunks[1].AsSpan(0, 20))).Reason);
        Assert.Equal(JoinFailureReason.NotAJoinPackage, Fails(() => new JoinPackageAssembler().Add(host.World.SaveToBytes())).Reason);

        var future = (byte[])chunks[0].Clone();
        future[8] = 2;
        Assert.Equal(JoinFailureReason.UnsupportedVersion, Fails(() => new JoinPackageAssembler().Add(future)).Reason);

        var mixed = new JoinPackageAssembler();
        mixed.Add(chunks[0]);
        Assert.Equal(JoinFailureReason.ChunkFromAnotherPackage, Fails(() => mixed.Add(other[1])).Reason);

        var partial = new JoinPackageAssembler();
        partial.Add(chunks[0]);
        partial.Add(chunks[2]);
        Assert.False(partial.IsComplete);
        Assert.Equal(1, partial.MissingChunks[0]);
        var missing = Fails(() => partial.Complete());
        Assert.Equal(JoinFailureReason.ChunksMissing, missing.Reason);
        Assert.Contains("first missing: 1", missing.Message, StringComparison.Ordinal);
        Assert.Equal(JoinFailureReason.ChunksMissing, Fails(() => new JoinPackageAssembler().Complete()).Reason);
    }

    [Fact]
    public void A_package_whose_chunks_verify_but_whose_whole_does_not_is_refused()
    {
        using var host = new JoinHost(workers: 1);
        host.RunTo(12);
        var chunks = JoinPackage.Capture(host.Recorder).ToChunks(chunkSize: 400).Select(c => (byte[])c.Clone()).ToArray();

        // Re-sign a tampered chunk: it passes its own check, but the whole-package hash catches it.
        chunks[2][JoinPackageChunks.HeaderSize + 5] ^= 0x10;
        BitConverter.TryWriteBytes(chunks[2].AsSpan(40), JoinPackageChunks.ChunkHash(chunks[2]));
        var assembler = new JoinPackageAssembler();
        foreach (var chunk in chunks)
        {
            assembler.Add(chunk);
        }

        Assert.Equal(JoinFailureReason.PackageCorrupt, Fails(() => assembler.Complete()).Reason);
    }

    [Fact]
    public void A_tampered_section_a_truncated_image_or_a_plain_save_is_refused_when_opened()
    {
        using var host = new JoinHost(workers: 1);
        host.RunTo(12);
        byte[] image = JoinPackage.Capture(host.Recorder).Encode(new SaveOptions { Compression = SaveCompression.None });

        var reader = new SaveContainer.Reader(image);
        var frames = reader.Entry("frames");
        var tampered = (byte[])image.Clone();
        tampered[(int)frames.Offset + 20] ^= 0x01;
        var corrupt = Fails(() => JoinPackage.Open(tampered));
        Assert.Equal(JoinFailureReason.PackageCorrupt, corrupt.Reason);
        Assert.Equal("frames", corrupt.Section);

        Assert.Equal(JoinFailureReason.PackageCorrupt, Fails(() => JoinPackage.Open(image.AsMemory(0, image.Length - 7))).Reason);
        Assert.Equal(JoinFailureReason.NotAJoinPackage, Fails(() => JoinPackage.Open(host.World.SaveToBytes())).Reason);
        Assert.Equal(JoinFailureReason.Malformed, Fails(() => JoinPackage.Open(new byte[10])).Reason);
    }

    [Fact]
    public void A_corrupt_embedded_save_fails_before_anything_plays_and_a_bad_input_log_fails_at_the_next_checkpoint()
    {
        using var host = new JoinHost(workers: 1);
        host.RunTo(28);
        var package = JoinPackage.Capture(host.Recorder);
        Assert.Equal(20, package.SaveTick);

        // A byte flipped inside the save's archetype data: the save's own section hash refuses it.
        var save = package.Save.ToArray();
        var archetype = new SaveContainer.Reader(save).NamesWithPrefix("archetype/").Last();
        var entry = new SaveContainer.Reader(save).Entry(archetype);
        save[(int)entry.Offset + (int)entry.StoredLength - 1] ^= 0x01;
        var badSave = Fails(() => JoinPackage.Open(package.WithSave(save).Encode()).Join(host.Schema));
        Assert.Equal(JoinFailureReason.SaveCorrupt, badSave.Reason);
        Assert.Equal("save", badSave.Section);

        // A save of another Tick in place of the declared one.
        Assert.Equal(JoinFailureReason.SaveCorrupt, Fails(() => package.WithSave(host.World.SaveToBytes()).Join(host.Schema)).Reason);

        // The commands of Tick 22 altered (every payload's first field +5): the checkpoint at 25 catches the divergence.
        var frame = package.Frames.ToArray().Single(f => f.Tick == 22).Frame;
        var altered = (byte[])frame.Clone();
        int at = 4;
        for (int i = 0; i < new InputFrameView(frame).Count; i++)
        {
            int length = BitConverter.ToInt32(altered, at + 12);
            BitConverter.TryWriteBytes(altered.AsSpan(at + 16), BitConverter.ToInt32(altered, at + 16) + 5);
            at += 16 + length;
        }
        var diverged = Fails(() => JoinPackage.Open(package.WithFrame(22, altered).Encode()).Join(host.Schema));
        Assert.Equal(JoinFailureReason.Diverged, diverged.Reason);
        Assert.Equal(25, diverged.Tick);
    }

    [Fact]
    public void A_joining_peer_with_another_schema_or_identity_is_refused_with_the_handshake_reasons()
    {
        using var host = new JoinHost(workers: 1);
        host.RunTo(5);
        var package = JoinPackage.Capture(host.World, new PeerIdentityOptions { Build = "1.0", Settings = [new SessionSetting("input-delay", "2")] });

        var otherBuilder = new WorldBuilder();
        InputScenario.ComposeStatic(otherBuilder);
        otherBuilder.AddResource<HandshakeTests.ExtraState>("extra");
        var schemaMismatch = Fails(() => package.Join(otherBuilder.Build()));
        Assert.Equal(JoinFailureReason.IdentityMismatch, schemaMismatch.Reason);
        Assert.True(schemaMismatch.Handshake!.Has(HandshakeMismatchKind.SchemaHash));

        var local = PeerIdentity.Of(host.Schema, package.Identity.Seed, new PeerIdentityOptions { Build = "1.1", Settings = [new SessionSetting("input-delay", "2")] });
        var buildMismatch = Fails(() => package.Join(host.Schema, new JoinOptions { Local = local }));
        Assert.Equal(JoinFailureReason.IdentityMismatch, buildMismatch.Reason);
        Assert.Equal(HandshakeMismatchKind.Build, Assert.Single(buildMismatch.Handshake!.Mismatches).Kind);
        Assert.Contains("Different build", buildMismatch.Message, StringComparison.Ordinal);

        using var joined = package.Join(host.Schema, new JoinOptions { Local = local with { Build = "1.0" } });
        Assert.Equal(host.Hashes[5], joined.Hash());
    }

    private static JoinPackageException Fails(Action action) => Assert.Throws<JoinPackageException>(action);

    private static JoinPackageException Fails(Func<object?> action) => Assert.Throws<JoinPackageException>(() => { _ = action(); });

    private static int[] Shuffled(int count)
    {
        var order = Enumerable.Range(0, count).ToArray();
        var random = new RandomSource(0x5348_5546UL, 1);
        for (int i = count - 1; i > 0; i--)
        {
            int j = random.Int32At((ulong)i, 0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }
}

/// <summary>
/// A host World driven by a seeded input script with per-source input delay (0-2 Ticks) plus, on source 3, a
/// command taking effect earlier than one submitted before it, so pending commands straddle every capture.
/// After <see cref="StartRelaying"/> it logs every submission (with its sequence) the way a lockstep layer would forward it.
/// </summary>
internal sealed class JoinHost : IDisposable
{
    private const ulong ScriptKey = 0x4A4F_494EUL;
    private readonly List<(long HostTick, uint Source, uint Sequence, InputCommandType Type, byte[] Payload, long Effective)> _relayed = [];
    private bool _relaying;

    public JoinHost(int workers)
    {
        var builder = new WorldBuilder();
        InputScenario.ComposeStatic(builder);
        builder.DebugValidation(false);
        Schema = builder.Build();
        World = Schema.CreateWorld(new WorldOptions { Seed = InputScenario.DefaultSeed, WorkerThreads = workers, InitialEntityCapacity = 4096 });
        Recorder = new ReplayRecorder(World, new ReplayOptions { CheckpointEvery = 5, FullSaveEvery = 10 });
        Hashes.Add(World.Hash());
    }

    public WorldSchema Schema { get; }

    public World World { get; }

    public ReplayRecorder Recorder { get; }

    /// <summary>World hash after each Tick, index = Tick.</summary>
    public List<ulong> Hashes { get; } = [];

    public void StartRelaying() => _relaying = true;

    public void RunTo(long tick)
    {
        while (World.CurrentTick < tick)
        {
            Script(World.CurrentTick);
            Assert.True(Recorder.Tick());
            Hashes.Add(World.Hash());
        }
    }

    /// <summary>Forwards to <paramref name="peer"/> what the host received while it was at <paramref name="hostTick"/>.</summary>
    public void Relay(World peer, long hostTick)
    {
        foreach (var r in _relayed)
        {
            if (r.HostTick == hostTick)
            {
                peer.Inputs.Submit(new InputSource(r.Source), r.Sequence, r.Type, r.Payload, r.Effective);
            }
        }
    }

    private void Script(long tick)
    {
        for (uint source = 0; source < 4; source++)
        {
            var script = new RandomSource(InputScenario.DefaultSeed, ScriptKey).Derive((ulong)tick).Derive(source);
            long effective = tick + (source % 3);
            for (int i = 0; i < 3; i++)
            {
                ulong at = (ulong)i * 4;
                switch (script.Int32At(at, 0, 10))
                {
                    case 0:
                    case 1:
                        Submit(source, new InputScenario.Spawn { Count = script.Int32At(at + 1, 1, 11) }, effective);
                        break;
                    case 2:
                        Submit(source, new InputScenario.Dismiss { Count = script.Int32At(at + 1, 1, 4) }, effective);
                        break;
                    default:
                        Submit(source, new InputScenario.Deposit { Wood = script.Int32At(at + 1, -2, 20), Stone = script.Int32At(at + 2, 0, 9) }, effective);
                        break;
                }
            }
        }

        // Later sequence, earlier effect: resubmitting in effect order would break the per-source sequence rule.
        Submit(3, new InputScenario.Deposit { Wood = 7, Stone = (int)(tick % 5) }, tick + 4);
        Submit(3, new InputScenario.Spawn { Count = 1 }, tick + 1);
    }

    private void Submit<T>(uint source, T command, long effective) where T : unmanaged
    {
        uint sequence = World.Inputs.Submit(new InputSource(source), in command, effective);
        if (_relaying)
        {
            _relayed.Add((World.CurrentTick, source, sequence, Schema.InputCommand<T>(), MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in command)).ToArray(), effective));
        }
    }

    public void Dispose() => World.Dispose();
}
