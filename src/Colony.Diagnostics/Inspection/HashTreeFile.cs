using System.Buffers.Binary;
using System.Globalization;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// A <see cref="WorldHashTree"/> on the wire (spec section 10.2, desync localisation): the protocol's map form
/// (<see cref="ToValue"/> / <see cref="FromValue"/>, which the server's <c>hashtree</c> op and
/// <c>colony-inspect hashtree</c> share), a <c>.tree</c> file (<see cref="Encode"/>, <see cref="WriteFile"/>), and
/// the level-by-level walk over two live hosts (<see cref="CompareLive(InspectionClient, InspectionClient, int)"/>) that fetches only the branches whose
/// hashes differ, so peers exchange hashes and never state.
/// </summary>
public sealed class HashTreeFile
{
    public const string Format = "colony-hash-tree";

    public const int FormatVersion = 1;

    /// <summary>Bytes per packed leaf: ordinal, x, y, z (little-endian int32) then the hash (little-endian uint64).</summary>
    public const int LeafBytes = (4 * sizeof(int)) + sizeof(ulong);

    private HashTreeFile(WorldHashTree tree)
    {
        Tree = tree;
    }

    public WorldHashTree Tree { get; }

    public static HashTreeFile From(WorldHashTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return new HashTreeFile(tree);
    }

    public byte[] Encode() => InspectCodec.Encode(ToValue(Tree));

    public void WriteFile(string path) => File.WriteAllBytes(path, Encode());

    public static HashTreeFile ReadFile(string path) => Decode(File.ReadAllBytes(path));

    public static HashTreeFile Decode(ReadOnlySpan<byte> bytes)
    {
        var root = InspectCodec.Decode(bytes);
        if (root.Kind != InspectValueKind.Map || root["format"].Kind != InspectValueKind.Text || root["format"].AsText != Format)
        {
            throw new FormatException("Not a Colony hash tree file.");
        }

        if (root["version"].AsWhole != FormatVersion)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Hash tree format version {root["version"].AsWhole} is not supported (expected {FormatVersion})."));
        }

