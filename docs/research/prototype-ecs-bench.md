# Prototype: ECS storage, memory model and scheduler benchmark

Throwaway prototype answering one question: **does the decided ECS storage, memory model and scheduler hit the targets?** Code: `prototypes/ecs-bench/` (not production quality). Raw output: `prototypes/ecs-bench/results/`. Date: 2026-10-30.

## Summary verdict

**Yes on the main target, with caveats on scaling.**

- **Tick time.** 100,000 colonists wandering and eating take **1.02 ms mean, 1.91 ms p99 per Tick on one thread**, and **0.20 ms mean, 0.34 ms p99 on 12 threads**. That is 6% of the 16 ms budget on one core. 1,000,000 colonists still fit: 7.8 ms on one thread, 2.8 ms on 8 to 12 threads. With 40,000 structural changes per Tick on top of the 100k colonists, a Tick takes 2.9 ms on one thread and 1.9 ms on 8 threads.
- **Zero managed allocation per Tick: hit.** The Tick threads (main plus every worker) allocated **0 bytes** across 1,000 measured Ticks in every configuration, and no GC ran. The process-wide counter shows 336 to 672 B per 1,000 Ticks. That matches a runtime background baseline of about 104 B per second measured while the main thread sleeps, so it is not Tick work. Reaching zero needed one fix: parking workers on `ManualResetEventSlim` allocated managed objects (details below).
- **Determinism: hit.** World hashes are bit-identical across 1, 2, 4 and 16 threads at Ticks 1, 10, 50, 100, 250 and 500, both with and without the churn stress. They are also identical across the JIT build, a Native AOT build with no AVX, and a Native AOT build with AVX2. A negative control that keys the RNG by worker thread fails the test as it should.
- **Scaling across cores: disappointing.** Speedup peaks at **5.1x on 12 threads** for 100k colonists, **2.8x** for 1M (memory-bandwidth bound) and **1.5x** under structural churn, because command playback is serial. **Using all 16 logical CPUs is slower than using 6** (0.48 ms mean against 0.25 ms), with bimodal roughly 1 ms Ticks. Nearly all the speedup comes from splitting Systems by chunk. Running different Systems in parallel helps little, because realistic colonist Systems write overlapping Components.
- **Where the time goes.** At 100k colonists the framework is not the bottleneck. Two game-logic Systems take about 80% of single-thread time: the Eat System's food lookup at 0.60 ms and the Wander System's scalar scan at 0.23 ms. The SIMD column Systems take 0.01 to 0.04 ms each. Scheduling overhead is a few µs per wave.

Machine: Intel Core i9-9900KF (Coffee Lake, 8 cores / 16 logical, AVX2, no AVX-512), Windows 11 Pro 10.0.26200, .NET 11.0.0-rc.2.26504.105 (SDK 11.0.100-rc.2.26504.105). It is a normal desktop with Discord, Steam, Godot and VS Code running, not an isolated benchmark box, so tails and maximums include OS noise.

## Method

### What was built

`src/Colony.Ecs` (the mini framework, about 900 lines):

