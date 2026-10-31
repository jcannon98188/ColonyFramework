using Colony.Fields;
using Colony.Persistence;

namespace Colony.Diagnostics.Inspection;

/// <summary>The request handlers. Every one runs on the Tick thread at a Tick boundary (see <see cref="Pump"/>).</summary>
public sealed partial class InspectionServer
{
    private World? _rewound;
    private ReplayPlayer? _player;

    /// <summary>The World reads go to: the live World, or the past World a <c>rewind</c> rebuilt.</summary>
    public World InspectedWorld => _rewound ?? World;

    /// <summary>True while reads go to a World rebuilt by <c>rewind</c> rather than the live one.</summary>
    public bool IsRewound => _rewound is not null;

    private static KeyValuePair<string, InspectValue> P(string key, InspectValue value) => new(key, value);

    private InspectValue Dispatch(string op, InspectValue request) => op switch
    {
        "hello" => Hello(),
        "status" => Status(),
        "schema" => Schema(),
        "archetypes" => Archetypes(),
        "entities" => Entities(request),
        "entity" => Entity(request),
        "resources" => Resources(),
        "events" => Events(request),
        "fields" => FieldsList(),
        "field" => FieldCells(request),
        "spatial" => Spatial(request),
        "diagnostics" => DiagnosticsRecord(),
        "inspectors" => InspectorsList(),
        "inspect" => RunInspector(request),
        "pause" => Pause(),
        "resume" => Resume(),
        "step" => Step(request),
        "rewind" => Rewind(request),
        "live" => Live(),
        "mutate" => Mutate(request),
        "snapshot" => Snapshot(),
        "diff" => Diff(request),
        "shutdown" => Shutdown(),
        _ => throw new InspectionException("unknown-op", $"Unknown op '{op}'."),
    };

    private InspectValue Hello() => InspectValue.Map(
        P("protocol", InspectCodec.ProtocolVersion),
        P("tick", World.CurrentTick),
        P("schemaHash", InspectValue.From(World.Schema.SchemaHash)),
        P("allowMutation", Options.AllowMutation),
        P("debugCommands", World.Schema.TryGetDebugCommands(out _)),
        P("replay", Options.Replay is not null),
        P("gate", Gate is not null),
        P("deterministic", World.IsDeterministic));

    private InspectValue Status() => InspectValue.Map(
        P("tick", World.CurrentTick),
        P("paused", Gate?.IsHeld ?? false),
        P("pendingSteps", Gate?.PendingSteps ?? 0),
        P("rewound", _rewound is null ? InspectValue.Null : InspectValue.From(_rewound.CurrentTick)),
        P("entities", World.EntityCount),
        P("recordedThrough", Options.Replay is { } replay ? InspectValue.From(replay.EndTick) : InspectValue.Null),
        P("faulted", World.IsFaulted));

