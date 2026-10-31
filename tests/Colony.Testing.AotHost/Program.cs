using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

// Prints one line per worker count: "<workers> <hash at checkpoint 1> ... <hash at the last Tick>" (hex).
return HarnessHost.Run(args, [new ColonistScenario(), new FieldScenario()], Console.Out, Console.Error);
