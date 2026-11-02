using System.Globalization;
using Colony.Fields;
using Colony.Hashing;

namespace Colony.ProcGen;

/// <summary>
/// Registers the generation pipeline of a schema (spec section 9.3): named stages over the schema's Fields,
/// world-level pre-passes and the generation version. Created by
/// <see cref="GenerationBuilderExtensions.AddGeneration"/> after <c>AddFields</c>; Plugins find it with
/// <see cref="GenerationBuilderExtensions.Generation(WorldBuilder)"/>, add stages and replace Overridable ones.
/// <see cref="Pipeline"/> (or <see cref="WorldBuilder.Build"/>) closes registration; each World then gets a
/// <see cref="WorldGeneration"/> whose <see cref="ChunkProvider"/> is seeded with the World seed.
/// </summary>
public sealed class GenerationBuilder : WorldExtensionDefinition
{
    /// <summary>Extension name; saves carry the generation version under it.</summary>
    public const string ExtensionName = "generation";

    /// <summary>Prefix of the generated (transient) Resource each pre-pass is cached in: <c>prefix + pre-pass name</c>.</summary>
    public const string PrePassResourcePrefix = "colony.generation.prepass.";

    /// <summary>Name of the Component every placed Entity carries (<see cref="ProcGen.GeneratedEntity"/>).</summary>
    public const string GeneratedEntityComponentName = "colony.generated";

    /// <summary>Layout hash of the extension's saved state ("GENR"): the state is the version section only.</summary>
    private const ulong LayoutSeed = 0x4745_4E52UL;

    private readonly WorldBuilder _builder;
    private readonly List<StageRegistration> _stages = [];
    private readonly List<GenerationPrePass> _prePasses = [];
    private readonly List<StageOverride> _overrides = [];
    private GenerationPipeline? _pipeline;
    private int _version;

    internal GenerationBuilder(WorldBuilder builder, FieldsBuilder fields, int version)
    {
        _builder = builder;
        Fields = fields;
        _version = version;
        GeneratedEntity = builder.AddComponent<GeneratedEntity>(GeneratedEntityComponentName).Component<GeneratedEntity>();
    }

    public override string Name => ExtensionName;

    public override ulong LayoutHash => Hash64.Compute(ReadOnlySpan<byte>.Empty, LayoutSeed);

    /// <summary>The generation version as the save manifest records it.</summary>
    public override string Version => GenerationVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The generation version (spec section 9.3): bump it whenever a stage's output changes (the golden hashes
    /// drift). A World loaded from a save with another version refuses to regenerate chunks.
    /// </summary>
    public int GenerationVersion
    {
        get => _version;
        set
        {
            ThrowIfFrozen();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _version = value;
        }
    }

    public FieldsBuilder Fields { get; }

    /// <summary>The <see cref="ProcGen.GeneratedEntity"/> Component placed Entities carry.</summary>
    public ComponentType GeneratedEntity { get; }

    /// <summary>The Game's <see cref="TilePos"/> Component; when set, placed Entities also get their position.</summary>
    public ComponentType? TilePos { get; set; }

    /// <summary>Stages in registration order (replacements in the slot of the stage they replaced).</summary>
    public IReadOnlyList<GenerationStage> Stages => _stages.ConvertAll(s => s.Stage);

    /// <summary>Stages replaced through <see cref="ReplaceStage"/>, in replacement order.</summary>
    public IReadOnlyList<StageOverride> Overrides => _overrides;

