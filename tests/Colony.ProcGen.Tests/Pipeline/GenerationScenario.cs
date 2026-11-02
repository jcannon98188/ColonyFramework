using Colony.Fields;
using Colony.Testing;

namespace Colony.ProcGen.Tests.Pipeline;

/// <summary>
/// The generation oracle (spec section 9.3; TASK-42 AC #3): the five-stage <see cref="TerrainPipeline"/> World starts
/// Unloaded and a scripted focus point tours it, so every Tick the residency manager generates chunks on demand
/// (noise, region-keyed lakes, the halo smoothing, the climate pre-pass and chunk-keyed tree placements) and drops
/// the ones it leaves. The World hash covers the generated Fields and the placed Entities, so it is the evidence that
/// generation is identical on any worker count and under Native AOT. No test-framework dependency: the Native AOT
/// host compiles it as well.
/// </summary>
public sealed class GenerationScenario : Testing.Scenario
{
    public const ulong DefaultSeed = 0x5EED_0042_0003UL;

    /// <summary>Tiles the focus point steps along its tour each Tick.</summary>
    public const int Step = 5;

    private static readonly InputSource Source = new(1);

    public override string Name => "generation";

    public override ulong Seed { get; init; } = DefaultSeed;

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DebugValidation(false);
        TerrainPipeline.Compose(builder, c => c.Builder.AddResidency(c.Fields, c.Builder.Component<TilePos>(), new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded }));
    }

    public override void Populate(World world) => ArgumentNullException.ThrowIfNull(world);

    public override void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Inputs.Submit(Source, new FocusPoint(1, Tour(world.CurrentTick), 0));
        world.Tick();
    }

    /// <summary>A boustrophedon over the 64 x 48 World, one <see cref="Step"/> per Tick, wrapping.</summary>
    public static TilePos Tour(long tick)
    {
        var size = TerrainPipeline.Size;
        int perRow = size.X / Step;
        int rows = size.Y / Step;
        int i = (int)(tick % (perRow * rows));
        int row = i / perRow;
        int column = i % perRow;
        int x = (row & 1) == 0 ? column * Step : size.X - 1 - (column * Step);
        return new TilePos(x, row * Step, 0);
    }
}
