using System.Globalization;
using Colony.Persistence;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// <c>colony-inspect</c> (spec section 10.2), the command-line client of the inspection protocol. The executable in
/// tools/Colony.Inspect only calls <see cref="Run"/>. Server commands connect to a host on this machine with
/// <c>--port</c> and <c>--token</c> (or the <c>COLONY_INSPECT_TOKEN</c> environment variable); <c>desync</c> and the
/// two-file <c>diff</c> work offline. <c>--json</c> prints the protocol's JSON mapping instead of text.
/// Exit codes: 0 success, 1 a comparison found differences, 2 bad usage, 3 the server refused or failed the request.
/// </summary>
public static class InspectCli
{
    public const string TokenVariable = "COLONY_INSPECT_TOKEN";

    public const string Usage = """
        usage: colony-inspect <command> [arguments] [--port N] [--token T] [--json]

        server commands (need --port and --token or COLONY_INSPECT_TOKEN):
          status | schema | archetypes | resources | fields | diagnostics | inspectors
          entities [--component NAME] [--archetype N] [--offset N] [--limit N]
          entity ID                         every Component of one Entity, raw and decoded
          events QUEUE                      pending events of an event queue
          field NAME X Y [--w N] [--h N] [--z N] [--int]
          spatial tile|nearest X Y [--z N] [--k N] | radius X Y R | box X Y X2 Y2
          inspect NAME                      run a registered (e.g. Plugin) inspector
          pause | resume | step [N] | rewind TICK | live
          mutate create COMPONENT... | destroy ID | add ID COMPONENT HEX | remove ID COMPONENT
          snapshot OUT.save                 save the inspected World to a file
          diff SAVE                         the inspected World against a save, Entity by Entity
          raw JSON                          send one request map as JSON
          shutdown                          ask the host to stop
        offline commands:
          diff A.save B.save                compare two save manifests
          desync A.trail B.trail            first diverging Tick, Phase and System of two hash trails
        """;

    private static KeyValuePair<string, InspectValue> P(string key, InspectValue value) => new(key, value);

    public static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--json" or "--int" or "--help" or "-h")
            {
                flags.Add(a);
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    error.WriteLine($"Option {a} needs a value.");
                    return 2;
                }