- **Storage.** Archetype/chunk storage with structure-of-arrays columns inside 64 KiB chunks. Column 0 holds the Entity IDs. Each column starts on a 64-byte boundary, and chunk capacity is a multiple of 16 elements. Chunks come from 4 MiB slabs (`NativeMemory.AlignedAlloc`) with a LIFO free list. Chunks stay dense through swap-back removal. Components are `unmanaged` structs, with a signature limited to 64 Component types (a `ulong` mask). The colonist archetype (Entity 8 B + Position 8 + Target 8 + Hunger 4 + Health 4) gets exactly 2,048 entities per chunk with zero bytes of padding.
- **Entity.** 64-bit Entity ID: 32-bit index plus 32-bit generation, with an index free list.
- **Command buffers.** Structural changes (create, destroy, add and remove Component) go only through byte-encoded `CommandBuffer`s. There is one buffer per (System, job), and the job index is the chunk ordinal in archetype-ID then chunk order, so it does not depend on thread count. Buffers are played back at each Phase boundary in System-ID order, then job order. Stale Entities are skipped and counted.
- **Systems.** Each System declares `All`/`None` query masks, Component read/write masks and Resource read/write masks in data. Systems get per-chunk column spans (`Column<T>()`, or `PaddedColumn<T>()`, which is rounded up to 16 elements for SIMD with no scalar tail). The plumbing is hand-written, with no source generators.
- **Scheduler.** There are three fixed Phases (Pre, Update, Late). Within a Phase, Systems are ordered by ID. A System that conflicts with an earlier one goes into a later "wave" (level = 1 + the maximum level of earlier conflicting Systems), which is a stable topological order with ties broken by System ID. All chunk jobs of every System in a wave go into one job list. The worker pool is N-1 dedicated threads plus the caller. They drain the list through a shared atomic index, one job at a time ("simple chunked", not per-thread deques). Idle workers spin for about 200 µs, then park on a kernel `AutoResetEvent`. Per-chunk reductions write into per-job slots, and `EndTick` combines them in job order. The per-System "every N Ticks with offset" rate is implemented but not used by the benchmark.
- **Determinism tools.** A Philox4x32-10 counter-based RNG keyed by (seed, System ID, Entity index and generation, Tick, stream). No System uses `System.Random`. The World hash is xxHash64, implemented locally and chained over chunk contents in archetype-ID then chunk order. It covers live rows only, so padding is excluded.

`src/Colony.Bench` holds the scenario and the host loop, which calls `World.Tick()`.

### Scenario

- **World.** 512x512 tiles, with fixed-point positions (1 tile = 256 units).
- **Entities.** 100,000 colonists (Position, Target, Hunger, Health) and 5,000 food Entities (Position, Food).
- **Systems,** with the wave layout the scheduler produced:

```
Pre:    [0:FoodIndex] -> playback
Update: [1:Hunger, 2:Move] -> [3:Eat] -> [4:Wander, 5:Starvation] -> playback
Late:   [6:Stats, 7:FoodSpawner] (+ [8:Churn] in the churn stress) -> playback
```

| System | Access | What it does |
|---|---|---|
| FoodIndex | R Position, Food; W Resource FoodGrid | Single job. Rebuilds a uniform grid of food (8x8-tile cells, 64x64 cells) in CSR form with a counting sort. |
| Hunger | W Hunger | `Vector256<float>` add over the padded column. |
| Move | R Target, W Position | `Vector256<int>` per-axis clamp step toward Target. |
| Eat | R Position, R FoodGrid; W Hunger, Target, Resources ColonyStats and EatenFood | Hungry colonists (Hunger ≥ 50) get an approximate-nearest food lookup (ring search over the grid, one extra ring after the first hit). Within one tile they `Destroy(food)` via the command buffer and reset Hunger. |
| Wander | R Position, Hunger; W Target | Fed colonists at their Target pick a random tile (Philox). |
| Starvation | R Hunger; W Health | Branchless `Vector256` compare-and-add. |
| Stats | R Hunger, Health; W ColonyStats | Per-chunk reduction combined in chunk order. |
| FoodSpawner | R Food, R EatenFood | Single job. `Create` commands bring food back to 5,000, regrowing within ±8 tiles of where food was eaten. |
| Churn (stress only) | W Lifetime | 100k debris Entities. Every Tick about 10% are destroyed and recreated, 10% gain a `Marked` tag and 10% lose it: 40,364 structural operations per Tick in total. |

In steady state the colony eats about 195 meals per Tick and about 2% of colonists are hungry at any time.

### Measurement

