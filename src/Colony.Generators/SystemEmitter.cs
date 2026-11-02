using System.Text;

namespace Colony.Generators;

/// <summary>
/// Emits, per partial type with <c>[System]</c> methods: <c>AddSystems(WorldBuilder)</c>, and for each System a
/// <c>{Method}System(WorldBuilder)</c> factory returning its <c>SystemDefinition</c> (spec section 4.3: static entry,
/// declared access set, ordering metadata), a <c>{Method}Query</c> ref struct with typed columns and per-row ref
/// access (spec section 4.1), and for per-Entity Systems the <c>{Method}Chunk</c> entry that runs the chunk loop.
/// </summary>
internal static class SystemEmitter
{
    private const string Builder = "global::Colony.WorldBuilder";
    private const string Context = "global::Colony.SystemContext";
    private const string ChunkView = "global::Colony.ChunkView";

    public static string HintName(EquatableArray<SystemMethodModel> group) => group[0].Placement.HintPrefix + ".Systems.g.cs";

    public static string Emit(EquatableArray<SystemMethodModel> group)
    {
        var first = group[0];
        var w = new SourceWriter();
        int opened = w.OpenPlacement(first.Placement, $"[System] methods of {first.OwnerFullName.Substring("global::".Length)}");
        w.Open($"partial {first.Owner.Keyword} {first.Owner.Name}");

        w.Line($"/// <summary>Registers every generated System of this type with <paramref name=\"builder\"/>; its Components must already be registered.</summary>");
        w.Open($"public static {Builder} AddSystems({Builder} builder)");
        w.Line("if (builder is null) { throw new global::System.ArgumentNullException(nameof(builder)); }");
        foreach (var method in group)
        {
            w.Line($"builder.AddSystem({method.System.MethodName}System(builder));");
        }

        w.Line("return builder;");
        w.Close();

        foreach (var method in group)
        {
            EmitSystem(w, method.System);
        }

        w.Close();
        w.CloseAll(opened);
        return w.ToString();
    }

    private static void EmitSystem(SourceWriter w, SystemModel system)
    {
        string m = system.MethodName;
        var types = system.AllTypes().ToList();

        w.Line();
        w.Line($"/// <summary>The <see cref=\"global::Colony.SystemDefinition\"/> of System <c>{system.SystemName}</c> (<see cref=\"{m}\"/>): Phase {system.PhaseName}, every {system.Every} Tick(s), writes [{Names(system.Writes)}], reads [{Names(system.Reads)}].</summary>");
        w.Open($"public static global::Colony.SystemDefinition {m}System({Builder} builder)");
        w.Line("if (builder is null) { throw new global::System.ArgumentNullException(nameof(builder)); }");
        foreach (var type in types)
        {
            w.Line($"global::Colony.ComponentType {type.LocalName} = builder.Component<{type.TypeCode}>();");
        }

        var resources = system.AllResources().ToList();
        var resourceLocals = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < resources.Count; i++)
        {
            string local = resources[i].LocalName + (resources.Count(r => r.LocalName == resources[i].LocalName) > 1 ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
            resourceLocals[resources[i].TypeCode] = local;
            w.Line($"global::Colony.ResourceType {local} = builder.Resource<{resources[i].TypeCode}>();");
        }

        w.Open("return new global::Colony.SystemDefinition");
        w.Line($"Name = {SymbolHelpers.Literal(system.SystemName)},");
        w.Line($"Phase = builder.PhaseNamed({SymbolHelpers.Literal(system.PhaseName)}),");
        w.Line($"All = {Mask(system.With)},");
        w.Line($"None = {Mask(system.Without)},");
        w.Line($"Reads = {Mask(system.Reads)},");
        w.Line($"Writes = {Mask(system.Writes)},");
        w.Line($"Every = {system.Every},");
        w.Line($"Offset = {system.Offset},");
        w.Line($"After = {StringArray(system.After)},");
        w.Line($"Before = {StringArray(system.Before)},");
        w.Line($"ChangeFilter = {Mask(system.ChangeFilter)},");
        w.Line($"SkipAsleepChunks = {(system.SkipAsleepChunks ? "true" : "false")},");
        if (system.ReadsResources.Count > 0)
        {
            w.Line($"ReadsResources = {ResourceMask(system.ReadsResources, resourceLocals)},");
        }

        if (system.WritesResources.Count > 0)
        {
            w.Line($"WritesResources = {ResourceMask(system.WritesResources, resourceLocals)},");
        }

        if (system.IgnoreEnableBits)
        {
            w.Line("IgnoreEnableBits = true,");
        }

        w.Line(Entry(system));
        w.Close(";");
        w.Close();

        if (system.HasQuery)
        {
            EmitQuery(w, system);
        }

        if (system.Kind == SystemKind.PerEntity)
        {
            EmitChunkEntry(w, system);
        }
    }

