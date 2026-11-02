namespace Colony.Persistence;

/// <summary>
/// The wire form of <see cref="LockstepSession"/> messages (little-endian): magic <c>LKS2</c>, sender, recipient,
/// flags, then the sections the flags announce. <c>hello</c>: the handshake (session parameters, start Tick and
/// hash, <see cref="PeerIdentity"/>, Input permissions). <c>sync</c>: cumulative acks (next frame Tick and next
/// checkpoint Tick the sender lacks from the recipient), explicit resend requests, frames (<c>tick, length,
/// wire frame</c>) and checkpoint hashes (<c>tick, hash</c>). <c>diagnosis</c>: after a desync, the sender's hash
/// trail over the disputed Ticks and, when kept, its hash tree at the checkpoint. Every message is idempotent:
/// drops are repaired by resending what is not yet acknowledged, duplicates and late copies are harmless.
/// </summary>
internal static class LockstepMessage
{
    public const uint Magic = 0x3253_4B4C; // "LKS2"

    public const byte HasHello = 1;
    public const byte HasSync = 2;
    public const byte HasDiagnosis = 4;
    public const byte DiagnosisSeen = 8;

    public sealed class Hello
    {
        public long StartTick { get; set; }

        public long JoinTick { get; set; }

        public int Seats { get; set; }

        public int CheckpointEvery { get; set; }

        public ulong StartHash { get; set; }

        public PeerIdentity Identity { get; set; } = null!;

        public SavedInputPermissions Permissions { get; set; } = SavedInputPermissions.Unrestricted;
    }

    public sealed class Diagnosis
    {
        public long Tick { get; set; }

        public bool HasLocalHash { get; set; }

        public ulong LocalHash { get; set; }

        public LockstepHashTrail Trail { get; set; } = new(1);

        public WorldHashTree? Tree { get; set; }
    }

    /// <summary>A decoded message; one instance is reused per session.</summary>
    public sealed class Decoded
    {
        public int Sender { get; set; }

        public int Recipient { get; set; }

        public byte Flags { get; set; }

        public Hello? Hello { get; set; }

        public long FrameAck { get; set; }

        public long HashAck { get; set; }

        public List<long> Missing { get; } = [];

        public List<(long Tick, byte[] Bytes)> Frames { get; } = [];

        public List<(long Tick, ulong Hash)> Hashes { get; } = [];

        public Diagnosis? Diagnosis { get; set; }

        public bool Has(byte flag) => (Flags & flag) != 0;
    }

    public static void WriteHeader(ByteWriter w, int sender, int recipient, byte flags)
    {
        w.WriteUInt32(Magic);
        w.WriteInt32(sender);
        w.WriteInt32(recipient);
        w.WriteByte(flags);
    }

    public static void WriteHello(ByteWriter w, long startTick, long joinTick, int seats, int checkpointEvery, ulong startHash, ReadOnlySpan<byte> identity, ReadOnlySpan<byte> permissions)
    {
        w.WriteInt64(startTick);
        w.WriteInt64(joinTick);
        w.WriteInt32(seats);
        w.WriteInt32(checkpointEvery);
        w.WriteUInt64(startHash);
        w.WriteInt32(identity.Length);
        w.WriteBytes(identity);
        w.WriteInt32(permissions.Length);
        w.WriteBytes(permissions);
    }

    public static void WriteTree(ByteWriter w, WorldHashTree tree)
    {
        w.WriteInt64(tree.Tick);
        w.WriteUInt64(tree.SchemaHash);
        w.WriteUInt64(tree.WorldHash);
        WriteNodes(w, tree.Groups);
    }

    private static void WriteNodes(ByteWriter w, IReadOnlyList<HashTreeNode> nodes)
    {
        w.WriteInt32(nodes.Count);
        foreach (var node in nodes)
        {
            w.WriteString(node.Name);
            w.WriteUInt64(node.Hash);
            w.WriteByte((byte)node.LeafKind);
            WriteNodes(w, node.Children);
            w.WriteInt32(node.Leaves.Count);
            foreach (var leaf in node.Leaves)
            {
                w.WriteInt32(leaf.Ordinal);
                w.WriteInt32(leaf.X);
                w.WriteInt32(leaf.Y);
                w.WriteInt32(leaf.Z);
                w.WriteUInt64(leaf.Hash);
            }
        }
    }