                named[a[2..]] = args[++i];
            }
            else
            {
                positional.Add(a);
            }
        }

        if (positional.Count == 0 || flags.Contains("--help") || flags.Contains("-h"))
        {
            (positional.Count == 0 ? error : output).WriteLine(Usage);
            return positional.Count == 0 ? 2 : 0;
        }

        bool json = flags.Contains("--json");
        string command = positional[0];
        var rest = positional.GetRange(1, positional.Count - 1);
        try
        {
            switch (command)
            {
                case "desync":
                    return Desync(rest, output, json);
                case "diff" when rest.Count == 2:
                    return OfflineDiff(rest, output, json);
            }

            var request = BuildRequest(command, rest, named, flags);
            using var client = Connect(named);
            var result = client.Request(request);
            if (command == "snapshot")
            {
                File.WriteAllBytes(rest[0], result["save"].AsBytes.ToArray());
                result = InspectValue.Map(P("tick", result["tick"]), P("path", Path.GetFullPath(rest[0])), P("bytes", result["save"].AsBytes.Length));
            }

            Print(output, result, json);
            return command == "diff" && !result["identical"].AsBoolean ? 1 : 0;
        }
        catch (UsageException e)
        {
            error.WriteLine(e.Message);
            error.WriteLine("run colony-inspect --help for the command list");
            return 2;
        }
        catch (InspectionException e)
        {
            error.WriteLine($"error ({e.Code}): {e.Message}");
            return 3;
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or FormatException or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"error: {e.Message}");
            return 3;
        }
    }

    private static InspectionClient Connect(Dictionary<string, string> named)
    {
        if (!named.TryGetValue("port", out string? portText) || !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
        {
            throw new UsageException("Server commands need --port N.");
        }

        string? token = named.TryGetValue("token", out string? t) ? t : Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrEmpty(token))
        {
            throw new UsageException($"Server commands need --token T or the {TokenVariable} environment variable.");
        }

        return InspectionClient.ConnectTcp(port, token);
    }

    /// <summary>The request map for a server command (internal for tests).</summary>
    internal static InspectValue BuildRequest(string command, List<string> args, Dictionary<string, string> named, HashSet<string> flags)
    {
        var entries = new List<KeyValuePair<string, InspectValue>> { P("op", command) };
        void Need(int count, string shape)
        {
            if (args.Count < count)
            {
                throw new UsageException($"usage: colony-inspect {command} {shape}");
            }
        }

        void Option(string name, string? key = null)
        {
            if (named.TryGetValue(name, out string? v))
            {
                entries.Add(P(key ?? name, Whole(v)));
            }
        }

        switch (command)
        {
            case "status" or "schema" or "archetypes" or "resources" or "fields" or "diagnostics" or "inspectors" or "pause" or "resume" or "live" or "shutdown":
                break;
            case "entities":
                if (named.TryGetValue("component", out string? component))
                {
                    entries.Add(P("component", component));
                }

                Option("archetype");
                Option("offset");
                Option("limit");
                break;
            case "entity":
                Need(1, "ID");
                entries.Add(P("entity", Whole(args[0])));
                break;
            case "events":
                Need(1, "QUEUE");
                entries.Add(P("name", args[0]));
                break;
            case "field":
                Need(3, "NAME X Y [--w N] [--h N] [--z N] [--int]");
                entries.Add(P("name", args[0]));
                entries.Add(P("x", Whole(args[1])));
                entries.Add(P("y", Whole(args[2])));
                Option("w");
                Option("h");
                Option("z");
                if (flags.Contains("--int"))
                {
                    entries.Add(P("as", "int"));
                }

                break;
            case "spatial":
                Need(3, "tile|nearest|radius|box X Y ...");
                entries.Add(P("kind", args[0]));
                entries.Add(P("x", Whole(args[1])));
                entries.Add(P("y", Whole(args[2])));
                if (args[0] == "radius")
                {
                    Need(4, "radius X Y R");
                    entries.Add(P("r", Whole(args[3])));
                }
                else if (args[0] == "box")
                {
                    Need(5, "box X Y X2 Y2");
                    entries.Add(P("x2", Whole(args[3])));
                    entries.Add(P("y2", Whole(args[4])));
                }

                Option("z");
                Option("k");
                break;
            case "inspect":
                Need(1, "NAME");
                entries.Add(P("name", args[0]));
                break;
            case "step":
                entries.Add(P("count", args.Count > 0 ? Whole(args[0]) : 1));
                break;
            case "rewind":
                Need(1, "TICK");
                entries.Add(P("tick", Whole(args[0])));
                break;
            case "mutate":
                Need(1, "create|destroy|add|remove ...");
                entries.Add(P("action", args[0]));
                switch (args[0])
                {
                    case "create":
                        entries.Add(P("components", InspectValue.List(args.Skip(1).Select(a => (InspectValue)a))));
                        break;
                    case "destroy":
                        Need(2, "destroy ID");
                        entries.Add(P("entity", Whole(args[1])));
                        break;
                    case "add":
                        Need(4, "add ID COMPONENT HEX");
                        entries.Add(P("entity", Whole(args[1])));
                        entries.Add(P("component", args[2]));
                        entries.Add(P("bytes", InspectValue.From(Convert.FromHexString(args[3]))));
                        break;
                    case "remove":
                        Need(3, "remove ID COMPONENT");
                        entries.Add(P("entity", Whole(args[1])));
                        entries.Add(P("component", args[2]));
                        break;
                    default:
                        throw new UsageException($"Unknown mutate action '{args[0]}' (create, destroy, add, remove).");
                }

                break;
            case "snapshot":
                Need(1, "OUT.save");
                break;
            case "diff":
                Need(1, "SAVE (or two saves for an offline diff)");
                entries.Add(P("save", InspectValue.From(File.ReadAllBytes(args[0]))));
                break;
            case "raw":
                Need(1, "JSON");
                var raw = InspectJson.Read(args[0]);
                if (raw.Kind != InspectValueKind.Map || raw["op"].Kind != InspectValueKind.Text)
                {
                    throw new UsageException("raw needs a JSON object with an \"op\".");
                }

                return raw;
            default:
                throw new UsageException($"Unknown command '{command}'.");
        }

        return InspectValue.Map(entries);
    }

    private static int Desync(List<string> args, TextWriter output, bool json)
    {
        if (args.Count != 2)
        {
            throw new UsageException("usage: colony-inspect desync A.trail B.trail");
        }

        var comparison = HashTrailFile.Compare(HashTrailFile.ReadFile(args[0]), HashTrailFile.ReadFile(args[1]));
        if (json)
        {
            var d = comparison.Divergence;
            Print(output, InspectValue.Map(
                P("diverged", comparison.Diverged),
                P("comparedTicks", comparison.ComparedTicks),
                P("tick", d is { } x ? InspectValue.From(x.Tick) : InspectValue.Null),
                P("phaseId", d is { } y ? InspectValue.From(y.PhaseId) : InspectValue.Null),
                P("systemId", d is { } z ? InspectValue.From(z.SystemId) : InspectValue.Null),
                P("archetype", d is { } w ? InspectValue.From(w.Archetype) : InspectValue.Null),
                P("description", comparison.Description)), json: true);
        }
        else
        {
            output.WriteLine(comparison.Diverged ? "diverged: " + comparison.Description : comparison.Description);
        }

        return comparison.Diverged ? 1 : 0;
    }

    private static int OfflineDiff(List<string> args, TextWriter output, bool json)
    {
        var diff = SaveDiff.CompareManifests(WorldPersistence.ReadManifest(args[0]), WorldPersistence.ReadManifest(args[1]));
        Print(output, diff, json);
        return diff["identical"].AsBoolean ? 0 : 1;
    }

    private static InspectValue Whole(string text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? InspectValue.From(v)
            : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong u) ? InspectValue.From(u)
            : throw new UsageException($"'{text}' is not a whole number.");

    /// <summary>Prints a result as JSON, or as indented <c>key: value</c> text (lists of scalars on one line).</summary>
    public static void Print(TextWriter output, InspectValue value, bool json)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);
        if (json)
        {
            output.WriteLine(InspectJson.Write(value, indented: true));
            return;
        }

        Text(output, value, 0);
    }

    private static void Text(TextWriter output, InspectValue value, int indent)
    {
        string pad = new(' ', indent * 2);
        switch (value.Kind)
        {
            case InspectValueKind.Map:
                foreach (var (key, item) in value.Entries)
                {
                    if (IsInline(item))
                    {
                        output.WriteLine($"{pad}{key}: {Scalar(item)}");
                    }
                    else
                    {
                        output.WriteLine($"{pad}{key}:");
                        Text(output, item, indent + 1);
                    }
                }

                break;
            case InspectValueKind.List:
                foreach (var item in value.Items)
                {
                    if (IsInline(item))
                    {
                        output.WriteLine($"{pad}- {Scalar(item)}");
                    }
                    else
                    {
                        output.WriteLine($"{pad}-");
                        Text(output, item, indent + 1);
                    }
                }

                break;
            default:
                output.WriteLine(pad + Scalar(value));
                break;
        }
    }

    private static bool IsInline(InspectValue value)
        => value.Kind is not (InspectValueKind.Map or InspectValueKind.List)
            || (value.Kind == InspectValueKind.List && value.Items.All(i => i.Kind is not (InspectValueKind.Map or InspectValueKind.List)))
            || (value.Kind == InspectValueKind.Map && value.Entries.Count == 0);

    private static string Scalar(InspectValue value) => value.Kind switch
    {
        InspectValueKind.Null => "-",
        InspectValueKind.Boolean => value.AsBoolean ? "true" : "false",
        InspectValueKind.Whole => value.AsWhole.ToString(CultureInfo.InvariantCulture),
        InspectValueKind.Number => value.AsNumber.ToString("R", CultureInfo.InvariantCulture),
        InspectValueKind.Text => value.AsText,
        InspectValueKind.Bytes => Convert.ToHexStringLower(value.AsBytes),
        InspectValueKind.List => "[" + string.Join(", ", value.Items.Select(Scalar)) + "]",
        _ => "{}",
    };

    private sealed class UsageException(string message) : Exception(message);
}
