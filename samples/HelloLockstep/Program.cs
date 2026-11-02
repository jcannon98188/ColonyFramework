using System.Globalization;
using Colony.Samples.Lockstep;

// One lockstep peer per process:
//   HelloLockstep --seat 0 --ports 5000,5001,5002 --active 0,1 [--ticks 300] [--tick-ms 2] [--workers 1] [--await-join 100]
//   HelloLockstep --seat 1 --ports 5000,5001,5002 --active 0,1
//   HelloLockstep --seat 2 --ports 5000,5001,5002 --join-from 0 [--join-margin 60]
// Prints "hash 0x..." (the World hash at the last Tick). Exit 0 in sync, 1 on a desync or failure, 2 on bad usage.
int seat = -1;
int[] ports = [];
int ticks = 300;
int[]? active = null;
int joinFrom = -1;
int joinMargin = 60;
int tickMs = 2;
int workers = 1;
int awaitJoin = -1;
try
{
    for (int i = 0; i < args.Length; i += 2)
    {
        string value = i + 1 < args.Length ? args[i + 1] : throw new FormatException($"Option {args[i]} needs a value.");
        switch (args[i])
        {
            case "--seat": seat = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--ports": ports = [.. value.Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture))]; break;
            case "--ticks": ticks = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--active": active = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => int.Parse(p, CultureInfo.InvariantCulture))]; break;
            case "--join-from": joinFrom = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--join-margin": joinMargin = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--tick-ms": tickMs = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--workers": workers = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--await-join": awaitJoin = int.Parse(value, CultureInfo.InvariantCulture); break;
            default: throw new FormatException($"Unknown option {args[i]}.");
        }
    }

    if (seat < 0 || seat >= ports.Length)
    {
        throw new FormatException("--seat and --ports are required, and the seat must index the ports.");
    }
}
catch (FormatException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

var result = PeerHost.Run(new PeerOptions
{
    Seat = seat,
    Ports = ports,
    Ticks = ticks,
    ActiveSeats = active,
    JoinFrom = joinFrom,
    JoinMarginTicks = joinMargin,
    TickMilliseconds = tickMs,
    Workers = workers,
    AwaitJoinAtTick = awaitJoin,
    Log = Console.Out,
});
if (result.Error is not null)
{
    Console.Error.WriteLine(result.Error);
    return 1;
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seat {result.Seat} ran Ticks {result.StartTick}..{result.FinalTick}, {result.Stalls} stall(s)"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hash 0x{result.FinalHash:X16}"));
return result.Desynced ? 1 : 0;
