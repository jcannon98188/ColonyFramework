using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

// Prints one line per worker count: "<workers> <hash at checkpoint 1> ... <hash at the last Tick>" (hex).
return HarnessHost.Run(args, [new ColonistHarnessScenario(), new FieldScenario(), new InputScenario(), new StreamingScenario(), new StreamingScenario { LoadDelayTicks = 2, RingWidth = 2 }, new StreamingScenario { ResidentChunkBudget = 10 }], Console.Out, Console.Error);