    /// <summary>The hook assignment: the generated chunk entry, the user's method itself, or an adapter lambda.</summary>
    private static string Entry(SystemModel system)
    {
        string m = system.MethodName;
        string query = $"new {m}Query(in chunk{string.Concat(system.Columns.Select(c => ", " + c.LocalName))})";
        switch (system.Kind)
        {
            case SystemKind.PerEntity:
                return $"ForEachChunk = (in {Context} context, in {ChunkView} chunk) => {m}Chunk(in context, {query}),";

            case SystemKind.TypedQuery:
                return $"ForEachChunk = (in {Context} context, in {ChunkView} chunk) => {m}({Arguments(system, query)}),";

            case SystemKind.Chunk:
                if (system.Parameters.Count == 2 && system.Parameters[0].Kind == ParameterKind.Context && system.Parameters[1].Kind == ParameterKind.ChunkView
                    && system.Parameters[0].IsIn() && system.Parameters[1].IsIn())
                {
                    return $"ForEachChunk = {m},";
                }

                return $"ForEachChunk = (in {Context} context, in {ChunkView} chunk) => {m}({Arguments(system, "in chunk")}),";

            default:
                if (system.Parameters.Count == 1 && system.Parameters[0].IsIn())
                {
                    return $"Begin = {m},";
                }

                return $"Begin = (in {Context} context) => {m}({Arguments(system, string.Empty)}),";
        }
    }

    /// <summary>Arguments for a chunk-level or serial call, in the user's parameter order.</summary>
    private static string Arguments(SystemModel system, string chunkArgument)
    {
        var args = new List<string>(system.Parameters.Count);
        foreach (var parameter in system.Parameters)
        {
            args.Add(parameter.Kind switch
            {
                ParameterKind.Context => parameter.IsIn() ? "in context" : "context",
                ParameterKind.ChunkView => parameter.IsIn() ? "in chunk" : "chunk",
                _ => chunkArgument,
            });
        }

        return string.Join(", ", args);
    }

