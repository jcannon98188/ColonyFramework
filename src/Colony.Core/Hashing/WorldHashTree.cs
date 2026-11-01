using System.Globalization;
using Colony.Hashing;

namespace Colony;

/// <summary>What the leaves of a hash tree item stand for, which decides how a leaf's coordinates read.</summary>
public enum HashTreeLeafKind : byte
{
    /// <summary>The item has no leaves: its hash is the whole of it (a Resource, a table, an extension's opaque state).</summary>
    None = 0,

    /// <summary>One chunk of Entity storage: <c>X</c> = archetype id, <c>Y</c> = chunk index, <c>Ordinal</c> = chunk index.</summary>
    ArchetypeChunk = 1,

    /// <summary>One Field chunk: <c>Ordinal</c> = Morton ordinal, <c>(X, Y, Z)</c> = chunk coordinate.</summary>
    FieldChunk = 2,

    /// <summary>A block of Entity slots of a non-fragmenting Component: <c>Ordinal</c> = block index, slots <c>X..Y</c>.</summary>
    SlotBlock = 3,
}

/// <summary>One leaf of the hash tree: a chunk (or slot block) and the hash of its bytes.</summary>
public readonly record struct HashTreeLeaf(int Ordinal, int X, int Y, int Z, ulong Hash)
{
    /// <summary>The leaf's identity without its hash; leaves of two trees are matched by it.</summary>
    public HashTreeLeafKey Key => new(Ordinal, X, Y, Z);

    /// <summary>The leaf's place in words for a given kind, e.g. <c>chunk (1, 0, 0)</c> or <c>archetype 2 chunk 3</c>.</summary>
    public string Describe(HashTreeLeafKind kind) => kind switch
    {
        HashTreeLeafKind.ArchetypeChunk => string.Create(CultureInfo.InvariantCulture, $"archetype {X} chunk {Y}"),
        HashTreeLeafKind.FieldChunk => string.Create(CultureInfo.InvariantCulture, $"chunk ({X}, {Y}, {Z})"),
        HashTreeLeafKind.SlotBlock => string.Create(CultureInfo.InvariantCulture, $"slots {X}..{Y}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"leaf {Ordinal}"),
    };
}

/// <summary>The identity of a <see cref="HashTreeLeaf"/>.</summary>
public readonly record struct HashTreeLeafKey(int Ordinal, int X, int Y, int Z);

/// <summary>
/// An inner node of the hash tree: a group (<c>components</c>, <c>fields</c>, an extension name...) holding
/// items, or an item (a Component, a Field, a Resource) holding leaves. A node's hash is
/// <see cref="WorldHashTree.CombineHashes"/> over its children's hashes, so equal hashes mean equal subtrees.
/// </summary>
public sealed class HashTreeNode
{
    public HashTreeNode(string name, ulong hash, HashTreeLeafKind leafKind, IReadOnlyList<HashTreeNode> children, IReadOnlyList<HashTreeLeaf> leaves)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(leaves);
        Name = name;
        Hash = hash;
        LeafKind = leafKind;
        Children = children;
        Leaves = leaves;
    }

    public string Name { get; }

    public ulong Hash { get; }

    /// <summary>What <see cref="Leaves"/> are; <see cref="HashTreeLeafKind.None"/> for a group or a leafless item.</summary>
    public HashTreeLeafKind LeafKind { get; }

    public IReadOnlyList<HashTreeNode> Children { get; }

    public IReadOnlyList<HashTreeLeaf> Leaves { get; }

    /// <summary>The child named <paramref name="name"/>, or null.</summary>
    public HashTreeNode? Child(string name)
    {
        foreach (var child in Children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }
}

/// <summary>One place where two hash trees differ: a group, an item, or one leaf (chunk) of an item.</summary>
public readonly record struct HashTreeDifference(string Group, string? Item, HashTreeLeafKind LeafKind, HashTreeLeaf? Left, HashTreeLeaf? Right)
{
    /// <summary>Set when the node or leaf exists on one side only: <c>"left"</c> or <c>"right"</c>.</summary>
    public string? OnlyIn { get; init; }

    /// <summary>E.g. <c>fields/temperature chunk (1, 0, 0)</c>, <c>components/vitals archetype 0 chunk 2</c>, <c>resources/tally</c>.</summary>
    public string Describe()
    {
        string path = Item is null ? Group : Group + "/" + Item;
        var leaf = Left ?? Right;
        string where = leaf is { } l && LeafKind != HashTreeLeafKind.None ? path + " " + l.Describe(LeafKind) : path;
        return OnlyIn is null ? where : where + " (only in " + OnlyIn + ")";
    }
}

/// <summary>The outcome of <see cref="WorldHashTree.Compare"/>.</summary>
public sealed class HashTreeComparison
{
    internal HashTreeComparison(long leftTick, long rightTick, bool identical, IReadOnlyList<HashTreeDifference> differences, bool truncated)
    {
        LeftTick = leftTick;
        RightTick = rightTick;
        Identical = identical;
        Differences = differences;
        Truncated = truncated;
    }

    public long LeftTick { get; }

    public long RightTick { get; }

    /// <summary>True when the two root hashes (World hashes) agree.</summary>
    public bool Identical { get; }

    /// <summary>The diverging places, group by group in tree order; empty when identical.</summary>
    public IReadOnlyList<HashTreeDifference> Differences { get; }

    /// <summary>True when more differences existed than the comparison's limit kept.</summary>
    public bool Truncated { get; }

    /// <summary>One line: agreement, or the first differences in words.</summary>
    public string Describe()
    {
        if (Identical)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The Worlds agree at Tick {LeftTick}.");
        }

        var parts = new List<string>(Differences.Count);
        foreach (var difference in Differences)
        {
            parts.Add(difference.Describe());
        }

        string list = string.Join("; ", parts);
        return string.Create(CultureInfo.InvariantCulture, $"Diverged at Tick {LeftTick} (vs {RightTick}) in {Differences.Count}{(Truncated ? "+" : "")} place(s): {list}");
    }
}

