namespace Colony.Core.Tests.Scheduling;

public sealed class TickClockTests
{
    private const double Sixty = 1.0 / 60;

    [Fact]
    public void Elapsed_time_accumulates_into_whole_ticks_and_keeps_the_remainder()
    {
        var clock = new TickClock(Sixty);

        Assert.Equal(0, clock.Advance(0.010));
        Assert.Equal(1, clock.Advance(0.010));
        Assert.Equal(0.020 - Sixty, clock.Accumulator, 12);
        Assert.InRange(clock.Alpha, 0, 1);
        Assert.Equal(3, clock.Advance(0.050));
    }

    [Fact]
    public void Pause_ignores_time_and_speed_scales_it()
    {
        var clock = new TickClock(Sixty) { Paused = true };

        Assert.Equal(0, clock.Advance(1));
        Assert.Equal(0, clock.Accumulator);

        clock.Paused = false;
        clock.Speed = 2;
        Assert.Equal(2, clock.Advance(Sixty));

        clock.Speed = 0;
        Assert.Equal(0, clock.Advance(10));
    }

    [Fact]
    public void A_backlog_beyond_the_catch_up_cap_is_dropped()
    {
        var clock = new TickClock(Sixty, maxCatchUpTicks: 5);

        Assert.Equal(5, clock.Advance(10));
        Assert.Equal(0, clock.Accumulator);
        Assert.Equal(5, clock.Advance(5 * Sixty));
        Assert.Equal(0, clock.Advance(0));

        clock.Advance(0.5 * Sixty);
        clock.Reset();
        Assert.Equal(0, clock.Accumulator);
    }

    [Fact]
    public void Arguments_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickClock(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickClock(Sixty, 0));
        var clock = new TickClock(Sixty);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Speed = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-1));
    }
}
