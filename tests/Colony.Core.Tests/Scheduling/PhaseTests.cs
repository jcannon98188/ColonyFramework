namespace Colony.Core.Tests.Scheduling;

public sealed class PhaseTests
{
    [Fact]
    public void The_six_named_phases_run_in_spec_order_by_default()
    {
        var schema = new WorldBuilder().Build();

        Assert.Equal(
            [Phase.Streaming, Phase.Input, Phase.Begin, Phase.Simulate, Phase.Late, Phase.End],
            schema.Phases.ToArray());
    }

    [Fact]
    public void A_game_phase_is_inserted_after_the_named_phase_it_names_and_keeps_registration_order()
    {
        var builder = new WorldBuilder();
        var weather = builder.AddPhase("Weather", after: Phase.Begin);
        var plants = builder.AddPhase("Plants", after: Phase.Begin);
        var schema = builder.Build();

        Assert.Equal("Weather", weather.Name);
        Assert.Equal(
            [Phase.Streaming, Phase.Input, Phase.Begin, weather, plants, Phase.Simulate, Phase.Late, Phase.End],
            schema.Phases.ToArray());
        Assert.NotEqual(weather, plants);
    }

    [Fact]
    public void Game_phases_go_only_between_named_phases()
    {
        var builder = new WorldBuilder();
        var weather = builder.AddPhase("Weather", after: Phase.Simulate);

        Assert.Throws<ArgumentException>(() => builder.AddPhase("Late2", after: Phase.End));
        Assert.Throws<ArgumentException>(() => builder.AddPhase("Nested", after: weather));
        Assert.Throws<ArgumentException>(() => builder.AddPhase("Weather", after: Phase.Begin));
        Assert.Throws<ArgumentException>(() => builder.AddPhase("Simulate", after: Phase.Begin));
    }
}
