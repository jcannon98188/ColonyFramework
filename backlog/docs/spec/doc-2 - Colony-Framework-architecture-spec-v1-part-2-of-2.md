---
id: doc-2
title: 'Colony Framework architecture spec (v1), part 2 of 2'
type: specification
created_date: '2026-10-31 01:46'
updated_date: '2026-11-02 04:22'
tags:
  - architecture
  - spec
  - v1
---
# Colony Framework: architecture spec (v1), part 2 (sections 9-16)

Part 1 is doc-1 (sections 1-8). Full copy in the repo at docs/spec/architecture.md.

## 9. Colony-sim building blocks

### 9.1 Pathfinding ("Decide pathfinding at scale", evidence in `prototype-gpu-pathfinding.md`)

A CPU service providing:

- **Region IDs** (connected components) maintained incrementally from the change feed: merges are cheap, splits trigger a bounded local relabel with a chunk-set rebuild fallback. They make unreachable requests O(1) (40-110 ns) and are mandatory.
- **Movement classes:** Game-defined Definitions, each with its own passability rule; structures built lazily per class on first use. Per-World limit (default 32, hard ceiling 256).
- **Two-level chunk-graph HPA*** for unique goals (paths approximate, within a few percent of optimal), refined near the unit by local A*.
- **Bounded flow Fields** for shared goals: goal chunks plus a radius, built lazily, with an LRU cap under the Field memory budget; units outside the bound use HPA* to reach the edge.
- **Layered repair:** per-chunk dirty marking, lazy repair on read, a per-Tick work budget with a staleness bound, and an up-front rebuild-vs-incremental threshold (prototype: CPU incremental repair can cost more than a rebuild).
- **Queries:** `Reachable(from, to, movement class)` in constant time; approximate path cost; budgeted deferred path request/result; and a "next step toward goal" query.
- **Request queue** processed in canonical order (priority class, then request ID) under a budget counted in work units (nodes expanded), results delivered N Ticks later; a missed deadline fails with a deterministic event. A request through an Unloaded chunk fails with "needs loading" carrying the chunk coordinate.
- Flow Fields never cross an Unloaded chunk: such a Field reports `NeedsLoading` with the first missing chunk, and a step into or toward one returns `FlowStepStatus.NeedsLoading`.
- Tier 1 deterministic (integer costs, ties by neighbour index). Caches are not saved, except the flow Field store (section 7.1).

Games own unit movement; a path-follow helper is provided.

### 9.2 Jobs and colony AI ("Decide job assignment and colony AI building blocks")

- **Claim** is a built-in Relation owned by the target (claimant, target, slot) with capacity, optional amount and release on job end, claimant death or target destruction. Two phases: agents propose in parallel; a deterministic resolve step (sorted by priority key then Entity ID, per target chunk bucket) grants or denies. Denied agents rescan next decision.
- **Work index:** per-work-type, per-chunk lists of available work items with deterministic ordering and change tracking, queried by radius-bounded bucket search ("nearest N available of types").
- **Optional hauling matcher** (supply/demand, producing haul work items).
- **Decision scheduling:** agents are woken (hashed cadence or trigger), with a per-System decision budget carrying remainders forward.
- **Anti-thrash defaults:** commitment window, per-agent cooldowns on failed targets, job-start circuit breaker (all replaceable).
- **Optional helpers:** a lexicographic priority-table chooser and utility scoring. No behavior trees or GOAP/HTN in v1.
- Needs, mood, work types, skills and job content are Game author design. Claims, work items and cadence are ordinary saved Components/Relations; work-index lists and reachability caches are rebuilt on load.

### 9.3 Procedural generation ("Decide procedural generation architecture")

- Generation is a pure function of (seed, chunk coordinate) through the ChunkProvider contract: any chunk alone, in any order, on any thread, same bytes.
- **Pipeline:** named stages, each declaring the Fields it reads and writes, ordered by before/after name; Plugins register and (if Overridable) replace stages.
- **Randomness:** counter-based RNG keyed by (seed, stage name hash, chunk or region coordinate, use index); adding a stage never shifts another's numbers.
- **Cross-chunk features:** region-keyed placement for large features; halo reads over neighbours' base data only; a deterministic world-level pre-pass for global solves (for example drainage graphs), cached as a generated Resource.
- **Entities** come from placement stages with IDs derived from (seed, stage, coordinate, index).
- **Validation:** golden hashes per stage and seed, order-independence and seam checks, extensible connectivity validators, per-stage timing.
- **Ships:** integer/fixed-point noise utilities (value, gradient, simplex, cellular, fBm, domain warping, region-keyed spot sampling), pipeline machinery and test tooling. **Optional packages:** a graph/network toolkit and a WFC helper for small bounded content. Terrain rules and content are Game author territory.
- The manifest records a generation version; regenerating a chunk is valid only if it matches. Float noise is excluded from the deterministic path.

