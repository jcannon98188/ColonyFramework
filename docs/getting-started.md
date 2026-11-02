# Getting started

This walks through the smallest useful Colony Framework program, then shows the tools around it. Every snippet comes
from [samples/HelloColony](../samples/HelloColony), which compiles and is checked against the spec by a test. Terms
(Tick, Phase, Field, Component and so on) are defined in the [glossary](../GLOSSARY.md).

## 1. Prerequisites

The .NET SDK named in [global.json](../global.json). From the repository root:

```bash
dotnet build Colony.slnx -c Release
```

The Framework is not published to a package feed yet. Reference the projects under `src/` directly
(`Colony.Core`, plus `Colony.Fields` and `Colony.Persistence` for the code below, and `Colony.Generators` as an
analyzer for the attributes). The sample's project file shows the exact references.

## 2. Components and Systems

A **Component** is a plain unmanaged struct. A **System** is a static method that runs once per Tick over every
Entity that has the Components it asks for. The source generator turns the attributes into the wiring.

```csharp
[Component] public partial struct Hunger { public Fixed32 Value; }
[Component] public partial struct Food   { public int Units; }

public static partial class ColonySystems
{
    [System(NamedPhase.Simulate, Every = 1)]
    public static void Starve(ref Hunger hunger)
        => hunger.Value += Fixed32.FromRatio(1, 600);
}
```

Simulation state uses fixed-point numbers (`Fixed32`, `Fixed64`) and counter-based RNG (`Philox`), never
`float` transcendentals, wall-clock time or `System.Random`. These are what keep results bit-identical across
machines, and the `COL1xxx` analyzers flag the common mistakes at build time.

A System declares what it reads and writes through its parameters, so the scheduler can run non-conflicting Systems
in parallel and still produce the same result on one thread or sixteen.

## 3. Build a World

Registration happens on a `WorldBuilder`. `Build()` validates and freezes the **schema**, which is the contract
every save, replay and peer is checked against.

```csharp
var builder = new WorldBuilder()
    .AddComponent<Hunger>().AddComponent<Food>();
builder.AddFields(new WorldSize(64, 64, 1)).AddField<Heat>("heat");   // a tile Field
ColonySystems.AddSystems(builder);
var schema = builder.Build();

using var world = schema.CreateWorld(new WorldOptions { Seed = 12345 });
```

Entities are created and changed through a **command buffer**, which is applied at a safe point:

```csharp
var commands = world.CreateCommandBuffer();
for (int i = 0; i < 100; i++) commands.Create(ComponentMask.Of(schema.Component<Hunger>()));
world.Playback(commands);
```

## 4. Tick, hash, save

```csharp
for (int tick = 0; tick < 1000; tick++) world.Tick();

world.Save("colony.save", SaveOptions.Default);
Console.WriteLine(world.Hash());   // identical on every machine
```

`world.Hash()` covers all simulation state. Two runs with the same schema, seed and inputs must print the same
number whatever the worker count. This is the main correctness check in the Framework's own tests, and it is
the first thing to try when you suspect nondeterminism in your game.

Run the finished sample:

```bash
dotnet run -c Release --project samples/HelloColony
dotnet run -c Release --project samples/HelloColony -- --workers 0 --colonists 100000   # all physical cores
```

## 5. Test determinism

`Colony.Testing` runs a scenario on 1, 2, 4 and N workers and compares the hash after every checkpoint, so a
worker-count dependency fails with the first diverging Tick:

```csharp
var result = DeterminismHarness.Run(new MyScenario(), new DeterminismOptions { Ticks = 100, CheckpointEvery = 10 });
```

Golden hashes (`GoldenHashStore`) pin a scenario's result in the repository, and `AllocationHarness` fails a
scenario that allocates on any Tick thread in steady state and names the System responsible. The test projects
under [tests/](../tests) contain working examples of both.

## 6. Watch a running World

HelloColony has the diagnostics sinks behind flags:

```bash
# Perfetto trace of every Tick; open the file at https://ui.perfetto.dev
dotnet run -c Release --project samples/HelloColony -- --trace trace.json --level Detailed

# live counters for dotnet-counters
dotnet run -c Release --project samples/HelloColony -- --ticks 0 --no-save --counters --pace 16 --colonists 100000

# inspect a running World from another terminal (the host prints the port and token)
dotnet run -c Release --project samples/HelloColony -- --ticks 0 --no-save --inspect 0 --replay colony.replay
dotnet run --project tools/Colony.Inspect -- status --port <N> --token <T>
```

`colony-inspect` also offers `schema`, `entities`, `field`, `pause`, `step`, `rewind` and `shutdown`. The full flag
list is in [samples/README.md](../samples/README.md).

## 7. Where to go next

| I want to... | Read |
|---|---|
| understand the design and its guarantees | [Architecture spec](spec/architecture.md), sections 2, 4 and 6 |
| move agents around a large map | spec section 9 (pathfinding), `Colony.Pathfinding` |
| assign work to colonists | spec section 9 (claims, work index, hauling), `Colony.Colony` |
| generate a world | spec section 9 (procedural generation), `Colony.ProcGen` |
| save, load, replay or migrate | spec section 7 |
| build lockstep multiplayer | [Lockstep networking guide](guides/lockstep-networking.md) and `samples/HelloLockstep` |
| extend the Framework without forking it | spec section 8 (Plugins, Definitions, C ABI) |
| understand a past decision | [ADRs](adr) |
| measure performance | [benchmarks/README.md](../benchmarks/README.md) |
| contribute or run the project loop | the build commands in the [README](../README.md#build-and-test) and the tracker under `backlog/` |
