using Colony.Testing;
using Colony.Jobs.Tests.Scenarios;
using Colony.Pathfinding.Tests.Scenarios;
using Colony.ProcGen.Tests.Pipeline;
using Colony.Testing.Tests.Scenarios;

// Prints one line per worker count: "<workers> <hash at checkpoint 1> ... <hash at the last Tick>" (hex).
return HarnessHost.Run(args, [new ColonistHarnessScenario(), new FieldScenario(), new InputScenario(), new StreamingScenario(), new StreamingScenario { LoadDelayTicks = 2, RingWidth = 2 }, new StreamingScenario { ResidentChunkBudget = 10 },
    new InputScenario { Restricted = true }, new PathRequestScenario(), new PathRequestScenario { Starved = true }, new FlowFieldScenario(), new FlowFieldScenario { TightBudget = true },
    new ClaimScenario(), new WorkIndexScenario(), new GenerationScenario()], Console.Out, Console.Error);