## 10. Tooling

### 10.1 Profiling and diagnostics ("Decide profiling and diagnostics")

- Fixed always-on numeric counters in a preallocated per-Tick record ring: Tick and Phase timings, per-System time and chunks visited/skipped, worker busy/spin/park, memory vs budget, command-buffer sizes, plus streaming counters (residency counts, load queue depth, stalls) and pathfinding counters (request queue, work units, repair backlog). Versioned layout.
- Cost levels: Off, Standard (default, under about 1% of Tick time), Detailed.
- Allocation-free in-process read API (`world.Diagnostics`) with opt-in sinks: EventSource/Meter bridge, Perfetto trace export, and the inspection protocol.
- First-class all-thread allocation check; optional Tick budget with slow-Tick reports; structured allocation-free logging (message IDs plus numeric args, drained between Ticks; no wall-clock time).
- Flag-controlled validation switches (ref checks, poisoning, leak checks, access-declaration checking, determinism spot-checks); optional hash trail per Tick/Phase/System/archetype for localising divergence.
- Plugins register named counters and timed sections in `Register`.

### 10.2 Inspection ("Decide debug and inspection tooling")

One versioned inspection protocol (compact binary with a JSON mapping), served in-process or over a localhost socket/pipe; host-started, off by default, compiled out under `COLONY_NO_INSPECT`; token plus read-only by default, `AllowMutation` opt-in. Reads at Tick boundaries (schema, Entities, Fields, Resources, events, spatial queries, diagnostics). Mutation only as debug commands through the command buffer, recorded in the replay log. Pause/step, replay-based rewind, hash-trail desync finder, World/save diff, data-not-pixels visualisation, Plugin-registered inspectors, and a `colony-inspect` CLI as the reference client. No GUI.

### 10.3 Testing and benchmarks ("Decide testing and benchmark strategy")

- **Six layers, each its own project:** unit/property; simulation determinism; contract and conformance (Plugin ordering, C ABI); compile-time (generator snapshots, analyzer diagnostics); soak/stress; benchmarks. Test framework: the current Microsoft.Testing.Platform default (xUnit v3 or TUnit), chosen at implementation time.
- **Determinism harness** (shipped as a package Plugins can run): seeded scenarios across 1/2/4/N threads, ISA settings and Native AOT; a negative control that must fail; committed golden hashes compared across OSes; save-load-continue equals never-saved. Added scope: replay equivalence, a simulated two-peer lockstep test, streaming evict/reload round trips and load-deadline stalls, pathfinding scenarios (unreachable targets, toggles, budget exhaustion).
- **Allocation harness** over every thread; debug memory validations; optional nightly use-after-free stress.
- **ABI conformance:** a C host loads the Framework, registers a Native Plugin and exercises every entry on JIT and AOT; `colony.h` is checked in and diffed in CI.
- **Benchmarks:** BenchmarkDotNet micro-benchmarks plus a custom Tick harness (100k/1M colonists, churn, Fields, save/load) with JSON output. Performance is tracked, not gated, on shared runners; hard gates only on deterministic counts. A dedicated machine runs nightly comparisons.
- **Model-based tests** against slow reference implementations (dictionary ECS, naive grid). Hashes prove sameness, not correctness.
- **CI:** PR matrix Windows x64 and Linux x64 on JIT; nightly adds Native AOT, ISA variants, ARM64 Linux, soak. CI runs on GitHub Actions; the dedicated benchmark machine is a self-hosted runner.
- Fixtures: a non-shipped test content package, scenarios defined by seed plus parameters, committed golden saves per container version.

## 11. Public API shape

- `WorldBuilder` is the single composition root: it registers Components, Systems, Fields, Plugins, budgets, determinism flags and the ChunkProvider, validates at build time (layout hashes, access conflicts, Plugin ordering, Field names) and produces an immutable `WorldSchema` shared across Worlds. Registration closes before the first Tick.
- `World`: create, `Tick`, `Submit`, `Save`/`Load`, `Diagnostics`, accessors for Resources, Events, Fields and Definitions.
- Systems and Components are attributed partial structs/methods processed by the generator. Everything not needed by Games, Plugins or generated code is `internal`.
- Generators and analyzers ship as one package; generated code lives in the user's assembly and uses only public surface.

