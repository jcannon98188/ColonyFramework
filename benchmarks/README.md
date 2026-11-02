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

| Scenario | Workers | Fields | Mean ms/Tick | p50 | p99 | Alloc | Chunks visited | Save / Load | vs prototype |
|---|---|---|---|---|---|---|---|---|---|
| colonist-100k | 1 | on | 16.748 | 16.751 | 18.155 | 0 B | 21 750 | 56.4 ms (1.89 MB) / 41.4 ms | n/a (prototype had no Fields) |
| colonist-100k | 1 | off (`--no-fields`) | 1.649 | 1.610 | 2.193 | 0 B | 21 750 | - | 1.02 ms -> 1.65 ms (+62%) |
| colonist-100k | 8 (all cores) | on | 6.246 | 6.486 | - | 0 B | 21 750 | - | - |
| colonist-100k | 8 (all cores) | off | 0.727 | 0.589 | 4.892 | 0 B | 21 750 | - | - |
| colonist-1m | 1 | on | 34.851 | 34.759 | 36.178 | 0 B | 216 195 | 133.7 ms (17.1 MB) / 106.2 ms | n/a |
| colonist-1m | 1 | off | 19.560 | 19.463 | 21.448 | 0 B | 216 195 | - | 7.8 ms -> 19.6 ms (+151%) |
| colonist-1m | 8 (all cores) | on | 11.829 | 12.287 | - | 0 B | 216 195 | - | - |

The two Field stencils (Diffuse double-buffered + Life in-place over 40 chunks) accounted for roughly 15 ms of the
single-threaded 100k Tick; the ECS part alone is 1.65 ms. The 1M scenario is ~2.5x slower than the prototype
single-threaded, attributable to enable bits, change filters, Relations and access enforcement that the prototype
did not have. The prototype cap of 1.02 ms is therefore not met by the full implementation. The Fields rows above
are superseded by the TASK-38 table below; track regressions against the newest numbers with `--baseline`.

### Fields stencil pass (TASK-38, 2026-11-01)

Profile of the TASK-30 numbers (same machine; Fields-only World of the Field scenario, 1 worker, thread cycle
counts via `QueryThreadCycleTime` so a loaded machine does not skew the split):

| Part of the Fields Tick | Mcycles/Tick | Share | What it is |
|---|---|---|---|
| Life in-place stencil (40 chunks x 4096 cells x 26 `Read`s) | 49 | 83% | kernel neighbour reads |
| Diffuse double-buffered stencil (40 x 4096 x 7 `Read`s) | 9 | 15% | kernel neighbour reads + `CellsEqual` + commit copy |
| Stencil bookkeeping with empty kernels (expand, stamp, `RunParallel` x10, change feed) | 0.9 | 1.5% | framework |

So 98% of the Fields cost was `StencilInput<T>.Read`: it was never inlined (one call per neighbour, 4.3 M calls
per Tick for Life) and did a slot computation, two `InlineArray` range checks and a uniform/dense branch per
call, about 12 cycles per read. Hashing is not per Tick and the change feed is 80 version compares; neither shows.
Target set from the profile: halve the Fields share of the 100k single-worker Tick (about 16 ms -> 8 ms).

Change: `StencilInput<T>` now gathers the one-cell halo (18x18x18, boundary baked in) into a per-thread native
scratch block in its constructor (324 row copies) and `Read` is forced inline as a live centre load (in-place mode),
a halo load for `[-1, 17)`, or the old pointer-table path for anything further out. No World hash, golden or save
changed (`fields`, `streaming*` goldens, `world-hash` and `chunks-visited` gates all equal).

Re-measured A/B in one session, both binaries at high process priority, p50 ms/Tick (old -> new):

| Scenario | Workers | Fields | Old p50 | New p50 | Delta | Alloc | Chunks visited | Hash |
|---|---|---|---|---|---|---|---|---|
| colonist-100k | 1 | on | 18.081 | 12.963 | -28% (Fields part 16.1 -> 11.0 ms, 1.46x) | 0 B | 21 750 | `0x5C04E91ED56A9470` (unchanged) |
| colonist-100k | 8 | on | 7.114 | 4.689 | -34% | 0 B | 21 750 | unchanged |
| colonist-1m | 1 | on | 36.340 | 31.197 | -14% | 0 B | 216 195 | `0x89AA1AAFF646FB06` (unchanged) |
| colonist-1m | 8 | on | 14.707 | 11.700 | -20% | 0 B | 216 195 | unchanged |

Fields-only World (Field scenario Tick, 1 worker): 59 -> 35..40 Mcycles (Life 49 -> 29..34, Diffuse 9 -> 5.5). The
target of 2x was not fully met: the remaining cost is the per-cell `Read(x, y, z)` API itself (the two tier tests and
the index math on every call). A kernel that gathers the same halo itself and indexes it directly runs Life in 14
Mcycles, so exposing the halo (or row spans) to kernels is the next step and a follow-up ticket, not this pass.

Off-vs-Standard diagnostics overhead (TASK-17's 1% gate, `DiagnosticsOverheadBenchmarks`, 100k colonists):

| Measurement | Off | Standard | Overhead |
|---|---|---|---|
| BenchmarkDotNet `TickOff` / `TickStandard`, Workers=1 (10 iterations, CI 99.9% +-3%) | 1.695 ms | 1.699 ms | +0.2% |
| Tick harness colonist-100k, Workers=1, Fields on (mean / p50) | 16.748 / 16.751 ms | 16.754 / 16.590 ms | +0.04% / -1.0% (noise) |

Standard diagnostics stay well under TASK-17's deferred 1% gate; the difference is inside the run-to-run noise of
both tools. The Workers=4 BenchmarkDotNet case did not complete in the hand-off run (see the TASK-30 report).

A `tick-harness.json` from the nightly machine is the authoritative record; this table is the hand-off snapshot.