/// <summary>
/// The hierarchical form of the World hash (spec section 10.2, desync localisation): the root is
/// <see cref="World.Hash"/>; below it groups (<c>tick</c>, <c>components</c>, <c>resources</c>, <c>tables</c>, one
/// per extension by its name such as <c>fields</c>, and <c>random</c>), below those items (one per Component, Field,
/// Resource...) and below Components and Fields one leaf per chunk. Built on demand at a Tick boundary by
/// <see cref="World.HashTree"/>; two peers that exchange trees (or walk them level by level) localise a desync to
/// the chunk without transferring state. Node hashes are <see cref="Hash64"/> digests seeded with
/// <see cref="NodeSeed"/> over the node's bytes (leaves) or its children's hashes (inner nodes).
/// </summary>
public sealed class WorldHashTree
{
    /// <summary>Seed of every tree node hash ("HTRE").</summary>
    public const ulong NodeSeed = 0x4854_5245UL;

    public const string TickGroup = "tick";

    public const string ComponentsGroup = "components";

    public const string ResourcesGroup = "resources";

    public const string TablesGroup = "tables";

    public const string RandomGroup = "random";

    /// <summary>The item under <see cref="ComponentsGroup"/> holding the Entity column of every chunk.</summary>
    public const string EntitiesItem = "(entities)";

    public WorldHashTree(long tick, ulong schemaHash, ulong worldHash, IReadOnlyList<HashTreeNode> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        Tick = tick;
        SchemaHash = schemaHash;
        WorldHash = worldHash;
        Groups = groups;
    }

    public long Tick { get; }

    public ulong SchemaHash { get; }

    /// <summary>The root: exactly <see cref="World.Hash"/> of the World the tree was built from.</summary>
    public ulong WorldHash { get; }

    public IReadOnlyList<HashTreeNode> Groups { get; }

    /// <summary>The group named <paramref name="name"/>, or null.</summary>
    public HashTreeNode? Group(string name)
    {
        foreach (var group in Groups)
        {
            if (string.Equals(group.Name, name, StringComparison.Ordinal))
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>The hash of an inner node from its children's hashes, in order.</summary>
    public static ulong CombineHashes(ReadOnlySpan<ulong> hashes) => Hash64.Compute(hashes, NodeSeed);

    /// <summary>
    /// Compares two trees level by level: equal node hashes are not descended into, so partially fetched trees
    /// (a group or item whose children were never requested because the hashes agreed) compare exactly like
    /// complete ones. Trees of different schemas are refused. At most <paramref name="limit"/> differences are kept.
    /// </summary>
    public static HashTreeComparison Compare(WorldHashTree left, WorldHashTree right, int limit = 64)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (left.SchemaHash != right.SchemaHash)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The trees come from different schemas ({left.SchemaHash:x16} vs {right.SchemaHash:x16})."), nameof(right));
        }

        var differences = new List<HashTreeDifference>();
        bool truncated = false;
        if (left.WorldHash != right.WorldHash)
        {
            truncated = CompareChildren(left.Groups, right.Groups, differences, limit);
            if (differences.Count == 0)
            {
                // The roots differ but nothing below does: the trees were built from different bytes outside any node.
                differences.Add(new HashTreeDifference("(root)", null, HashTreeLeafKind.None, null, null));
            }
        }

        return new HashTreeComparison(left.Tick, right.Tick, left.WorldHash == right.WorldHash, differences, truncated);
    }