- **Process.** Release build with TieredPGO on by default, workstation GC, non-concurrent.
- **Warm-up and samples.** Each configuration builds a fresh World from seed 12345, warms up for 500 Ticks, then runs a full GC and measures 1,000 Ticks, timing each Tick with `Stopwatch`. The 1M run used the same counts. Every thread count ran 3 times, with repeats interleaved across thread counts. Tables show the median over the 3 runs, and Max is the worst run.
- **Per-System time.** `Stopwatch` timestamps around every job, summed over threads. It is CPU time, not wall time.
- **Allocations.** `GC.GetAllocatedBytesForCurrentThread()` on the main thread, plus the same call sampled by each worker after every wave ("Tick threads"). `GC.GetTotalAllocatedBytes(precise: true)` deltas give the process-wide figure. GC collection counts were also recorded.
- **Memory.** Peak chunks in use, slab bytes reserved and process working set.

## Results

### 100k colonists, 5k food (JIT)

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Speedup (mean) | Fits 16 ms? |
|---|---|---|---|---|---|---|
| 1 | 1.019 | 0.930 | 1.912 | 2.147 | 1.00x | yes |
| 2 | 0.557 | 0.510 | 1.023 | 1.097 | 1.83x | yes |
| 4 | 0.318 | 0.291 | 0.571 | 0.765 | 3.20x | yes |
| 6 | 0.253 | 0.238 | 0.448 | 0.508 | 4.03x | yes |
| 8 | 0.223 | 0.212 | 0.362 | 0.886 | 4.57x | yes |
| 12 | 0.199 | 0.186 | 0.340 | 0.773 | **5.11x** | yes |
| 16 | 0.477 | 0.196 | 1.126 | 2.984 | 2.14x | yes |

Per-System CPU ms per Tick, summed over threads (49 chunk jobs per System, single jobs for FoodIndex and FoodSpawner):

| Threads | FoodIndex | Hunger (SIMD) | Move (SIMD) | Eat | Wander | Starvation (SIMD) | Stats | FoodSpawner |
|---|---|---|---|---|---|---|---|---|
| 1 | 0.024 | 0.010 | 0.035 | 0.596 | 0.225 | 0.017 | 0.088 | 0.004 |
| 8 | 0.030 | 0.075 | 0.132 | 0.711 | 0.261 | 0.042 | 0.093 | 0.005 |
| 16 | 0.031 | 0.151 | 0.299 | 0.841 | 0.304 | 0.089 | 0.097 | 0.005 |

- **Phase wall time at 12 threads:** Pre 0.031 ms; Update 0.140 ms waves + 0.008 ms playback; Late 0.015 ms + 0.006 ms.
- **Structural changes:** 364 per Tick (about 182 food destroyed and 182 created), costing 0.013 ms of playback in total.
- **Allocation:** 0 B per Tick on Tick threads in every run, 0 GCs.
- **Memory:** 51 chunks (3.2 MiB of column data) in one 4 MiB slab. Process working set is 38 to 50 MiB under JIT and 17 to 19 MiB under Native AOT, mostly runtime.

### 1M colonists, 50k food (scaling with data larger than L3)

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Speedup |
|---|---|---|---|---|---|
| 1 | 7.824 | 7.598 | 14.129 | 35.177 | 1.00x |
| 2 | 4.659 | 4.521 | 8.213 | 19.395 | 1.68x |
| 4 | 3.150 | 3.018 | 5.515 | 11.476 | 2.48x |
| 8 | 2.816 | 2.686 | 4.765 | 9.298 | 2.78x |
| 12 | 2.797 | 2.710 | 4.265 | 8.465 | **2.80x** |
| 16 | 3.519 | 3.473 | 4.615 | 8.810 | 2.22x |