### 11.1 Hello colony (illustrative sketch)

This shows intended shape, not final signatures. It becomes the first sample and a compile-checked doc test.

```csharp
[Component] public partial struct Hunger { public Fixed32 Value; }
[Component] public partial struct Food   { public int Units; }

[Field("heat")] public partial struct Heat { public Fixed32 Value; }

public static partial class ColonySystems
{
    [System(Phase.Simulate, Every = 1)]
    public static void Starve(ref Hunger hunger)
        => hunger.Value += Fixed32.FromRatio(1, 600);
}

var schema = new WorldBuilder()
    .Seed(12345)
    .AddComponent<Hunger>().AddComponent<Food>()
    .AddField<Heat>()
    .AddSystems<ColonySystems>()
    .Build();                                  // validates, freezes the schema

using var world = schema.CreateWorld();
for (int i = 0; i < 100; i++) world.Create(new Hunger());

for (int tick = 0; tick < 1000; tick++) world.Tick();

world.Save("colony.save", SaveOptions.Default);
Console.WriteLine(world.Hash());               // identical on every machine
```

The sketch as it compiles is `samples/HelloColony/Sketch.cs`; `SpecSketchDocTests` diffs the two line by line and fails on any drift beyond these exact deviations:

- **Fields:** there is no `[Field]` attribute; `Heat` is a plain `public struct`, registered by name on the Fields builder: `builder.AddFields(new WorldSize(64, 64, 1)).AddField<Heat>("heat")` replaces `.AddField<Heat>()`.
- **Phase in attributes:** `[System(NamedPhase.Simulate, Every = 1)]`, since an attribute argument must be a constant and `Phase` is not one.
- **Systems:** the generator emits `ColonySystems.AddSystems(builder)`, a statement, so the chain breaks into `var builder = ...;`, `ColonySystems.AddSystems(builder);` and `var schema = builder.Build();` in place of `.AddSystems<ColonySystems>()`.
- **Seed:** a World option, `schema.CreateWorld(new WorldOptions { Seed = 12345 })`, not `.Seed(12345)` on the builder (the seed is per World, the schema is shared).
- **Creating Entities:** through a command buffer: `var commands = world.CreateCommandBuffer();`, `commands.Create(ComponentMask.Of(schema.Component<Hunger>()))` in the loop, then `world.Playback(commands);`. There is no `world.Create(new Hunger())`.
- The compiled sketch also ends with `return world.Hash();` so the doc test can check the run.

## 12. GPU: deferred, with a seam

The GPU work done during mapping (Backend abstraction, wgpu-native, Exact/Approximate Kernels, GPU Field and pathfinding prototypes) is retained as research. Findings: dense GPU-authoritative Fields won about 15x (2.1 ms vs about 32 ms); a CPU-authoritative mirror never wins (PCIe-bound); shared-goal flow Fields won only about 2x; per-unit GPU queries lost 17-20x to CPU HPA*; float Kernels differed up to 5 ULP between Vulkan and DX12 while integer Kernels were bit-identical.

Decision: v1 is CPU-only. The gain does not justify bindings, a second Kernel implementation, an authority model, device-loss handling and a test matrix before a real workload needs it. Kept seams: Fields behind a Field interface with the CPU implementation as oracle; the Kernel/Backend vocabulary in the glossary; integer-distance and fixed-point formulations; the Exact/Approximate rule (only Exact Kernels run in deterministic Worlds). A later optional `Colony.Gpu.*` package starts from `docs/research/prototype-gpu-field.md`, `prototype-gpu-pathfinding.md` and the superseded GPU Backend ticket (wgpu-native with own generated bindings, CPU default with explicit per-Field GPU opt-in, transfer budgets, staging ring plus scatter Kernel, Vulkan default).

## 13. Prototype evidence

| Prototype | Result | Report |
|---|---|---|
| ECS storage and scheduler | 100k colonists 1.02 ms/Tick single-thread, 0.20 ms on 12 threads; 1M colonists 7.8 ms single-thread; zero allocation; bit-identical across 1/2/4/16 threads and JIT/AOT. Weak spots: scaling, serial playback, AOT default ISA (fixed by x86-64-v3). | `prototype-ecs-bench.md` |
| GPU Field | See section 12. | `prototype-gpu-field.md` |
| GPU pathfinding | See sections 9.1 and 12. Region IDs mandatory; plain A* infeasible at scale; full-World Fields break at 512x512x64. Big-World hybrid and thread rows were not run. | `prototype-gpu-pathfinding.md` |