    private InspectValue Schema()
    {
        var schema = World.Schema;
        var components = new List<InspectValue>();
        foreach (ref readonly var c in schema.Components)
        {
            var fields = new List<InspectValue>();
            foreach (var f in c.Fields)
            {
                fields.Add(InspectValue.Map(P("name", f.Name), P("type", f.TypeName), P("offset", f.Offset), P("size", f.Size)));
            }

            int owner = schema.ComponentOwner(c.Type);
            components.Add(InspectValue.Map(
                P("id", c.Type.Id),
                P("name", c.Name),
                P("size", c.Size),
                P("alignment", c.Alignment),
                P("layoutHash", InspectValue.From(c.LayoutHash)),
                P("nonFragmenting", c.IsNonFragmenting),
                P("relation", c.IsRelation),
                P("buffer", c.IsBuffer),
                P("plugin", owner >= 0 ? schema.Plugins[owner].Id : null),
                P("fields", InspectValue.List(fields))));
        }

        var resources = new List<InspectValue>();
        foreach (ref readonly var r in schema.Resources)
        {
            resources.Add(InspectValue.Map(P("id", r.Type.Id), P("name", r.Name), P("size", r.Size), P("kind", r.IsEventQueue ? "event" : "value"), P("persistent", r.IsPersistent)));
        }

        var phases = new List<InspectValue>();
        foreach (var phase in schema.Phases)
        {
            phases.Add(InspectValue.Map(P("id", phase.Id), P("name", phase.Name)));
        }

        var systems = new List<InspectValue>();
        foreach (ref readonly var s in schema.Systems)
        {
            systems.Add(InspectValue.Map(P("id", s.Id), P("name", s.Name), P("phase", s.Phase.Name), P("wave", s.Wave), P("order", s.Order), P("every", s.Every), P("offset", s.Offset)));
        }

        var plugins = new List<InspectValue>();
        foreach (var plugin in schema.Plugins)
        {
            plugins.Add(InspectValue.Map(P("id", plugin.Id), P("version", plugin.Version), P("deterministic", plugin.Deterministic)));
        }

        var inputs = new List<InspectValue>();
        foreach (var input in schema.InputCommands)
        {
            inputs.Add(InspectValue.Map(P("id", input.Type.Id), P("name", input.Name), P("size", input.Size)));
        }

        var extensions = new List<InspectValue>();
        foreach (var extension in schema.Extensions)
        {
            extensions.Add(extension.Name);
        }

        var counters = new List<InspectValue>();
        foreach (string counter in schema.Counters)
        {
            counters.Add(counter);
        }

        return InspectValue.Map(
            P("schemaHash", InspectValue.From(schema.SchemaHash)),
            P("components", InspectValue.List(components)),
            P("resources", InspectValue.List(resources)),
            P("phases", InspectValue.List(phases)),
            P("systems", InspectValue.List(systems)),
            P("plugins", InspectValue.List(plugins)),
            P("inputs", InspectValue.List(inputs)),
            P("extensions", InspectValue.List(extensions)),
            P("counters", InspectValue.List(counters)),
            P("inspectors", InspectorsList()));
    }

    private InspectValue Archetypes()
    {
        var world = InspectedWorld;
        var list = new List<InspectValue>();
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            var (signature, entities, chunks) = world.DescribeArchetype(a);
            list.Add(InspectValue.Map(P("id", a), P("entities", entities), P("chunks", chunks), P("components", ComponentNames(world.Schema, signature))));
        }

