namespace Colony.Core.Tests.Scheduling;

public sealed class TickGateTests
{
    [Fact]
    public void A_held_gate_stops_ticks_and_steps_let_a_counted_number_through()
    {
        var gate = new TickGate();
        var schema = new WorldBuilder()
            .AddSystem(new SystemDefinition { Name = "Noop", Begin = (in SystemContext _) => { } })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { TickGate = gate, WorkerThreads = 1 });

        Assert.True(world.Tick());
        gate.Hold();
        Assert.True(gate.IsHeld);
        Assert.False(world.Tick());
        Assert.Equal(1, world.CurrentTick);

        gate.Step(2);
        Assert.True(world.Tick());
        Assert.True(world.Tick());
        Assert.False(world.Tick());
        Assert.Equal(3, world.CurrentTick);

        gate.Release();
        Assert.False(gate.IsHeld);
        Assert.True(world.Tick());
        Assert.Equal(4, world.CurrentTick);
    }

    [Fact]
    public void One_gate_can_hold_several_worlds()
    {
        var gate = new TickGate();
        var schema = new WorldBuilder().Build();
        using var a = schema.CreateWorld(new WorldOptions { TickGate = gate, WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { TickGate = gate, WorkerThreads = 1 });

        gate.Hold();
        Assert.False(a.Tick());
        Assert.False(b.Tick());
        gate.Step();
        Assert.True(a.Tick());
        Assert.False(b.Tick());
        Assert.Equal(0, gate.PendingSteps);
    }
}