Surveys: `ecs-designs.md`, `gpu-compute-options.md`, `dotnet-11-performance-features.md`, `colony-sim-prior-art.md`, `procgen-approaches.md`, `colony-ai-and-jobs.md`. Caveats: prototypes were single-machine (i9-9900KF, RTX 2060 SUPER, Windows 11); the ECS prototype did not exercise enable bits, Relations, Fields, change detection or access enforcement; read numbers as orders of magnitude.

## 14. Repo structure and tooling

One solution: `src/<Package>/`, `tests/<Package>.Tests/`, `benchmarks/`, `samples/` (a minimal headless host and a C native host), `docs/`, `prototypes/` (throwaway, never referenced or built by the solution), `tools/` (development-only tooling, never referenced by `src/`, not shipped), central package management, shared `Directory.Build.props` (analyzers, AOT and determinism flags) and `global.json` pinning the SDK. Licensed MIT (`LICENSE` at the repo root); hosted at `github.com/jcannon98188/ColonyFramework`; CI on GitHub Actions.

## 15. Implementation hand-off

Ticket seams in dependency order. Each item is tracked in Backlog as the listed tasks (all labelled `ready-for-agent`, with native dependency edges); the task bodies carry the acceptance criteria.

1. **Repo skeleton:** solution, build props, analyzers, CI dependency check, `Colony.Generators` scaffold. — TASK-3.
2. **Core vertical slice:** math layer, fixed-point and RNG (TASK-4); entity storage with slabs, chunks, archetypes and command buffers (TASK-6); scheduler, Phases and Tick, reproducing the ECS prototype's colonist hash across 1/N threads and JIT/AOT (TASK-7); Resources, events, enable bits and rates (TASK-8); Relations, buffer Components, string table and blobs (TASK-9); determinism and allocation harnesses in `Colony.Testing` (TASK-10).
3. **Generators and analyzers:** determinism analyzers (TASK-5, can start right after the skeleton); source generator for Components, Systems and typed queries (TASK-11).
4. **Fields and spatial index:** Field chunks, stencils, uniform chunks and change feed (TASK-12); spatial index (TASK-13).
5. **Persistence:** save container and migrations (TASK-14); Input Commands and replay log (TASK-15).
6. **Plugins and Definitions:** manifest, Register, Overridable Systems, Definition registry, `Colony.Abstractions` (TASK-16).
7. **Diagnostics:** record ring, logging, allocation check, validation switches, hash trail (TASK-17); EventSource/Meter and Perfetto sinks (TASK-18).
8. **Pathfinding:** movement classes, region IDs and Reachable (TASK-19); chunk graph, HPA* and request queue (TASK-20); flow Fields and layered repair (TASK-21).
9. **Colony building blocks:** claims and decision scheduling (TASK-22); work index, hauling matcher and chooser helpers (TASK-23).
10. **ProcGen:** noise utilities (TASK-24, depends only on the math layer); pipeline, ChunkProvider and validation tooling (TASK-25).
11. **Streaming:** residency manager and evict/reload round trip (TASK-26); background loaders, eviction budget and catch-up (TASK-27).
12. **C ABI:** `Colony.Native`, header generation, conformance suite (TASK-28).
13. **Inspection:** protocol, server, `colony-inspect` (TASK-29).
14. **Benchmarks and sample host:** BenchmarkDotNet suite, Tick harness, hello-colony headless host (TASK-30).
15. **Optional early tooling:** the warm-Roslyn dev daemon (TASK-2; standalone, fresh implementation, `tools/`), blocked on the harnesses and generator so there is a build/test loop to measure.

## 16. Open items and risks

- Prototype gaps listed in section 13; re-measure on the real implementation.
- Background-save copy-on-write cost was not measured (the ECS prototype did not cover it).
- Two Native AOT runtimes in one process (AOT-built Plugin libraries) is untested.
- Possible later work, not specified: time/calendar helpers, event/notification conventions, snapshot ring and rollback, state-replication networking, a GPU package, GOAP/BT libraries.
- ADRs: `docs/adr/0001-archetype-chunk-storage.md`, `0002-tier-1-bit-exact-determinism.md`, `0003-cpu-only-v1.md`.