        return InspectValue.Map(P("tick", world.CurrentTick), P("archetypes", InspectValue.List(list)));
    }

    /// <summary>Live Entity ids in archetype, chunk, row order: optional <c>archetype</c>, <c>component</c> (name) filter, <c>offset</c>, <c>limit</c>.</summary>
    private InspectValue Entities(InspectValue request)
    {
        var world = InspectedWorld;
        int offset = (int)Optional(request, "offset", 0);
        int limit = (int)Math.Min(Optional(request, "limit", 256), Options.MaxItemsPerReply);
        int only = (int)Optional(request, "archetype", -1);
        ComponentType? filter = request["component"].Kind == InspectValueKind.Text ? ComponentByName(world.Schema, request["component"].AsText).Type : null;
        var ids = new List<InspectValue>();
        long total = 0;
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            if (only >= 0 && a != only)
            {
                continue;
            }

            var (signature, _, chunks) = world.DescribeArchetype(a);
            if (filter is { } type && !signature.Contains(type))
            {
                continue;
            }

            for (int c = 0; c < chunks; c++)
            {
                foreach (var id in world.ArchetypeEntities(a, c))
                {
                    if (total >= offset && ids.Count < limit)
                    {
                        ids.Add(InspectValue.From(id.Value));
                    }

                    total++;
                }
            }
        }

        if (filter is { } nonFragmenting && world.Schema.Describe(nonFragmenting).IsNonFragmenting)
        {
            throw new InspectionException("not-available", "Filtering by a non-fragmenting Component is not supported; list all Entities and read each.");
        }

        return InspectValue.Map(P("tick", world.CurrentTick), P("total", total), P("offset", offset), P("entities", InspectValue.List(ids)));
    }

    /// <summary>One Entity: every Component it has, raw bytes plus field values when the layout is known.</summary>
    private InspectValue Entity(InspectValue request)
    {
        var world = InspectedWorld;
        var id = new EntityId((ulong)Required(request, "entity").AsWhole);
        if (!world.IsAlive(id))
        {
            return InspectValue.Map(P("id", InspectValue.From(id.Value)), P("alive", false));
        }

        var components = new List<InspectValue>();
        foreach (ref readonly var c in world.Schema.Components)
        {
            if (!world.Has(id, c.Type))
            {
                continue;
            }

            var bytes = world.ComponentBytes(id, c.Type);
            components.Add(InspectValue.Map(
                P("name", c.Name),
                P("enabled", world.IsEnabled(id, c.Type)),
                P("bytes", InspectValue.From(bytes)),
                P("value", ComponentValues.Decode(in c, bytes) ?? InspectValue.Null)));
        }

        return InspectValue.Map(P("id", InspectValue.From(id.Value)), P("alive", true), P("tick", world.CurrentTick), P("components", InspectValue.List(components)));
    }

    private InspectValue Resources()
    {
        var world = InspectedWorld;
        var list = new List<InspectValue>();
        foreach (ref readonly var r in world.Schema.Resources)
        {
            list.Add(r.IsEventQueue
                ? InspectValue.Map(P("name", r.Name), P("kind", "event"), P("pending", world.EventCount(r.Type)))
                : InspectValue.Map(P("name", r.Name), P("kind", "value"), P("bytes", InspectValue.From(world.ResourceBytes(r.Type)))));
        }

        return InspectValue.Map(P("tick", world.CurrentTick), P("resources", InspectValue.List(list)));
    }

    /// <summary>The pending events of the queue <c>name</c>, one raw record each, in merge order.</summary>
    private InspectValue Events(InspectValue request)
    {
        var world = InspectedWorld;
        var queue = ResourceByName(world.Schema, Required(request, "name").AsText);
        if (!queue.IsEventQueue)
        {
            throw new InspectionException("bad-request", $"Resource '{queue.Name}' is a value, not an event queue.");
        }

        var bytes = world.EventBytes(queue.Type);
        int count = bytes.Length / Math.Max(1, queue.Size);
        var records = new List<InspectValue>();
        for (int i = 0; i < Math.Min(count, Options.MaxItemsPerReply); i++)
        {
            records.Add(InspectValue.From(bytes.Slice(i * queue.Size, queue.Size)));
        }

        return InspectValue.Map(P("tick", world.CurrentTick), P("name", queue.Name), P("count", count), P("size", queue.Size), P("events", InspectValue.List(records)));
    }

    private InspectValue FieldsList()
    {
        var fields = RequireFields(InspectedWorld);
        var list = new List<InspectValue>();
        foreach (var f in fields.Fields)
        {
            list.Add(InspectValue.Map(P("id", f.Id), P("name", f.Name), P("cellSize", f.CellSize)));
        }

        return InspectValue.Map(
            P("size", InspectValue.List(fields.Size.X, fields.Size.Y, fields.Size.Z)),
            P("chunks", fields.ChunkCount),
            P("fields", InspectValue.List(list)));
    }

    /// <summary>
    /// The cells of Field <c>name</c> in the box <c>x, y, z</c> .. <c>x + w - 1, y + h - 1, z</c> (one z layer), row by
    /// row (x fastest); each cell is its raw bytes, or a whole number when the cells are 1, 2, 4 or 8 bytes and <c>as</c> is "int".
    /// </summary>
    private InspectValue FieldCells(InspectValue request)
    {
        var world = InspectedWorld;
        var fields = RequireFields(world);
        string name = Required(request, "name").AsText;
        int fieldId = -1;
        int cellSize = 0;
        foreach (var f in fields.Fields)
        {
            if (string.Equals(f.Name, name, StringComparison.Ordinal))
            {
                fieldId = f.Id;
                cellSize = f.CellSize;
            }
        }

        if (fieldId < 0)
        {
            throw new InspectionException("bad-request", $"No Field named '{name}'.");
        }

        int x0 = (int)Required(request, "x").AsWhole;
        int y0 = (int)Required(request, "y").AsWhole;
        int z = (int)Optional(request, "z", 0);
        int w = (int)Optional(request, "w", 1);
        int h = (int)Optional(request, "h", 1);
        if (w <= 0 || h <= 0 || (long)w * h > Options.MaxItemsPerReply)
        {
            throw new InspectionException("bad-request", $"A Field read covers 1..{Options.MaxItemsPerReply} cells.");
        }

        bool asInt = request["as"].Kind == InspectValueKind.Text && string.Equals(request["as"].AsText, "int", StringComparison.Ordinal);
        var storage = fields.StorageAt(fieldId);
        var rows = new List<InspectValue>(h);
        for (int y = y0; y < y0 + h; y++)
        {
            var row = new List<InspectValue>(w);
            for (int x = x0; x < x0 + w; x++)
            {
                if (x < 0 || y < 0 || z < 0 || x >= fields.Size.X || y >= fields.Size.Y || z >= fields.Size.Z)
                {
                    row.Add(InspectValue.Null);
                    continue;
                }

                int ordinal = fields.OrdinalOfTile(x, y, z);
                var cell = storage.IsUniform(ordinal)
                    ? storage.UniformValue(ordinal)
                    : storage.ReadCells(ordinal).Slice(FieldChunk.CellIndex(x & (FieldChunk.Size - 1), y & (FieldChunk.Size - 1), z & (FieldChunk.Size - 1)) * cellSize, cellSize);
                row.Add(asInt ? WholeOf(cell) : InspectValue.From(cell));
            }

            rows.Add(InspectValue.List(row));
        }

        return InspectValue.Map(P("tick", world.CurrentTick), P("name", name), P("cellSize", cellSize), P("rows", InspectValue.List(rows)));
    }

    /// <summary>Spatial index queries: <c>kind</c> tile | box | radius | nearest around <c>x, y, z</c> (box: <c>x2, y2, z2</c>; radius: <c>r</c>; nearest: <c>k</c>).</summary>
    private InspectValue Spatial(InspectValue request)
    {
        var world = InspectedWorld;
        if (!world.TryGetExtension<SpatialIndex>(out var index))
        {
            throw new InspectionException("not-available", "The schema registered no spatial index.");
        }

        var at = new TilePos((int)Required(request, "x").AsWhole, (int)Required(request, "y").AsWhole, (int)Optional(request, "z", 0));
        int capacity = (int)Math.Clamp(Optional(request, "k", Options.MaxItemsPerReply), 1, Options.MaxItemsPerReply);
        var hits = new SpatialHit[capacity];
        string kind = Required(request, "kind").AsText;
        int count = kind switch
        {
            "tile" => index.QueryTile(at, hits),
            "radius" => index.QueryRadius(at, (int)Required(request, "r").AsWhole, hits),
            "nearest" => index.QueryNearest(at, hits),
            "box" => index.QueryBox(
                new TileBox(at, new TilePos((int)Required(request, "x2").AsWhole, (int)Required(request, "y2").AsWhole, (int)Optional(request, "z2", at.Z))),
                at,
                hits),
            _ => throw new InspectionException("bad-request", $"Unknown spatial query kind '{kind}' (tile, box, radius, nearest)."),
        };

        var list = new List<InspectValue>(count);
        for (int i = 0; i < count; i++)
        {
            var hit = hits[i];
            list.Add(InspectValue.Map(
                P("entity", InspectValue.From(hit.Entity.Value)),
                P("position", InspectValue.List(hit.Position.X, hit.Position.Y, hit.Position.Z)),
                P("distanceSquared", hit.DistanceSquared)));
        }

        return InspectValue.Map(P("tick", world.CurrentTick), P("kind", kind), P("hits", InspectValue.List(list)));
    }

    /// <summary>The latest Tick record of <c>world.Diagnostics</c> (spec section 10.1): Tick totals, Phases, Systems and counters.</summary>
    private InspectValue DiagnosticsRecord()
    {
        var world = World;
        var diagnostics = world.Diagnostics;
        if (diagnostics.Count == 0)
        {
            return InspectValue.Map(P("tick", world.CurrentTick), P("recorded", false));
        }

        var record = diagnostics.Latest;
        ref readonly var t = ref record.Tick;
        var phases = new List<InspectValue>();
        foreach (ref readonly var p in record.Phases)
        {
            phases.Add(InspectValue.Map(P("name", world.Schema.Phases[p.PhaseId].Name), P("wallTicks", p.WallTicks), P("jobs", p.Jobs), P("commands", p.Commands)));
        }

        var systems = new List<InspectValue>();
        foreach (ref readonly var s in record.Systems)
        {
            if (!s.Ran)
            {
                continue;
            }

            systems.Add(InspectValue.Map(
                P("name", world.Schema.Systems[s.SystemId].Name),
                P("wallTicks", s.WallTicks),
                P("jobs", s.Jobs),
                P("chunksVisited", s.ChunksVisited),
                P("chunksSkipped", s.ChunksSkipped),
                P("commands", s.Commands),
                P("allocatedBytes", s.AllocatedBytes)));
        }

        var counters = new List<KeyValuePair<string, InspectValue>>();
        var names = world.Schema.Counters;
        for (int i = 0; i < Math.Min(names.Length, record.Counters.Length); i++)
        {
            counters.Add(P(names[i], record.Counters[i]));
        }

        return InspectValue.Map(
            P("tick", t.Tick),
            P("recorded", true),
            P("level", t.Level.ToString()),
            P("wallTicks", t.WallTicks),
            P("timestampFrequency", System.Diagnostics.Stopwatch.Frequency),
            P("entities", t.EntityCount),
            P("archetypes", t.ArchetypeCount),
            P("chunksVisited", t.ChunksVisited),
            P("chunksSkipped", t.ChunksSkipped),
            P("commands", t.Commands),
            P("allocatedBytes", t.AllocatedBytes),
            P("reservedBytes", t.ReservedBytes),
            P("slow", t.Slow),
            P("phases", InspectValue.List(phases)),
            P("systems", InspectValue.List(systems)),
            P("counters", InspectValue.Map(counters)));
    }

    private InspectValue InspectorsList()
    {
        var list = new List<InspectValue>();
        foreach (var inspector in World.Schema.Inspectors)
        {
            list.Add(inspector.Name);
        }

        return InspectValue.List(list);
    }

    /// <summary>Runs the inspector <c>name</c> (registered with <c>WorldBuilder.AddInspector</c>, typically by a Plugin in Register).</summary>
    private InspectValue RunInspector(InspectValue request)
    {
        var world = InspectedWorld;
        string name = Required(request, "name").AsText;
        Colony.Diagnostics.InspectorDescriptor? found = null;
        foreach (var inspector in world.Schema.Inspectors)
        {
            if (string.Equals(inspector.Name, name, StringComparison.Ordinal))
            {
                found = inspector;
            }
        }

        if (found is null)
        {
            throw new InspectionException("bad-request", $"No inspector named '{name}'.");
        }

        var output = new InspectorOutput();
        found.Run(world, output);
        var entries = new List<InspectValue>();
        foreach (var entry in output.Entries)
        {
            var value = entry.Kind switch
            {
                InspectorValueKind.Whole => InspectValue.From(entry.Whole),
                InspectorValueKind.Number => InspectValue.From(entry.Number),
                InspectorValueKind.Entity => InspectValue.From(entry.Entity.Value),
                _ => InspectValue.From(entry.Text),
            };
            entries.Add(InspectValue.Map(P("name", entry.Name), P("kind", entry.Kind.ToString()), P("value", value)));
        }

        return InspectValue.Map(P("tick", world.CurrentTick), P("name", name), P("entries", InspectValue.List(entries)));
    }

    private InspectValue Pause()
    {
        RequireGate().Hold();
        return Status();
    }

    private InspectValue Resume()
    {
        EndRewind();
        RequireGate().Release();
        return Status();
    }

    /// <summary>Lets <c>count</c> (1) Ticks through the gate; while rewound, plays the replay forward instead.</summary>
    private InspectValue Step(InspectValue request)
    {
        int count = (int)Optional(request, "count", 1);
        if (count < 1)
        {
            throw new InspectionException("bad-request", "step needs a count of at least 1.");
        }

        if (_player is not null)
        {
            _player.SeekTo(Math.Min(_player.World.CurrentTick + count, _player.Log.EndTick));
            return Status();
        }

        var gate = RequireGate();
        if (!gate.IsHeld)
        {
            throw new InspectionException("bad-request", "The World is running; pause it before stepping.");
        }

        gate.Step(count);
        return Status();
    }

    /// <summary>
    /// Replay-based rewind: pauses the live World, rebuilds the World at <c>tick</c> from the host's replay log
    /// (nearest embedded save, then replay forward with checkpoint verification) and points reads at it until
    /// <c>live</c> or <c>resume</c>. The live World is never modified.
    /// </summary>
    private InspectValue Rewind(InspectValue request)
    {
        var replay = Options.Replay ?? throw new InspectionException("not-available", "The host records no replay; pass InspectionServerOptions.Replay to enable rewind.");
        long tick = Required(request, "tick").AsWhole;
        if (tick < replay.StartTick || tick > replay.EndTick)
        {
            throw new InspectionException("bad-request", $"The replay covers Ticks {replay.StartTick}..{replay.EndTick}.");
        }

        Gate?.Hold();
        EndRewind();
        var log = replay.Finish();
        var world = log.CreateWorld(tick, World.Schema, new LoadOptions { WorldOptions = new WorldOptions { WorkerThreads = 1 } });
        try
        {
            var player = new ReplayPlayer(log, world);
            player.SeekTo(tick);
            _rewound = world;
            _player = player;
        }
        catch
        {
            world.Dispose();
            throw;
        }

        return InspectValue.Map(P("tick", _rewound.CurrentTick), P("hash", InspectValue.From(_rewound.Hash())), P("liveTick", World.CurrentTick));
    }

    private InspectValue Live()
    {
        EndRewind();
        return Status();
    }

    private void EndRewind()
    {
        _player = null;
        _rewound?.Dispose();
        _rewound = null;
    }

    /// <summary>
    /// Submits a debug command (<c>action</c> create | destroy | add | remove; <c>entity</c>, <c>component</c> by
    /// name, <c>bytes</c> for add, <c>components</c> list for create) as an Input Command for the next Tick.
    /// Requires <see cref="InspectionServerOptions.AllowMutation"/> and <see cref="DebugCommands.AddDebugCommands"/> in the schema.
    /// </summary>
    private InspectValue Mutate(InspectValue request)
    {
        if (!Options.AllowMutation)
        {
            throw new InspectionException("forbidden", "The server is read-only; the host must set InspectionServerOptions.AllowMutation.");
        }

        if (_rewound is not null)
        {
            throw new InspectionException("forbidden", "Reads point at a rewound World; return to 'live' before mutating.");
        }

        var schema = World.Schema;
        if (!schema.TryGetDebugCommands(out var type))
        {
            throw new InspectionException("not-available", "The schema has no debug commands; the Game must call WorldBuilder.AddDebugCommands().");
        }

        string action = Required(request, "action").AsText;
        DebugCommand command;
        switch (action)
        {
            case "create":
                var names = Required(request, "components").Items;
                var types = new ComponentType[names.Count];
                for (int i = 0; i < types.Length; i++)
                {
                    types[i] = ComponentByName(schema, names[i].AsText).Type;
                }

                command = DebugCommands.Create(types);
                break;
            case "destroy":
                command = DebugCommands.Destroy(new EntityId((ulong)Required(request, "entity").AsWhole));
                break;
            case "add":
                var component = ComponentByName(schema, Required(request, "component").AsText);
                var value = Required(request, "bytes").AsBytes;
                if (value.Length != component.Size)
                {
                    throw new InspectionException("bad-request", $"Component '{component.Name}' is {component.Size} bytes; got {value.Length}.");
                }

                command = DebugCommands.Add(new EntityId((ulong)Required(request, "entity").AsWhole), component.Type, value);
                break;
            case "remove":
                command = DebugCommands.Remove(new EntityId((ulong)Required(request, "entity").AsWhole), ComponentByName(schema, Required(request, "component").AsText).Type);
                break;
            default:
                throw new InspectionException("bad-request", $"Unknown mutate action '{action}' (create, destroy, add, remove).");
        }

        long tick = World.Inputs.NextTick;
        uint sequence = World.Inputs.Submit(DebugCommands.Source, in command, tick);
        return InspectValue.Map(P("tick", tick), P("sequence", (long)sequence));
    }

    /// <summary>A save of the inspected World (container bytes), e.g. for an offline diff.</summary>
    private InspectValue Snapshot()
    {
        var world = InspectedWorld;
        return InspectValue.Map(P("tick", world.CurrentTick), P("save", InspectValue.From(world.SaveToBytes())));
    }

    /// <summary>Compares the inspected World with the save in <c>save</c> (bytes), loaded with this World's schema.</summary>
    private InspectValue Diff(InspectValue request)
    {
        var image = Required(request, "save").AsBytes.ToArray();
        using var other = WorldPersistence.Load(image, World.Schema, new LoadOptions { VerifyWorldHash = false, WorldOptions = new WorldOptions { WorkerThreads = 1 } });
        var report = WorldDiff.Compare(InspectedWorld, other, (int)Optional(request, "limit", 64));
        return report.ToValue();
    }

    private InspectValue Shutdown()
    {
        ShutdownRequested = true;
        _signal.Release();
        return InspectValue.Map(P("shutdown", true));
    }

    private TickGate RequireGate()
        => Gate ?? throw new InspectionException("not-available", "The host started the World without a TickGate; pause and step need one (WorldOptions.TickGate or InspectionServerOptions.TickGate).");

    private static FieldSet RequireFields(World world)
        => world.TryGetExtension<FieldSet>(out var fields) ? fields : throw new InspectionException("not-available", "The schema registered no Fields.");

    private static InspectValue Required(InspectValue request, string key)
    {
        var value = request[key];
        return value.IsNull ? throw new InspectionException("bad-request", $"Missing argument '{key}'.") : value;
    }

    private static long Optional(InspectValue request, string key, long fallback)
        => request[key].IsNull ? fallback : request[key].AsWhole;

    private static ComponentDescriptor ComponentByName(WorldSchema schema, string name)
    {
        foreach (ref readonly var c in schema.Components)
        {
            if (string.Equals(c.Name, name, StringComparison.Ordinal))
            {
                return c;
            }
        }

        throw new InspectionException("bad-request", $"No Component named '{name}'.");
    }

    private static ResourceDescriptor ResourceByName(WorldSchema schema, string name)
    {
        foreach (ref readonly var r in schema.Resources)
        {
            if (string.Equals(r.Name, name, StringComparison.Ordinal))
            {
                return r;
            }
        }

        throw new InspectionException("bad-request", $"No Resource named '{name}'.");
    }

    internal static InspectValue ComponentNames(WorldSchema schema, in ComponentMask signature)
    {
        var names = new List<InspectValue>();
        foreach (ref readonly var c in schema.Components)
        {
            if (signature.Contains(c.Type))
            {
                names.Add(c.Name);
            }
        }

        return InspectValue.List(names);
    }

    private static InspectValue WholeOf(ReadOnlySpan<byte> cell) => cell.Length switch
    {
        1 => InspectValue.From((long)cell[0]),
        2 => InspectValue.From((long)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(cell)),
        4 => InspectValue.From((long)System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(cell)),
        8 => InspectValue.From(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(cell)),
        _ => InspectValue.From(cell),
    };
}