- **Per-System CPU on 1 thread:** Wander 2.55, Eat 2.27, Stats 0.95, Move 0.85, Starvation 0.36, FoodIndex 0.29, Hunger 0.17 ms.
- **The same at 16 threads:** Move 9.86 ms CPU, Wander 8.0. That is 12x CPU inflation for the same work: the SIMD passes are DRAM-bandwidth bound, and extra threads just wait on memory.
- **Memory:** 505 chunks in 32 MiB of slabs. Process peak working set was 243 MiB, but it accumulates across the sequential runs in one process (old Entity tables wait for the GC), so treat it as an upper bound.
- **Playback:** about 3,550 structural operations per Tick cost 0.28 ms.
- **The 1-thread worst case of 35 ms** came from one run and is OS noise; p99 stays at 14 ms. This is the one configuration where single-threaded p99 comes close to the budget.

### Structural-churn stress: 100k colonists plus 100k debris, about 40k structural operations per Tick

Per Tick: 10,182 creates, 10,182 destroys, 10,000 Component adds and 10,000 Component removes (the last two are archetype moves). Four archetypes.

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Speedup | Late-phase playback ms |
|---|---|---|---|---|---|---|
| 1 | 2.896 | 2.785 | 4.198 | 6.068 | 1.00x | 1.30 |
| 2 | 2.348 | 2.297 | 3.362 | 3.845 | 1.23x | 1.30 |
| 4 | 2.026 | 1.984 | 2.624 | 3.138 | 1.43x | 1.30 |
| 8 | 1.950 | 1.928 | 2.799 | 3.163 | 1.48x | 1.32 |
| 12 | 1.896 | 1.851 | 2.816 | 3.408 | 1.53x | 1.35 |
| 16 | 2.494 | 2.490 | 3.480 | 3.807 | 1.16x | 1.39 |

- **Cost per operation:** about **32 ns** on one thread (1.30 ms / 40,364), single-threaded by design, which makes it Amdahl's serial fraction. From 4 threads up, playback is about two thirds of the Tick.
- **Recording is the cheap part:** the Churn System's own recording costs 0.55 ms CPU on one thread. That figure inflates to 1.5 to 3.2 ms CPU at 4 to 16 threads, while its wave wall time stays at 0.3 to 0.4 ms. See the surprises below.
- **Allocation:** 0 B per Tick on Tick threads, 0 GCs, because command buffers only grow during warm-up. Memory peaked at 84 chunks (8 MiB of slabs).

### Determinism

`--mode determinism` runs the seeded simulation for 500 Ticks at 1, 2, 4 and 16 threads and compares the World hash at Ticks 1, 10, 50, 100, 250 and 500.

