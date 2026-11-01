using Colony.Diagnostics.Inspection;
using Colony.Fields;

namespace Colony.Diagnostics.Tests.Inspection;

/// <summary>
/// TASK-32: the hash tree on the wire (file and inspection protocol) and <c>colony-inspect desync</c> over two
/// trees or two live hosts, localising a perturbed chunk exactly.
/// </summary>
public sealed class HashTreeDesyncTests
{
    private static KeyValuePair<string, InspectValue> P(string key, InspectValue value) => new(key, value);

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = InspectCli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void A_hash_tree_file_round_trips_and_compares_identical_with_the_live_tree()
    {
        using var fx = InspectFixture.Create(entities: 4);
        fx.Run(2);
        var tree = fx.World.HashTree();
        var file = HashTreeFile.From(tree);

        var decoded = HashTreeFile.Decode(file.Encode()).Tree;

        Assert.Equal(tree.WorldHash, decoded.WorldHash);
        Assert.Equal(tree.Tick, decoded.Tick);
        Assert.Equal(tree.Groups.Select(g => g.Name), decoded.Groups.Select(g => g.Name));
        Assert.Equal(tree.Group("fields")!.Child("temperature")!.Leaves, decoded.Group("fields")!.Child("temperature")!.Leaves);
        Assert.True(WorldHashTree.Compare(tree, decoded).Identical);
        Assert.Throws<FormatException>(() => HashTreeFile.Decode(InspectCodec.Encode(InspectValue.Map(P("format", "nope")))));
    }

    [Fact]
    public void Two_live_hosts_are_localised_level_by_level_exchanging_only_hashes()
    {
        using var a = InspectFixture.Create(entities: 4);
        using var b = InspectFixture.Create(entities: 4);
        a.Run(2);
        b.Run(2);
        using var clientA = a.Server.Connect(InspectFixture.Token);
        using var clientB = b.Server.Connect(InspectFixture.Token);

        var root = clientA.Request("hashtree");
        Assert.Equal(InspectValue.From(a.World.Hash()).AsWhole, root["hash"].AsWhole);
        Assert.Contains(root["groups"].Items, g => g["name"].AsText == "fields");
        var group = clientA.Request("hashtree", P("group", "fields"));
        Assert.Equal("temperature", Assert.Single(group["items"].Items)["name"].AsText);
        var item = clientA.Request("hashtree", P("group", "fields"), P("item", "temperature"));
        Assert.Equal(4 * HashTreeFile.LeafBytes, item["leaves"].AsBytes.Length); // 32 x 32 tiles = 2 x 2 chunks

        var same = HashTreeFile.CompareLive(clientA, clientB);
        Assert.True(same.Identical);

        // Negative control: perturb one Field cell in chunk (1, 0, 0) of b between Ticks.
        var temperature = b.Schema.Extension<FieldsBuilder>().Field<short>("temperature");
        b.World.Fields().Accessor(temperature)[20, 3, 0] = 99;

        var comparison = HashTreeFile.CompareLive(clientA, clientB);

        Assert.False(comparison.Identical);
        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("fields/temperature chunk (1, 0, 0)", difference.Describe());
    }

    [Fact]
    public void Colony_inspect_desync_reports_the_diverging_chunk_for_two_tree_files_and_two_live_hosts()
    {
        using var a = InspectFixture.Create(entities: 4);
        using var b = InspectFixture.Create(entities: 4);
        a.Run(1);
        b.Run(1);
        var vitals = a.Schema.Component<Vitals>();
        var victim = WorldDiffTestHelpers.FirstEntity(b.World);
        b.World.Get<Vitals>(victim, vitals).Health = 77;

        string dir = Directory.CreateTempSubdirectory("colony-hashtree-").FullName;
        try
        {
            string left = Path.Combine(dir, "a.tree");
            string right = Path.Combine(dir, "b.tree");
            HashTreeFile.From(a.World.HashTree()).WriteFile(left);
            HashTreeFile.From(b.World.HashTree()).WriteFile(right);

            var offline = Cli("desync", left, right);
            Assert.Equal(1, offline.Exit);
            Assert.Contains("components/vitals archetype 0 chunk 0", offline.Out, StringComparison.Ordinal);
            Assert.Equal(0, Cli("desync", left, left).Exit);

            var json = InspectJson.Read(Cli("desync", left, right, "--json").Out);
            Assert.True(json["diverged"].AsBoolean);
            var d = Assert.Single(json["differences"].Items);
            Assert.Equal("components", d["group"].AsText);
            Assert.Equal("vitals", d["item"].AsText);
            Assert.Equal(0, d["leaf"]["x"].AsWhole);

            // Live: two hosts over loopback TCP, walked level by level; and a tree saved from a host.
            a.Gate.Hold();
            b.Gate.Hold();
            a.Server.ListenTcp();
            b.Server.ListenTcp();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var hostA = a.StartHostLoop(cts.Token);
            var hostB = b.StartHostLoop(cts.Token);
            string portA = a.Server.Endpoint!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string portB = b.Server.Endpoint!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var live = Cli("desync", "--port", portA, "--token", InspectFixture.Token, "--port2", portB, "--token2", InspectFixture.Token);
            Assert.Equal(1, live.Exit);
            Assert.Contains("components/vitals archetype 0 chunk 0", live.Out, StringComparison.Ordinal);

            string saved = Path.Combine(dir, "live-a.tree");
            Assert.Equal(0, Cli("hashtree", saved, "--port", portA, "--token", InspectFixture.Token).Exit);
            Assert.Equal(0, Cli("desync", saved, left).Exit);
            var mixed = Cli("desync", saved, "--port", portB, "--token", InspectFixture.Token);
            Assert.Equal(1, mixed.Exit);
            Assert.Contains("components/vitals", mixed.Out, StringComparison.Ordinal);

            Assert.Equal(0, Cli("shutdown", "--port", portA, "--token", InspectFixture.Token).Exit);
            Assert.Equal(0, Cli("shutdown", "--port", portB, "--token", InspectFixture.Token).Exit);
            Assert.True(hostA.Join(TimeSpan.FromSeconds(30)));
            Assert.True(hostB.Join(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
