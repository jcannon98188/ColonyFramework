using System.Globalization;
using Colony.Scenario;

// Prints one line per worker count: "<workers> <hash at checkpoint 1> ... <final hash>" (hex).
int ticks = 30;
int[] workers = [1];
int[] checkpoints = [];
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--ticks":
            ticks = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--workers":
            workers = ParseList(args[i + 1]);
            break;
        case "--checkpoints":
            checkpoints = ParseList(args[i + 1]);
            break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}");
            return 2;
    }
}

foreach (int count in workers)
{
    using var world = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), count);
    var hashes = ColonistScenario.Run(world, ticks, checkpoints);
    Console.Out.Write(count.ToString(CultureInfo.InvariantCulture));
    foreach (ulong hash in hashes)
    {
        Console.Out.Write(' ');
        Console.Out.Write(hash.ToString("x16", CultureInfo.InvariantCulture));
    }

    Console.Out.WriteLine();
}

return 0;

static int[] ParseList(string text) => text.Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
