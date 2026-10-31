# benchmarks/

Performance is tracked, not gated (spec section 10.3): BenchmarkDotNet micro-benchmarks and a custom Tick harness
write results a dedicated machine compares nightly. Hard gates apply only to deterministic counts (allocations,
chunks visited, World hash, save/load round trip). Both projects are in `Colony.slnx`, built by CI, never shipped,
and never referenced by `src/`. They link the shared scenario sources from `tests/` (the colonist oracle and the
Field scenario) instead of referencing test projects.

## Micro-benchmarks: `Colony.Benchmarks`

One command runs them all (Release; results in `BenchmarkDotNet.Artifacts/`, Markdown plus full JSON):

```
dotnet run -c Release --project benchmarks/Colony.Benchmarks
```

Add BenchmarkDotNet options after `--`, e.g. `-- --filter "*Hashing*"` or `-- --list flat`. Benchmarks run
in-process (`InProcessEmitToolchain`), with `MemoryDiagnoser`, 3 warm-up and 10 measured iterations per case.

| Class | Covers |
|---|---|
| `StorageBenchmarks` | churn of 1000 colonists through a command buffer, Remove+Add archetype moves, query iteration over 100k |
| `SchedulerBenchmarks` | the whole colonist Tick (100k, 1% churn) on 1 worker and on all physical cores |
| `DiagnosticsOverheadBenchmarks` | TASK-17's deferred gate: Off (baseline) vs Standard diagnostics on 1 and 4 workers |
| `FieldsBenchmarks` | the Field scenario Tick (double-buffered diffusion + in-place Life, 40 chunks), accessor writes |
| `HashingBenchmarks` | `Hash64` over 64 B / 4 KiB / 64 KiB, Philox blocks, `RandomSource` draws, `World.Hash()` of 100k colonists |
| `PersistenceBenchmarks` | `SaveToBytes` and `Load` of 10k colonists, LZ4 and uncompressed |

`Colony.Benchmarks.Tests` smoke-runs every `[Benchmark]` once (setup, one call, cleanup) so the suite cannot rot;
it never runs BenchmarkDotNet.

## Tick harness: `Colony.TickHarness`

```
dotnet run -c Release --project benchmarks/Colony.TickHarness -- --out results.json
dotnet run -c Release --project benchmarks/Colony.TickHarness -- --out results.json --baseline nightly.json
```

Options: `--scenario colonist-100k|colonist-1m` (default: both), `--colonists N`, `--food N`, `--ticks N` (60
measured), `--warmup N` (100), `--workers N` (1; 0 = physical cores), `--diagnostics Off|Standard|Detailed`
(Off), `--no-fields`, `--no-save`, `--out file.json`, `--baseline file.json`, `--list`.

Each scenario composes the colonist oracle (`tests/Colony.Core.Tests/Scenario/ColonistScenario.cs`: wander,
eat, starve, recover, 1% churn per Tick) together with the Field scenario (`tests/Colony.Testing.Tests/Scenarios/
FieldScenario.cs`: integer diffusion and 3D Life stencils over 40 chunks) in one World, warms it up, measures N
Ticks, then saves to bytes, loads into a fresh World and Ticks both once more.

The JSON report holds `Machine` (name, OS, architecture, cores, runtime, Vector256), `Commit` (`git rev-parse
HEAD`, else `GITHUB_SHA`), `Timestamp` and one run per scenario: `Scenario` (sizes, workers, Ticks, diagnostics
level, Fields on/off), `Timing` (mean/p50/p90/p99/min/max ms per Tick), `Counts` (main-thread and worker
allocated bytes, chunks visited and skipped, Entities, World hash), `SaveLoad` (save/load ms, bytes, loaded and
continued hash matches), `Gates` and, with `--baseline`, `Comparison` (timing deltas in percent).

Gates (exit 1 when any fails; `Gates.cs`):

- `allocations`: zero managed bytes on the Tick thread and the workers over the measured Ticks.
- `save-load`: the loaded World hashes like the saved one and still agrees after one more Tick.
- `chunks-visited` and `world-hash` (with `--baseline`): identical to the baseline run of the same scenario and worker count.

Timing is compared against the baseline and printed, never gated.

## Baselines

Prototype numbers (spec section 13, `prototypes/ecs-bench`, i9-9900KF): 100k colonists 1.02 ms/Tick
single-threaded, 1M colonists 7.8 ms/Tick single-threaded; zero allocation. The prototype had no enable bits,
Relations, Fields, events, change detection or access enforcement, so the numbers below are not like for like.

Re-measured on the real implementation (same machine: i9-9900KF, 8 physical / 16 logical cores, Windows 11,
.NET 11 RC2, Release, JIT, Fields on, 100 warm-up + 60 measured Ticks; TASK-30 report, 2026-10-31):

BASELINE_TABLE

Off-vs-Standard diagnostics overhead (TASK-17's 1% gate, `DiagnosticsOverheadBenchmarks`, 100k colonists):

OVERHEAD_TABLE

A `tick-harness.json` from the nightly machine is the authoritative record; this table is the hand-off snapshot.
