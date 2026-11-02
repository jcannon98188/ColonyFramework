namespace Colony.Testing;

/// <summary>
/// A seeded, parameterised simulation the harnesses can run (spec section 10.3): the scenario composes
/// its schema on a <see cref="WorldBuilder"/> the harness owns, populates a World the harness created
/// (seeded from <see cref="Seed"/>, so every draw goes through <see cref="World.Random"/>) and drives one
/// Tick per <see cref="Advance"/>. Parameters are properties of the subclass; a failure reproduces from the
/// scenario type, its parameters and the seed. Hashes prove sameness, not correctness: model-based
/// tests stay separate.
/// </summary>
public abstract class Scenario
{
    /// <summary>Stable name; the golden-hash file is named after it.</summary>
    public abstract string Name { get; }

    /// <summary>The World seed (<see cref="WorldOptions.Seed"/>).</summary>
    public virtual ulong Seed { get; init; }

    /// <summary><see cref="WorldOptions.InitialEntityCapacity"/>: size it for the load so warm-up finishes before measurement.</summary>
    public virtual int InitialEntityCapacity => 1024;

    /// <summary>Registers Components, Systems and extensions. Called once per World the harness creates, so per-schema scratch state is safe.</summary>
    public abstract void Compose(WorldBuilder builder);

    /// <summary>Creates the initial Entities and Field state. Draw randomness from <see cref="World.Random"/> only.</summary>
    public abstract void Populate(World world);

    /// <summary>Advances the World by one Tick; override to drive Input Commands or a gate alongside the Tick.</summary>
    public virtual void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Tick();
    }

    /// <summary>
    /// Called instead of <see cref="Populate"/> when the harness loaded <paramref name="world"/> from a save
    /// (spec section 7.1): rebuild any per-schema scratch caches the Systems derive from World state between
    /// Ticks (a site list, a grid), so the loaded World continues exactly as the unsaved one would. Nothing by default.
    /// </summary>
    public virtual void OnLoaded(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
    }

    /// <summary>Wraps plain delegates as a scenario for one-off tests.</summary>
    public static Scenario Create(string name, ulong seed, Action<WorldBuilder> compose, Action<World> populate, Action<World>? step = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(compose);
        ArgumentNullException.ThrowIfNull(populate);
        return new DelegateScenario(name, compose, populate, step) { Seed = seed };
    }

    private sealed class DelegateScenario : Scenario
    {
        private readonly Action<WorldBuilder> _compose;
        private readonly Action<World> _populate;
        private readonly Action<World>? _step;

        public DelegateScenario(string name, Action<WorldBuilder> compose, Action<World> populate, Action<World>? step)
        {
            Name = name;
            _compose = compose;
            _populate = populate;
            _step = step;
        }

        public override string Name { get; }

        public override void Compose(WorldBuilder builder) => _compose(builder);

        public override void Populate(World world) => _populate(world);

        public override void Advance(World world)
        {
            if (_step is null)
            {
                base.Advance(world);
            }
            else
            {
                _step(world);
            }
        }
    }
}