| Run | Result |
|---|---|
| Base scenario (100k colonists) | PASS, every checkpoint equal; Tick 500 hash `45f6b02630ddc330` |
| With churn (100k debris) | PASS; Tick 500 hash `9e75cd3ac2a420f6` |
| Negative control `--sabotage` (Wander's RNG keyed by worker thread) | FAIL at 2, 4 and 16 threads, as intended |
| Native AOT default ISA (no AVX, so Vector128 and scalar fallbacks run) | Same hashes as JIT |
| Native AOT `IlcInstructionSet=x86-64-v3` | Same hashes as JIT |

### Native AOT

`dotnet publish -r win-x64 -p:PublishAot=true` builds in about 4 s (the ILCompiler package was already in the NuGet cache) with no trim or AOT warnings, and runs correctly: same hashes, zero allocation, 1.6 MB exe.

| Build | 1 thread mean | 8 threads | 12 threads | Working set |
|---|---|---|---|---|
| JIT (TieredPGO) | 1.019 ms | 0.223 | 0.199 | ~40 MiB |
| AOT, default ISA | **2.068 ms** | 0.373 | 0.303 | ~18 MiB |
| AOT, `-p:IlcInstructionSet=x86-64-v3` | 1.080 ms | 0.236 | 0.213 | 17 to 19 MiB |

Default AOT targets a baseline x64 ISA, so `Vector256.IsHardwareAccelerated` is false. Move has no Vector128 path and fell back to scalar, going from 0.035 to 0.96 ms. With the v3 ISA, AOT matches JIT.

## What surprised me

1. **The framework is cheap and game logic dominates.** Storage, iteration and scheduling overhead are noise at 100k: the three SIMD Systems together take 0.06 ms. The cost sits in the per-entity branchy Systems (Eat, Wander) and in spatial queries. An early version of the scenario let colonists strip the map centre of food (random-waypoint movement concentrates density there). Hungry colonists then searched far rings every Tick, and the single-thread Tick grew from 1 ms to **23 ms**, with Eat at 22 ms. The ECS was unchanged. Per-System timing found it immediately, so it needs to be a first-class Framework feature.
2. **Using every logical CPU hurts.** At 16 threads the mean doubles compared with 8 to 12 threads, and Tick times become bimodal (p50 0.2 ms, but many Ticks around 1.0 to 1.1 ms). With 15 workers plus main on 16 logical CPUs, any other process's thread pre-empts a worker that holds a job, and the whole wave waits for it to be rescheduled. Never parking (spin forever) made this far worse: p99 27 ms at 16 threads.
3. **Per-System CPU time grows with threads even for perfectly parallel SIMD work.** Hunger costs 0.010 ms CPU on one thread and 0.151 ms on 16 for identical work. Jobs are tiny (a Hunger chunk is about 0.2 µs), and dynamic job grabbing sends each chunk to a different core every wave, so its cache lines migrate between L2s on every pass. The same thing probably explains the inflation of the Churn System's recording CPU (its command buffers were last read by the main thread during playback). I have not profiled this, so it is a hypothesis.
4. **The `ManualResetEventSlim` park path allocates.** It allocated managed memory (presumably Monitor waiter objects) whenever a worker actually blocked: 280 to 616 B per 1,000 Ticks, more with shorter spin windows. Switching to a kernel `AutoResetEvent` brought worker allocation to exactly 0. The zero-allocation rule needs a CI check that includes worker threads, not just `GetAllocatedBytesForCurrentThread` on the main thread.
5. **Running different Systems in parallel buys little.** Colonist Systems naturally write overlapping Components (Hunger, Target), so the Update Phase serialises into 3 waves. Only Hunger with Move, and Wander with Starvation, share a wave. Chunk splitting gives the speedup, and 100k colonists give only 49 chunks per System, which is thin slack for 12 or more threads.
6. **Serial playback becomes the ceiling under churn.** About 32 ns per operation is fine for a few hundred operations per Tick (the base scenario: 0.013 ms). At 40k per Tick it is 1.3 ms, and the scenario stops scaling beyond 1.5x.
7. **Command buffers cannot express "claim".** Two colonists that reach the same food in the same Tick both get fed; the second `Destroy` is skipped as stale (about 13.5 per Tick here). The result is deterministic but wrong as gameplay. The design needs a deterministic conflict-resolution story for contested Entities.
8. **AOT's default ISA silently halves SIMD performance** (finding 3 of the Native AOT section above), and a missing Vector128 path costs 27x in that System.

## Recommended changes to the decided design

1. **Default worker count:** physical cores, or logical cores minus 1 or 2, and configurable. Do not fill every SMT sibling. Park workers on an allocation-free primitive (a kernel event or `WaitOnAddress`-style wait) after a short, tunable spin.
2. **Chunk-to-thread affinity for split Systems.** Assign chunk i to worker i mod N first, and steal only when imbalanced. Results stay deterministic, because job indices, not threads, key buffers and reductions. This targets the cache-migration inflation in surprise 3. Also add a minimum-entities-per-job knob and fuse tiny jobs.
3. **Make enable bits mandatory, not optional,** for frequent state such as hungry, marked or carrying. Here 20k tag add/remove operations per Tick cost about 0.6 ms of archetype moves that enable bits would make essentially free.
4. **Faster playback.**
   - Bulk paths: reserve N rows per archetype and memcpy columns for batches of creates, and batch destroys per archetype.
   - Optionally a parallel playback stage partitioned by archetype, with a deterministic merge.
   - Deferred-Entity handles from `Create`, which the prototype lacks.
   - Budget structural churn explicitly (about 30 ns per operation per core in this design).
5. **A deterministic contested-Entity rule,** for example "first command in stable order wins; losers get a deterministic failure event next Phase", or a reservation Resource. The command-buffer model alone produced a double-eat bug.
6. **Keep waves, or move to a DAG later.** At this scale wave barriers cost a few µs, so a full DAG scheduler is not worth it yet. The gains are in recommendations 1 to 4.
7. **SIMD/ISA policy:** every hot SIMD path needs a Vector128 fallback (the scalar fallback was 27x slower). AOT builds should set `IlcInstructionSet` (x86-64-v3 on desktop). Deterministic Systems must avoid FMA, reassociated reductions and estimate instructions, so that every ISA path gives identical bits. The hash test should run in CI across ISA configurations (JIT with `DOTNET_EnableAVX2=0`, and AOT).
8. **Treat per-System timing and an allocation check across all threads as Framework features,** not benchmark extras.

## Known shortcuts

- **Not implemented:** enable bits, deferred Entity handles from `Create`, relations, Fields and change detection.
- **Not exercised or not enforced:**
  - The "every N Ticks with offset" rate is implemented but unused.
  - Declared access is trusted, not enforced: `Column<T>()` does not check that the System declared T.
  - Resources are managed classes.
- **Scheduler:** a shared atomic job index rather than per-thread work-stealing deques, and wave-level barriers rather than a DAG.
- **Scenario simplifications:**
  - Food lookup is an approximate nearest (ring search plus one extra ring) over a uniform grid rebuilt each Tick, not an exact nearest and not brute force.
  - Two colonists can eat the same food in one Tick (surprise 7).
  - Food regrows near where it was eaten, to keep a steady state.
- **Storage simplifications:** the Component registry is a static global with at most 64 types and IDs assigned in first-touch order (the bench touches them in a fixed order). There is no archetype edge cache: transitions are a dictionary lookup on the signature. Generation wrap-around is ignored.
- **Measurement limits:**
  - Per-job `Stopwatch` calls are included in the timings.
  - Results are from a busy desktop, not an isolated machine.
  - The working set is process-wide and accumulates across sequential runs.
  - Hashing cost itself was not measured.
- **Build notes:** `net11.0` worked as-is with the RC2 SDK. `dotnet sln add` with two projects in one call wrote an empty `.slnx` on this SDK, so the `.slnx` was written by hand.

## How to rerun

From `prototypes/ecs-bench/`:

```powershell
dotnet build EcsBench.slnx -c Release
$exe = "src/Colony.Bench/bin/Release/net11.0/Colony.Bench.exe"
& $exe --mode bench --repeat 3 --threads 1,2,4,6,8,12,16                    # 100k colonists
& $exe --mode bench --repeat 3 --debris 100000 --threads 1,2,4,8,12,16      # churn stress
& $exe --mode bench --repeat 3 --colonists 1000000 --food 50000             # 1M
& $exe --mode determinism                                                   # 1/2/4/N-thread hash test
& $exe --mode determinism --sabotage                                        # negative control: must FAIL (exit code 1)
& $exe --mode trace --warmup 1000 --ticks 1000                              # scenario steady-state check
# Native AOT
dotnet publish src/Colony.Bench/Colony.Bench.csproj -c Release -r win-x64 -p:PublishAot=true -p:IlcInstructionSet=x86-64-v3 -o artifacts/aot-v3
```

Other options are `--warmup N`, `--ticks N`, `--seed N` and `--food N`. The environment variable `COLONY_SPIN_US` sets how long idle workers spin before parking (default 200 µs).