    private static void EmitQuery(SourceWriter w, SystemModel system)
    {
        string m = system.MethodName;
        w.Line();
        w.Line($"/// <summary>Typed query of <see cref=\"{m}\"/> over one chunk: its declared columns as spans, with per-Entity ref access on top (spec section 4.1).</summary>");
        w.Open($"public readonly ref struct {m}Query");
        w.Line($"private readonly {ChunkView} _chunk;");
        w.Line();
        var ctorParameters = new StringBuilder($"in {ChunkView} chunk");
        foreach (var column in system.Columns)
        {
            ctorParameters.Append($", global::Colony.ComponentType {column.LocalName}");
        }

        w.Open($"public {m}Query({ctorParameters})");
        w.Line("_chunk = chunk;");
        foreach (var column in system.Columns)
        {
            w.Line($"{column.PropertyName} = chunk.Column<{column.TypeCode}>({column.LocalName});");
        }

        w.Close();
        w.Line();
        w.Line("/// <summary>Binds the query to a chunk by schema lookup; the generated entry passes the Component types directly instead.</summary>");
        w.Open($"public {m}Query(in {ChunkView} chunk, global::Colony.WorldSchema schema)", $": this(in chunk{string.Concat(system.Columns.Select(c => $", schema.Component<{c.TypeCode}>()"))})");
        w.Close();
        w.Line();
        w.Line("/// <summary>The underlying chunk view (asleep bit, signature, padded columns).</summary>");
        w.Line($"public {ChunkView} Chunk => _chunk;");
        w.Line();
        w.Line("/// <summary>Live rows in the chunk.</summary>");
        w.Line("public int Count => _chunk.Count;");
        w.Line();
        w.Line("/// <summary>The Entity id of every live row.</summary>");
        w.Line("public global::System.ReadOnlySpan<global::Colony.EntityId> Entities => _chunk.Entities;");
        foreach (var column in system.Columns)
        {
            string span = column.IsWrite ? $"global::System.Span<{column.TypeCode}>" : $"global::System.ReadOnlySpan<{column.TypeCode}>";
            string refKind = column.IsWrite ? "ref" : "ref readonly";
            w.Line();
            w.Line($"/// <summary>The {column.PropertyName} column ({(column.IsWrite ? "written" : "read")}).</summary>");
            w.Line($"public {span} {column.PropertyName} {{ get; }}");
            w.Line();
            w.Line($"/// <summary>The {column.PropertyName} of the Entity in <paramref name=\"row\"/>.</summary>");
            w.Line($"public {refKind} {column.TypeCode} {column.PropertyName}At(int row) => ref {column.PropertyName}[row];");
        }

        w.Close();
    }

    private static void EmitChunkEntry(SourceWriter w, SystemModel system)
    {
        string m = system.MethodName;
        w.Line();
        w.Line($"/// <summary>Chunk entry of <see cref=\"{m}\"/>: calls it once per Entity of the chunk, in row order.</summary>");
        w.Open($"public static void {m}Chunk(in {Context} context, in {m}Query query)");
        bool needsEntities = system.Parameters.Any(p => p.Kind == ParameterKind.EntityId);
        foreach (var column in system.Columns)
        {
            w.Line($"var {Local(column)} = query.{column.PropertyName};");
        }

        if (needsEntities)
        {
            w.Line("var entities = query.Entities;");
        }

        w.Line("int count = query.Count;");
        w.Open("for (int row = 0; row < count; row++)");
        var args = new List<string>(system.Parameters.Count);
        foreach (var parameter in system.Parameters)
        {
            switch (parameter.Kind)
            {
                case ParameterKind.Component:
                    var column = system.Columns.First(c => c.TypeCode == parameter.TypeCode);
                    args.Add((parameter.IsWrite ? "ref " : "in ") + Local(column) + "[row]");
                    break;
                case ParameterKind.EntityId:
                    args.Add((parameter.IsIn() ? "in " : string.Empty) + "entities[row]");
                    break;
                default:
                    args.Add(parameter.IsIn() ? "in context" : "context");
                    break;
            }
        }

        w.Line($"{m}({string.Join(", ", args)});");
        w.Close();
        w.Close();
    }

    private static string Local(ColumnModel column) => SymbolHelpers.CamelCase(column.PropertyName) + "Column";

    private static string Mask(IEnumerable<ColumnModel> columns)
    {
        var list = columns.ToList();
        return list.Count == 0
            ? "global::Colony.ComponentMask.Empty"
            : $"global::Colony.ComponentMask.Of({string.Join(", ", list.Select(c => c.LocalName))})";
    }

    private static string ResourceMask(IEnumerable<ColumnModel> resources, Dictionary<string, string> locals)
    {
        var names = resources.Select(r => locals[r.TypeCode]).Distinct(StringComparer.Ordinal).ToList();
        return $"global::Colony.ResourceMask.Of({string.Join(", ", names)})";
    }

    private static string StringArray(EquatableArray<string> values) =>
        values.Count == 0
            ? "global::System.Array.Empty<string>()"
            : $"new string[] {{ {string.Join(", ", values.Select(SymbolHelpers.Literal))} }}";

    private static string Names(IEnumerable<ColumnModel> columns) => string.Join(", ", columns.Select(c => c.PropertyName));

    private static bool IsIn(this ParameterModel parameter) => !parameter.IsWrite && parameter.PassedByReference;
}
