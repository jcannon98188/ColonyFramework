using System.Globalization;
using Colony.Generated.Tests.Hello;
using Colony.Numerics;

namespace Colony.Generated.Tests;

/// <summary>Ticket AC #1 and #4: the spec 11.1 sample compiles, runs 1000 Ticks under the scheduler and prints a World hash.</summary>
public sealed class HelloColonyTests
{
    [Fact]
    public void Hello_colony_runs_1000_ticks_and_prints_a_world_hash()
    {
        ulong hash = HelloColony.Run(workers: 1);

        TestContext.Current.TestOutputHelper?.WriteLine(string.Format(CultureInfo.InvariantCulture, "World hash after {0} Ticks: 0x{1:X16}", HelloColony.Ticks, hash));
        Assert.NotEqual(0UL, hash);
        Assert.Equal(hash, HelloColony.Run(workers: 1));
        Assert.Equal(hash, HelloColony.Run(workers: 4));
    }

    [Fact]
    public void The_generated_starve_system_ran_every_tick_on_every_colonist()
    {
        var schema = HelloColony.BuildSchema();
        using var world = HelloColony.CreateWorld(schema, workers: 2);
        for (int tick = 0; tick < HelloColony.Ticks; tick++)
        {
            Assert.True(world.Tick());
        }

        var hunger = schema.Component<Hunger>();
        var expected = Fixed32.Zero;
        for (int i = 0; i < HelloColony.Ticks; i++)
        {
            expected += Fixed32.FromRatio(1, 600);
        }

        int seen = 0;
        foreach (var chunk in world.Query(hunger))
        {
            foreach (ref readonly var value in chunk.Column<Hunger>(hunger))
            {
                Assert.Equal(expected, value.Value);
                seen++;
            }
        }

        Assert.Equal(HelloColony.Colonists, seen);
        Assert.Equal(HelloColony.Ticks, world.CurrentTick);
    }

    [Fact]
    public void The_generated_component_registers_with_its_stable_name_and_field_level_layout()
    {
        var schema = HelloColony.BuildSchema();

        var hunger = schema.Describe(schema.Component<Hunger>());
        Assert.Equal("Hunger", hunger.Name);
        Assert.Equal(4, hunger.Size);
        var field = Assert.Single(hunger.Fields);
        Assert.Equal(new ComponentField("Value", "Colony.Numerics.Fixed32", 0, 4), field);
        Assert.Equal(ComponentLayout.ComputeHash("Hunger", 4, 4, hunger.Fields), hunger.LayoutHash);
        var other = new WorldBuilder().AddComponent<Food>().AddComponent<Hunger>().Build();
        Assert.Equal(hunger.LayoutHash, other.Describe(other.Component<Hunger>()).LayoutHash);
        Assert.Equal("Food", ComponentLayout.Of<Food>().Name);
    }

    [Fact]
    public void The_generated_system_definition_carries_the_declared_access_set_phase_and_rate()
    {
        var schema = HelloColony.BuildSchema();

        var starve = schema.System("Starve");
        Assert.Equal(Phase.Simulate, starve.Phase);
        Assert.Equal(1, starve.Every);
        Assert.True(starve.Writes.Contains(schema.Component<Hunger>()));
        Assert.False(starve.Writes.Contains(schema.Component<Food>()));
        Assert.True(starve.Reads.IsEmpty);
    }

    [Fact]
    public void Generated_components_have_value_equality()
    {
        var a = new Food { Units = 3 };
        var b = new Food { Units = 3 };
        var c = new Food { Units = 4 };

        Assert.True(a == b);
        Assert.True(a != c);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(new Hunger { Value = Fixed32.One }, default);
    }
}