        return new HashTreeFile(FromValue(root));
    }

    /// <summary>True when <paramref name="bytes"/> decode to a hash tree (as opposed to, say, a hash trail).</summary>
    public static bool IsTree(ReadOnlySpan<byte> bytes)
    {
        var root = InspectCodec.Decode(bytes);
        return root.Kind == InspectValueKind.Map && root["format"].Kind == InspectValueKind.Text && root["format"].AsText == Format;
    }

    /// <summary>
    /// The whole tree as one map: <c>format</c>, <c>version</c>, <c>tick</c>, <c>schemaHash</c>, <c>hash</c> and
    /// <c>groups</c>, each group <c>{name, hash, items}</c>, each item <c>{name, hash, kind, leaves}</c> with the
    /// leaves packed (<see cref="LeafBytes"/> each).
    /// </summary>
    public static InspectValue ToValue(WorldHashTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var groups = new List<InspectValue>(tree.Groups.Count);
        foreach (var group in tree.Groups)
        {
            groups.Add(GroupValue(group, withItems: true));
        }

        return InspectValue.Map(
            new("format", Format),
            new("version", FormatVersion),
            new("tick", tree.Tick),
            new("schemaHash", InspectValue.From(tree.SchemaHash)),
            new("hash", InspectValue.From(tree.WorldHash)),
            new("groups", InspectValue.List(groups)));
    }

    /// <summary>The root level only: <c>tick</c>, <c>schemaHash</c>, <c>hash</c> and the groups' names and hashes.</summary>
    public static InspectValue RootValue(WorldHashTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var groups = new List<InspectValue>(tree.Groups.Count);
        foreach (var group in tree.Groups)
        {
            groups.Add(InspectValue.Map(new("name", group.Name), new("hash", InspectValue.From(group.Hash))));
        }

        return InspectValue.Map(
            new("tick", tree.Tick),
            new("schemaHash", InspectValue.From(tree.SchemaHash)),
            new("hash", InspectValue.From(tree.WorldHash)),
            new("groups", InspectValue.List(groups)));
    }

    /// <summary>One group with its items' names, hashes and kinds (no leaves).</summary>
    public static InspectValue GroupValue(HashTreeNode group, bool withItems)
    {
        ArgumentNullException.ThrowIfNull(group);
        var items = new List<InspectValue>(group.Children.Count);
        foreach (var item in group.Children)
        {
            items.Add(withItems ? ItemValue(item) : InspectValue.Map(new("name", item.Name), new("hash", InspectValue.From(item.Hash)), new("kind", (long)item.LeafKind)));
        }

        return InspectValue.Map(new("name", group.Name), new("hash", InspectValue.From(group.Hash)), new("items", InspectValue.List(items)));
    }

    /// <summary>One item with its leaves packed.</summary>
    public static InspectValue ItemValue(HashTreeNode item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var bytes = new byte[item.Leaves.Count * LeafBytes];
        for (int i = 0; i < item.Leaves.Count; i++)
        {
            var leaf = item.Leaves[i];
            var span = bytes.AsSpan(i * LeafBytes, LeafBytes);
            BinaryPrimitives.WriteInt32LittleEndian(span, leaf.Ordinal);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], leaf.X);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], leaf.Y);
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], leaf.Z);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], leaf.Hash);
        }

        return InspectValue.Map(new("name", item.Name), new("hash", InspectValue.From(item.Hash)), new("kind", (long)item.LeafKind), new("leaves", InspectValue.From(bytes)));
    }

    /// <summary>Rebuilds a tree from <see cref="ToValue"/>'s map (groups without <c>items</c> and items without <c>leaves</c> are accepted as unexpanded nodes).</summary>
    public static WorldHashTree FromValue(InspectValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var groups = new List<HashTreeNode>();
        foreach (var group in value["groups"].Items)
        {
            groups.Add(GroupFromValue(group));
        }

        return new WorldHashTree(value["tick"].AsWhole, (ulong)value["schemaHash"].AsWhole, (ulong)value["hash"].AsWhole, groups);
    }

    public static HashTreeNode GroupFromValue(InspectValue group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var items = new List<HashTreeNode>();
        if (!group["items"].IsNull)
        {
            foreach (var item in group["items"].Items)
            {
                items.Add(ItemFromValue(item));
            }
        }

        return new HashTreeNode(group["name"].AsText, (ulong)group["hash"].AsWhole, HashTreeLeafKind.None, items, []);
    }

    public static HashTreeNode ItemFromValue(InspectValue item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var kind = (HashTreeLeafKind)(byte)item["kind"].AsWhole;
        var leaves = new List<HashTreeLeaf>();
        if (!item["leaves"].IsNull)
        {
            var bytes = item["leaves"].AsBytes;
            if (bytes.Length % LeafBytes != 0)
            {
                throw new FormatException("A leaf list is not a whole number of packed leaves.");
            }

            for (int offset = 0; offset < bytes.Length; offset += LeafBytes)
            {
                var span = bytes.Slice(offset, LeafBytes);
                leaves.Add(new HashTreeLeaf(
                    BinaryPrimitives.ReadInt32LittleEndian(span),
                    BinaryPrimitives.ReadInt32LittleEndian(span[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(span[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(span[12..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(span[16..])));
            }
        }

        return new HashTreeNode(item["name"].AsText, (ulong)item["hash"].AsWhole, kind, [], leaves);
    }

    /// <summary>The whole tree of a live host (its <c>hashtree</c> op with <c>full</c>).</summary>
    public static WorldHashTree Fetch(InspectionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return FromValue(client.Request("hashtree", new KeyValuePair<string, InspectValue>("full", true)));
    }

    /// <summary>
    /// Localises a desync between two live hosts level by level: the roots first, then the items of every group
    /// whose hash differs, then the leaves of every item whose hash differs. Only hashes travel; the fetched partial
    /// trees compare exactly like complete ones (<see cref="WorldHashTree.Compare"/>).
    /// </summary>
    public static HashTreeComparison CompareLive(InspectionClient left, InspectionClient right, int limit = 64)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var (a, b) = FetchDiverging(left, right);
        return WorldHashTree.Compare(a, b, limit);
    }

    /// <summary>One side is a tree in hand (a file), the other a live host walked level by level against it.</summary>
    public static HashTreeComparison CompareLive(WorldHashTree left, InspectionClient right, int limit = 64)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var b = FetchDiverging(new TreeSource(left), new ClientSource(right));
        return WorldHashTree.Compare(left, b, limit);
    }

    /// <summary>The two partial trees <see cref="CompareLive(InspectionClient, InspectionClient, int)"/> walks: every differing branch expanded, agreeing branches left as their hash.</summary>
    public static (WorldHashTree Left, WorldHashTree Right) FetchDiverging(InspectionClient left, InspectionClient right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var a = new ClientSource(left);
        var b = new ClientSource(right);
        var rootA = a.Root();
        var rootB = b.Root();
        var groupsA = new List<HashTreeNode>();
        var groupsB = new List<HashTreeNode>();
        ExpandGroups(a, b, rootA, rootB, groupsA, groupsB);
        return (new WorldHashTree(rootA.Tick, rootA.SchemaHash, rootA.WorldHash, groupsA), new WorldHashTree(rootB.Tick, rootB.SchemaHash, rootB.WorldHash, groupsB));
    }

    private static WorldHashTree FetchDiverging(ISource a, ISource b)
    {
        var rootA = a.Root();
        var rootB = b.Root();
        var groupsA = new List<HashTreeNode>();
        var groupsB = new List<HashTreeNode>();
        ExpandGroups(a, b, rootA, rootB, groupsA, groupsB);
        return new WorldHashTree(rootB.Tick, rootB.SchemaHash, rootB.WorldHash, groupsB);
    }

    private static void ExpandGroups(ISource a, ISource b, WorldHashTree rootA, WorldHashTree rootB, List<HashTreeNode> groupsA, List<HashTreeNode> groupsB)
    {
        foreach (var ga in rootA.Groups)
        {
            var gb = rootB.Group(ga.Name);
            if (rootA.WorldHash == rootB.WorldHash || gb is null || ga.Hash == gb.Hash)
            {
                groupsA.Add(ga);
                continue;
            }

            var itemsA = a.Group(ga.Name);
            var itemsB = b.Group(ga.Name);
            var expandedA = new List<HashTreeNode>(itemsA.Children.Count);
            var expandedB = new List<HashTreeNode>(itemsB.Children.Count);
            foreach (var ia in itemsA.Children)
            {
                var ib = itemsB.Child(ia.Name);
                if (ib is null || ia.Hash == ib.Hash || ia.LeafKind == HashTreeLeafKind.None)
                {
                    expandedA.Add(ia);
                    continue;
                }

                expandedA.Add(a.Item(ga.Name, ia.Name));
                expandedB.Add(b.Item(ga.Name, ia.Name));
            }

            foreach (var ib in itemsB.Children)
            {
                if (!expandedB.Exists(n => string.Equals(n.Name, ib.Name, StringComparison.Ordinal)))
                {
                    expandedB.Add(ib);
                }
            }

            groupsA.Add(new HashTreeNode(ga.Name, ga.Hash, HashTreeLeafKind.None, expandedA, []));
            groupsB.Add(new HashTreeNode(gb.Name, gb.Hash, HashTreeLeafKind.None, expandedB, []));
        }

        foreach (var gb in rootB.Groups)
        {
            if (!groupsB.Exists(n => string.Equals(n.Name, gb.Name, StringComparison.Ordinal)))
            {
                groupsB.Add(gb);
            }
        }
    }

    private interface ISource
    {
        public WorldHashTree Root();

        public HashTreeNode Group(string name);

        public HashTreeNode Item(string group, string name);
    }

    private sealed class ClientSource(InspectionClient client) : ISource
    {
        public WorldHashTree Root() => FromValue(client.Request("hashtree"));

        public HashTreeNode Group(string name) => GroupFromValue(client.Request("hashtree", new KeyValuePair<string, InspectValue>("group", name)));

        public HashTreeNode Item(string group, string name) => ItemFromValue(client.Request("hashtree", new KeyValuePair<string, InspectValue>("group", group), new KeyValuePair<string, InspectValue>("item", name)));
    }

    private sealed class TreeSource(WorldHashTree tree) : ISource
    {
        public WorldHashTree Root() => tree;

        public HashTreeNode Group(string name) => tree.Group(name) ?? throw new KeyNotFoundException($"No group '{name}'.");

        public HashTreeNode Item(string group, string name) => Group(group).Child(name) ?? throw new KeyNotFoundException($"No item '{name}' in group '{group}'.");
    }
}