    private static bool CompareChildren(IReadOnlyList<HashTreeNode> left, IReadOnlyList<HashTreeNode> right, List<HashTreeDifference> differences, int limit)
    {
        var rightByName = new Dictionary<string, HashTreeNode>(right.Count, StringComparer.Ordinal);
        foreach (var node in right)
        {
            rightByName[node.Name] = node;
        }

        foreach (var l in left)
        {
            if (differences.Count >= limit)
            {
                return true;
            }

            if (!rightByName.Remove(l.Name, out var r))
            {
                differences.Add(new HashTreeDifference(l.Name, null, HashTreeLeafKind.None, null, null) { OnlyIn = "left" });
                continue;
            }

            if (l.Hash == r.Hash)
            {
                continue;
            }

            if (l.Children.Count == 0 && r.Children.Count == 0)
            {
                differences.Add(new HashTreeDifference(l.Name, null, HashTreeLeafKind.None, null, null));
                continue;
            }

            if (CompareItems(l, r, differences, limit))
            {
                return true;
            }
        }

        foreach (var r in right)
        {
            if (rightByName.ContainsKey(r.Name))
            {
                if (differences.Count >= limit)
                {
                    return true;
                }

                differences.Add(new HashTreeDifference(r.Name, null, HashTreeLeafKind.None, null, null) { OnlyIn = "right" });
            }
        }

        return false;
    }

    private static bool CompareItems(HashTreeNode leftGroup, HashTreeNode rightGroup, List<HashTreeDifference> differences, int limit)
    {
        string group = leftGroup.Name;
        var rightByName = new Dictionary<string, HashTreeNode>(rightGroup.Children.Count, StringComparer.Ordinal);
        foreach (var node in rightGroup.Children)
        {
            rightByName[node.Name] = node;
        }

        foreach (var l in leftGroup.Children)
        {
            if (differences.Count >= limit)
            {
                return true;
            }

            if (!rightByName.Remove(l.Name, out var r))
            {
                differences.Add(new HashTreeDifference(group, l.Name, l.LeafKind, null, null) { OnlyIn = "left" });
                continue;
            }

            if (l.Hash == r.Hash)
            {
                continue;
            }

            if (l.Leaves.Count == 0 && r.Leaves.Count == 0)
            {
                differences.Add(new HashTreeDifference(group, l.Name, HashTreeLeafKind.None, null, null));
                continue;
            }

            if (CompareLeaves(group, l, r, differences, limit))
            {
                return true;
            }
        }

        foreach (var r in rightGroup.Children)
        {
            if (rightByName.ContainsKey(r.Name))
            {
                if (differences.Count >= limit)
                {
                    return true;
                }

                differences.Add(new HashTreeDifference(group, r.Name, r.LeafKind, null, null) { OnlyIn = "right" });
            }
        }

        return false;
    }

    private static bool CompareLeaves(string group, HashTreeNode left, HashTreeNode right, List<HashTreeDifference> differences, int limit)
    {
        var kind = left.LeafKind != HashTreeLeafKind.None ? left.LeafKind : right.LeafKind;
        var rightByKey = new Dictionary<HashTreeLeafKey, HashTreeLeaf>(right.Leaves.Count);
        foreach (var leaf in right.Leaves)
        {
            rightByKey[leaf.Key] = leaf;
        }

        foreach (var l in left.Leaves)
        {
            if (differences.Count >= limit)
            {
                return true;
            }

            if (!rightByKey.Remove(l.Key, out var r))
            {
                differences.Add(new HashTreeDifference(group, left.Name, kind, l, null) { OnlyIn = "left" });
            }
            else if (l.Hash != r.Hash)
            {
                differences.Add(new HashTreeDifference(group, left.Name, kind, l, r));
            }
        }

        foreach (var r in right.Leaves)
        {
            if (rightByKey.ContainsKey(r.Key))
            {
                if (differences.Count >= limit)
                {
                    return true;
                }

                differences.Add(new HashTreeDifference(group, left.Name, kind, null, r) { OnlyIn = "right" });
            }
        }

        return false;
    }
}

