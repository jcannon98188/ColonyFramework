namespace Colony.Generators;

/// <summary>One enclosing partial type the generated code must reopen: <c>partial class Outer</c>.</summary>
internal sealed record TypeDeclarationModel(string Keyword, string Name);

/// <summary>Where a generated declaration goes: its namespace (null for the global one) and containing types, outermost first.</summary>
internal sealed record PlacementModel(string? Namespace, EquatableArray<TypeDeclarationModel> Containers, string HintPrefix);

/// <summary>One instance field of a [Component]: the name, the code type and the stable type name hashed into the layout.</summary>
internal sealed record FieldModel(string Name, string TypeCode, string StableTypeName);

/// <summary>A [Component] struct ready to emit (spec section 4.1): stable name, options, fields and whether to add equality members.</summary>
internal sealed record ComponentModel(
    PlacementModel Placement,
    string Name,
    string FullName,
    string StableName,
    bool NonFragmenting,
    EquatableArray<FieldModel> Fields,
    bool EmitEquality,
    EquatableArray<DiagnosticInfo> Diagnostics);

internal enum ParameterKind
{
    /// <summary>A <c>ref</c> or <c>in</c> Component struct.</summary>
    Component,
    EntityId,
    Context,
    ChunkView,
    /// <summary>The System's own generated typed query, <c>in {Method}Query</c>.</summary>
    Query,
}

/// <summary>One parameter of a [System] method. <paramref name="IsWrite"/> is <c>ref</c>; <paramref name="PassedByReference"/> is <c>ref</c> or <c>in</c>.</summary>
internal sealed record ParameterModel(ParameterKind Kind, string Name, string TypeCode, string TypeSimpleName, bool IsWrite, bool PassedByReference);

internal enum SystemKind
{
    /// <summary>Called once per Entity with ref/in Components; the generator writes the chunk loop.</summary>
    PerEntity,
    /// <summary>Called once per chunk with the raw <c>ChunkView</c>; access comes from [Reads]/[Writes].</summary>
    Chunk,
    /// <summary>Called once per chunk with the generated typed query; access comes from [Reads]/[Writes].</summary>
    TypedQuery,
    /// <summary>Called once per Tick on the Tick thread with only the context (a Begin hook).</summary>
    Serial,
}

/// <summary>One column of a generated typed query.</summary>
internal sealed record ColumnModel(string TypeCode, string PropertyName, string LocalName, bool IsWrite);

/// <summary>One [System] method with everything the definition and entry need (spec section 4.3).</summary>
internal sealed record SystemModel(
    string MethodName,
    string SystemName,
    string PhaseName,
    int Every,
    int Offset,
    EquatableArray<string> After,
    EquatableArray<string> Before,
    bool SkipAsleepChunks,
    SystemKind Kind,
    EquatableArray<ParameterModel> Parameters,
    EquatableArray<ColumnModel> Columns,
    EquatableArray<ColumnModel> With,
    EquatableArray<ColumnModel> Without,
    EquatableArray<ColumnModel> ChangeFilter,
    EquatableArray<ColumnModel> ReadsResources,
    EquatableArray<ColumnModel> WritesResources,
    bool IgnoreEnableBits,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public IEnumerable<ColumnModel> Writes => Columns.Where(c => c.IsWrite);

    public IEnumerable<ColumnModel> Reads => Columns.Where(c => !c.IsWrite);

    /// <summary>Every Component the definition needs a <c>ComponentType</c> for, each once, in first-use order.</summary>
    public IEnumerable<ColumnModel> AllTypes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in Columns.Concat(With).Concat(Without).Concat(ChangeFilter))
        {
            if (seen.Add(column.TypeCode))
            {
                yield return column;
            }
        }
    }

    public bool HasQuery => Kind is SystemKind.PerEntity or SystemKind.TypedQuery;

    /// <summary>Every Resource the definition needs a <c>ResourceType</c> for, each once (a type both read and written counts once).</summary>
    public IEnumerable<ColumnModel> AllResources()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in WritesResources.Concat(ReadsResources))
        {
            if (seen.Add(resource.TypeCode))
            {
                yield return resource;
            }
        }
    }
}

/// <summary>A [System] method together with the partial type it lives in; grouped per type before emission.</summary>
internal sealed record SystemMethodModel(PlacementModel Placement, TypeDeclarationModel Owner, string OwnerFullName, SystemModel System);
