# Colony Framework

A headless, deterministic C# framework for colony simulations in the style of Dwarf Fortress and RimWorld.

Colony Framework is a library, not an engine and not a game. It is closer to MonoGame than to Unity: you bring the
rendering, input, UI and content; it brings the simulation core. It targets .NET 11 and is Native AOT friendly.

## Why it exists

Colony sims need tens of thousands of agents ticking every frame, a large tile world, pathfinding at scale, save
files that always load, and (for multiplayer or replays) simulations that agree bit for bit across machines. The
Framework provides those pieces and keeps the contract between them strict:

- **Deterministic.** For the same Framework build, Plugin set, seed and input log, World state is bit-identical
  every Tick across thread counts, CPU instruction sets and Windows/Linux x86-64. A hash of the whole World is the
  test oracle, and a hash *tree* finds the first diverging System, Phase and Tick when two runs disagree.
- **Allocation free in steady state.** Hot data is unmanaged structs in 64-byte-aligned chunks. A steady-state
  Tick does not allocate managed memory, and an allocation harness gates it.
- **Built for scale.** Targets 100k+ Entities (1M for stress), a bounded World around 512x512x256 tiles, and a
  16 ms Tick budget on a desktop CPU.
- **Building blocks, not a game model.** Claims, a work index, a hauling matcher, pathfinding and procedural
  generation are mechanisms. Your game supplies the content and the policy.
- **Data in, data out.** Components, Fields, Resources and commands are plain unmanaged structs, so they can be
  saved, hashed, replayed, inspected and passed through a C ABI.

## What is in the box

| Package | What it gives you |
|---|---|
| `Colony.Core` | Archetype ECS (Entities, Components, Relations, buffers), chunked memory, the parallel Phase/System scheduler, command buffers, Resources and events, fixed-point math, counter-based RNG, World hashing and the hash tree |
| `Colony.Fields` | Tile Fields in chunks, stencil kernels, a spatial index, a change feed, chunk providers and streaming residency |
| `Colony.Persistence` | Save/load with migrations, replay log and verifier, Input Commands with per-source permissions, join packages, a transport-free lockstep session |
| `Colony.Pathfinding` | Region IDs, a chunk graph with HPA*, flow Fields, a budgeted request queue |
| `Colony.Colony` | Claims, a work index, a hauling matcher, priority-table and utility helpers |
| `Colony.ProcGen` | Staged generation pipeline, noise, region and halo machinery, validation tooling |
| `Colony.Plugins` | Plugin manifests, Definitions, overridable Systems and the C ABI host |
| `Colony.Diagnostics` | Profiling (EventSource, Meter, Perfetto traces), logging, an inspection server and the `colony-inspect` CLI |
| `Colony.Generators` | Source generator and analyzers, including determinism analyzers (`COL1xxx`) |
| `Colony.Testing` | Determinism harness, allocation harness, golden hashes, lockstep test network |
| `Colony.Native` | AOT shared library exposing the C ABI (`colony.h`) |

Dependencies point one way (`Core` depends on the BCL only), and a CI check enforces the layering. The full rules
are in [the architecture spec](docs/spec/architecture.md), section 3.

## A first look

```csharp
[Component] public partial struct Hunger { public Fixed32 Value; }

public static partial class ColonySystems
{
    [System(NamedPhase.Simulate, Every = 1)]
    public static void Starve(ref Hunger hunger)
        => hunger.Value += Fixed32.FromRatio(1, 600);
}

var builder = new WorldBuilder().AddComponent<Hunger>();
ColonySystems.AddSystems(builder);
var schema = builder.Build();

using var world = schema.CreateWorld(new WorldOptions { Seed = 12345 });
var commands = world.CreateCommandBuffer();
for (int i = 0; i < 100; i++) commands.Create(ComponentMask.Of(schema.Component<Hunger>()));
world.Playback(commands);

for (int tick = 0; tick < 1000; tick++) world.Tick();
Console.WriteLine(world.Hash());   // identical on every machine
```

The complete, compiling version is [samples/HelloColony](samples/HelloColony). See
[docs/getting-started.md](docs/getting-started.md) for a walkthrough.

## Build and test

You need the .NET SDK pinned in [global.json](global.json) (11.0.100 release candidate; the repo rolls forward
within it).

```bash
dotnet build Colony.slnx -c Release
dotnet test Colony.slnx -c Release                 # about a minute; xUnit v3 on Microsoft.Testing.Platform
dotnet run --project tools/Colony.DependencyCheck -- .   # package layering rules
dotnet run -c Release --project samples/HelloColony      # 100 colonists, 1000 Ticks, prints the World hash
```

While iterating, test just the project you touch: `dotnet test tests/Colony.Core.Tests -c Debug`.
CI runs the same suite on Windows x64 and Linux x64 (JIT) for every pull request.

## Repository map

| Path | Contents |
|---|---|
| `src/` | The packages above |
| `tests/` | One test project per package, plus the Native AOT comparison host |
| `samples/` | `HelloColony` (minimal host with every diagnostics sink behind a flag) and `HelloLockstep` (two peers over loopback TCP) |
| `benchmarks/` | BenchmarkDotNet suite and the Tick harness; performance is tracked, not gated |
| `tools/` | `Colony.Inspect` (`colony-inspect`) and `Colony.DependencyCheck` |
| `docs/` | Spec, ADRs, guides and research notes (below) |
| `backlog/` | The task tracker ([Backlog.md](https://github.com/MrLesk/Backlog.md)), kept in the repo |

## Documentation

- [Getting started](docs/getting-started.md): build a World, run it, hash it, save it, watch it.
- [Architecture spec](docs/spec/architecture.md): the v1 design, including principles, packages, the determinism
  contract, persistence and the C ABI.
- [Lockstep networking guide](docs/guides/lockstep-networking.md): the enablers for lockstep multiplayer.
  The Framework ships no networking; it ships what a game needs to build it.
- [Architecture decision records](docs/adr): why the storage, determinism, CPU-only and dependency choices were made.
- [Research notes](docs/research): prior art and prototype evidence behind the spec.
- [Glossary](GLOSSARY.md): the project's vocabulary (Tick, Phase, Field, Plugin and the rest).
- [Samples](samples/README.md) and [benchmarks](benchmarks/README.md): every flag and recorded number.

## Status

Version 0.x, unstable API. The v1 scope is implemented and tested; known gaps and open risks are listed in
section 16 of the spec. Out of scope for v1: rendering, audio and UI, GPU compute, a scripting language, a
networking implementation, unbounded Worlds, and macOS/ARM64 as guaranteed platforms.

## License

[MIT](LICENSE)
