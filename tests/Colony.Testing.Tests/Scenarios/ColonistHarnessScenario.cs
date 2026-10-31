using Colony.Scenario;
using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

/// <summary>
/// TASK-7's acceptance oracle (tests/Colony.Core.Tests/Scenario/ColonistScenario.cs, shared by source with
/// Colony.Core.Tests and Colony.Diagnostics.Tests) wrapped as a harness <see cref="Scenario"/>: 100k colonists,
/// 5k food, 1% churn. <see cref="Sabotage"/> turns on the thread-keyed RNG that makes it the negative control.
/// </summary>
public sealed class ColonistHarnessScenario : Scenario
{
    public const int DefaultColonists = ColonistScenario.DefaultColonists;
    public const int DefaultFood = ColonistScenario.DefaultFood;

    public override string Name => Sabotage ? "colonist-negative-control" : "colonist";

    public override ulong Seed { get; init; } = ColonistScenario.DefaultSeed;

    public int Colonists { get; init; } = DefaultColonists;

    public int Food { get; init; } = DefaultFood;

    /// <summary>Derives per-thread random streams in Move, so worker counts disagree: the negative control.</summary>
    public bool Sabotage { get; init; }

    public override int InitialEntityCapacity => (Colonists + Food) * 2;

    public override void Compose(WorldBuilder builder) => ColonistScenario.Compose(builder, Sabotage, Food);

    public override void Populate(World world) => ColonistScenario.Populate(world, Colonists, Food);
}