    /// <summary>Adds a stage. Inside a Plugin scope the stage is attributed to that Plugin; <paramref name="overridable"/> lets a later Plugin replace it.</summary>
    public GenerationBuilder AddStage(GenerationStage stage, bool overridable = false)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ThrowIfFrozen();
        ArgumentException.ThrowIfNullOrWhiteSpace(stage.Name, nameof(stage));
        if (IndexOf(stage.Name) >= 0 || _prePasses.Exists(p => string.Equals(p.Name, stage.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A generation stage or pre-pass named '{stage.Name}' is already registered.", nameof(stage));
        }

        _stages.Add(new StageRegistration(stage, overridable, _builder.CurrentPluginId));
        return this;
    }

    /// <summary>
    /// Replaces the Overridable stage with the same <see cref="GenerationStage.Name"/>. The replacement keeps the
    /// name (so its randomness stream), the registration slot and the Overridable flag; the fact is recorded in
    /// <see cref="Overrides"/>.
    /// </summary>
    public GenerationBuilder ReplaceStage(GenerationStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ThrowIfFrozen();
        int index = IndexOf(stage.Name);
        if (index < 0)
        {
            throw new ArgumentException($"No generation stage named '{stage.Name}' is registered; add it instead.", nameof(stage));
        }

        var original = _stages[index];
        if (!original.Overridable)
        {
            throw new InvalidOperationException($"Generation stage '{stage.Name}' is not Overridable and cannot be replaced.");
        }

        string? replacing = _builder.CurrentPluginId;
        _stages[index] = new StageRegistration(stage, true, original.Owner);
        _overrides.Add(new StageOverride(stage.Name, original.Owner, replacing));
        return this;
    }

    /// <summary>Adds a world-level pre-pass and registers the transient Resource its value is cached in.</summary>
    public GenerationBuilder AddPrePass<T>(GenerationPrePass<T> prePass)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(prePass);
        ThrowIfFrozen();
        ArgumentException.ThrowIfNullOrWhiteSpace(prePass.Name, nameof(prePass));
        if (IndexOf(prePass.Name) >= 0 || _prePasses.Exists(p => string.Equals(p.Name, prePass.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A generation stage or pre-pass named '{prePass.Name}' is already registered.", nameof(prePass));
        }

        if (_prePasses.Exists(p => p.ValueType == typeof(T)))
        {
            throw new ArgumentException($"A pre-pass producing {typeof(T).Name} is already registered; pre-pass values are looked up by type.", nameof(prePass));
        }

        prePass.RegisterResource(_builder, PrePassResourcePrefix + prePass.Name);
        _prePasses.Add(prePass);
        return this;
    }

    /// <summary>The frozen, validated pipeline. The first access closes stage registration.</summary>
    public GenerationPipeline Pipeline
    {
        get
        {
            _pipeline ??= new GenerationPipeline(Fields, _stages, _prePasses, _version);
            return _pipeline;
        }
    }

    public override void Freeze() => _ = Pipeline;

    public override IWorldExtension Create(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var schema = world.Schema;
        var resources = new ResourceType[_prePasses.Count];
        for (int i = 0; i < resources.Length; i++)
        {
            resources[i] = schema.Resource(PrePassResourcePrefix + _prePasses[i].Name);
        }

        return new WorldGeneration(world, this, Pipeline, resources);
    }

    private int IndexOf(string name) => _stages.FindIndex(s => string.Equals(s.Stage.Name, name, StringComparison.Ordinal));

    private void ThrowIfFrozen()
    {
        if (_pipeline is not null)
        {
            throw new InvalidOperationException("The generation pipeline has been built; stage registration is closed.");
        }
    }

    internal readonly record struct StageRegistration(GenerationStage Stage, bool Overridable, string? Owner);
}

/// <summary>A stage another registration replaced: the stage name, the Plugin that registered the original and the Plugin that replaced it (null outside Plugin scopes).</summary>
public readonly record struct StageOverride(string Stage, string? OriginalPlugin, string? ReplacingPlugin);

/// <summary>Registration entry points of the generation pipeline.</summary>
public static class GenerationBuilderExtensions
{
    /// <summary>
    /// Gives the schema a generation pipeline over the Fields of <paramref name="fields"/> (register Fields first)
    /// with generation version <paramref name="version"/>. Registers the <see cref="GeneratedEntity"/> Component.
    /// Call once per schema, after <c>AddFields</c>.
    /// </summary>
    public static GenerationBuilder AddGeneration(this WorldBuilder builder, FieldsBuilder fields, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        var generation = new GenerationBuilder(builder, fields, version);
        builder.AddExtension(generation);
        return generation;
    }

    /// <summary>The schema's generation builder, for Plugins adding or replacing stages; throws when the Game registered none.</summary>
    public static GenerationBuilder Generation(this WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.FindExtension<GenerationBuilder>()
            ?? throw new InvalidOperationException("The schema has no generation pipeline; the Game calls AddGeneration first.");
    }

    /// <summary>The World's generation state.</summary>
    public static WorldGeneration Generation(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Extension<WorldGeneration>();
    }
}