/// <summary>
/// Receives the nodes of a <see cref="WorldHashTree"/> as <see cref="World.HashTree"/> and the extensions
/// (<see cref="IWorldExtension.HashTree"/>) walk their state: groups hold items, items hold leaves. Inner node
/// hashes are derived from the children, so a caller only computes leaf (or leafless item) hashes.
/// </summary>
public sealed class HashTreeBuilder
{
    private readonly List<HashTreeNode> _groups = [];
    private readonly List<ulong> _hashes = [];
    private List<HashTreeNode>? _items;
    private string? _groupName;
    private List<HashTreeLeaf>? _leaves;
    private string? _itemName;
    private HashTreeLeafKind _leafKind;

    internal HashTreeBuilder()
    {
    }

    /// <summary>Opens a group (a level directly under the root); items go inside until <see cref="EndGroup"/>.</summary>
    public void BeginGroup(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_groupName is not null)
        {
            throw new InvalidOperationException($"Group '{_groupName}' is still open.");
        }

        _groupName = name;
        _items = [];
    }

    /// <summary>Closes the open group; its hash combines the item hashes in order.</summary>
    public void EndGroup()
    {
        if (_groupName is null || _items is null)
        {
            throw new InvalidOperationException("No group is open.");
        }

        if (_itemName is not null)
        {
            throw new InvalidOperationException($"Item '{_itemName}' is still open.");
        }

        _groups.Add(new HashTreeNode(_groupName, Combine(_items), HashTreeLeafKind.None, _items, []));
        _groupName = null;
        _items = null;
    }

    /// <summary>A group with no items whose hash is given (e.g. the Tick).</summary>
    public void AddGroup(string name, ulong hash)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_groupName is not null)
        {
            throw new InvalidOperationException($"Group '{_groupName}' is still open.");
        }

        _groups.Add(new HashTreeNode(name, hash, HashTreeLeafKind.None, [], []));
    }

    /// <summary>Opens an item inside the open group; leaves of <paramref name="kind"/> go inside until <see cref="EndItem"/>.</summary>
    public void BeginItem(string name, HashTreeLeafKind kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        RequireGroup();
        if (_itemName is not null)
        {
            throw new InvalidOperationException($"Item '{_itemName}' is still open.");
        }

        _itemName = name;
        _leafKind = kind;
        _leaves = [];
    }

    /// <summary>Adds one leaf (a chunk) to the open item. Leaves are matched across trees by (ordinal, x, y, z).</summary>
    public void AddLeaf(int ordinal, int x, int y, int z, ulong hash)
    {
        if (_leaves is null)
        {
            throw new InvalidOperationException("No item is open.");
        }

        _leaves.Add(new HashTreeLeaf(ordinal, x, y, z, hash));
    }

    /// <summary>Closes the open item; its hash combines the leaf hashes in order.</summary>
    public void EndItem()
    {
        if (_itemName is null || _leaves is null || _items is null)
        {
            throw new InvalidOperationException("No item is open.");
        }

        _hashes.Clear();
        foreach (var leaf in _leaves)
        {
            _hashes.Add(leaf.Hash);
        }

        _items.Add(new HashTreeNode(_itemName, WorldHashTree.CombineHashes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hashes)), _leafKind, [], _leaves));
        _itemName = null;
        _leaves = null;
    }

    /// <summary>An item with no leaves whose hash is given (a Resource, a table, opaque extension state).</summary>
    public void AddItem(string name, ulong hash)
    {
        ArgumentNullException.ThrowIfNull(name);
        RequireGroup();
        if (_itemName is not null)
        {
            throw new InvalidOperationException($"Item '{_itemName}' is still open.");
        }

        _items!.Add(new HashTreeNode(name, hash, HashTreeLeafKind.None, [], []));
    }

    internal WorldHashTree Build(long tick, ulong schemaHash, ulong worldHash)
    {
        if (_groupName is not null)
        {
            throw new InvalidOperationException($"Group '{_groupName}' is still open.");
        }

        return new WorldHashTree(tick, schemaHash, worldHash, _groups);
    }

    private void RequireGroup()
    {
        if (_groupName is null || _items is null)
        {
            throw new InvalidOperationException("No group is open.");
        }
    }

    private ulong Combine(List<HashTreeNode> nodes)
    {
        _hashes.Clear();
        foreach (var node in nodes)
        {
            _hashes.Add(node.Hash);
        }

        return WorldHashTree.CombineHashes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hashes));
    }
}