    /// <summary>Parses <paramref name="message"/> into <paramref name="into"/>; anything malformed is a <see cref="LockstepProtocolException"/>.</summary>
    public static void Decode(ReadOnlySpan<byte> message, Decoded into)
    {
        into.Hello = null;
        into.Diagnosis = null;
        into.Missing.Clear();
        into.Frames.Clear();
        into.Hashes.Clear();
        try
        {
            var r = new ByteReader(message, "lockstep message");
            if (r.ReadUInt32() != Magic)
            {
                throw new LockstepProtocolException("Not a lockstep message.");
            }

            into.Sender = r.ReadInt32();
            into.Recipient = r.ReadInt32();
            into.Flags = r.ReadByte();
            if ((into.Flags & HasHello) != 0)
            {
                var hello = new Hello
                {
                    StartTick = r.ReadInt64(),
                    JoinTick = r.ReadInt64(),
                    Seats = r.ReadInt32(),
                    CheckpointEvery = r.ReadInt32(),
                    StartHash = r.ReadUInt64(),
                };
                hello.Identity = PeerIdentity.Decode(r.ReadBytes(r.ReadCount(1)));
                var permissions = new ByteReader(r.ReadBytes(r.ReadCount(1)), "lockstep hello permissions");
                hello.Permissions = SavedInputPermissions.Read(ref permissions);
                permissions.ExpectEnd();
                into.Hello = hello;
            }

            if ((into.Flags & HasSync) != 0)
            {
                into.FrameAck = r.ReadInt64();
                into.HashAck = r.ReadInt64();
                int missing = r.ReadCount(8);
                for (int i = 0; i < missing; i++)
                {
                    into.Missing.Add(r.ReadInt64());
                }

                int frames = r.ReadCount(12);
                for (int i = 0; i < frames; i++)
                {
                    long tick = r.ReadInt64();
                    into.Frames.Add((tick, r.ReadBytes(r.ReadCount(1)).ToArray()));
                }

                int hashes = r.ReadCount(16);
                for (int i = 0; i < hashes; i++)
                {
                    into.Hashes.Add((r.ReadInt64(), r.ReadUInt64()));
                }
            }

            if ((into.Flags & HasDiagnosis) != 0)
            {
                var diagnosis = new Diagnosis { Tick = r.ReadInt64(), HasLocalHash = r.ReadByte() != 0, LocalHash = r.ReadUInt64() };
                diagnosis.Trail = LockstepHashTrail.Read(ref r);
                if (r.ReadByte() != 0)
                {
                    diagnosis.Tree = ReadTree(ref r);
                }

                into.Diagnosis = diagnosis;
            }

            r.ExpectEnd();
        }
        catch (SaveFormatException e)
        {
            throw new LockstepProtocolException("Malformed lockstep message: " + e.Message, e);
        }
    }

    private static WorldHashTree ReadTree(ref ByteReader r)
    {
        long tick = r.ReadInt64();
        ulong schemaHash = r.ReadUInt64();
        ulong worldHash = r.ReadUInt64();
        return new WorldHashTree(tick, schemaHash, worldHash, ReadNodes(ref r, 0));
    }

    private static HashTreeNode[] ReadNodes(ref ByteReader r, int depth)
    {
        if (depth > 8)
        {
            throw new SaveFormatException("The hash tree is nested too deeply.");
        }

        var nodes = new HashTreeNode[r.ReadCount(13)];
        for (int i = 0; i < nodes.Length; i++)
        {
            string name = r.ReadString();
            ulong hash = r.ReadUInt64();
            var kind = (HashTreeLeafKind)r.ReadByte();
            var children = ReadNodes(ref r, depth + 1);
            var leaves = new HashTreeLeaf[r.ReadCount(24)];
            for (int l = 0; l < leaves.Length; l++)
            {
                leaves[l] = new HashTreeLeaf(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadUInt64());
            }

            nodes[i] = new HashTreeNode(name, hash, kind, children, leaves);
        }

        return nodes;
    }
}
