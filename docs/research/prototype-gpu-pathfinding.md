# Prototype: GPU vs CPU pathfinding for 10k–100k units

This is a throwaway headless benchmark in `prototypes/gpu-pathfinding/` (C#, .NET 11 RC, wgpu-native v29 on Vulkan). The question is: for 10k, 50k and 100k units on a 3D tile World, does GPU pathfinding beat a well-built CPU design in **total cost per Tick**? The budget is 16 ms. The cost includes upload, readback and dynamic-obstacle repair.

Run 2026-10-30. Terms follow `GLOSSARY.md`:

- **Field**: here, one per-tile integer distance grid for one destination.
- **Kernel**: GPU compute work, paired with a CPU reference implementation.
- **Backend**: wgpu/Vulkan.
- **Tick** and **Entity** as defined there; the units are Entities.

Code copied from `prototypes/gpu-field/` (wgpu interop, device wrapper, benchmark infrastructure). Nothing in that directory was modified.

## Summary verdict

1. **GPU wins only for shared destinations, and only modestly.** With 64 shared destination Fields at 100k units on the 256x256x32 World and 0.001% tiles toggled per Tick, the GPU averages **7.2–9.2 ms per Tick against 12–17 ms** for a tuned multi-threaded CPU flow-field design. It also uses about 1 core instead of 6–8.

   At 0.01% toggles the gap widens: 29 ms vs 86 ms. Neither design holds 16 ms at p99 under toggles: the GPU's p99 is 15–25 ms and the CPU's is 36–52 ms.

   With no toggles both are trivially cheap (GPU 1.2 ms, CPU 2.3 ms per Tick at 100k units). So **the whole contest is about repairing Fields after obstacles change, not about moving units.** The GPU move Kernel for 100k units takes 0.11 ms of Kernel time, or 0.29 ms including readback.

2. **For unique goals the GPU loses badly, and nothing fits 16 ms at 100k units with 2–5% churn.**
   - GPU per-unit queries, done as one Field per goal batched 32 at a time, cost **1.47 ms per goal** on the small World and **16.9 ms per goal** on the big one. That is about 20x and 17x worse than CPU HPA*, which costs 0.065 ms and 1 ms per request at 16 threads.
   - CPU HPA* is the best unique-goal planner measured, but 3000 requests per Tick still costs **160–200 ms** on the small World and **1.5 s** at 50k units on the big World.
   - Plain A* (with a region early-out, 16 threads) costs 1.9 ms per request on the small World and about 30 ms per request on the big World.

3. **The 512x512x64 World breaks every flow-field design.**
   - 64 full-World u16 Fields take 2.15 GB, on the CPU or on the GPU.
   - A full build costs 3.65 s on the CPU and 0.96 s on the GPU.
   - Repair at 0.001% toggles (168 tiles per Tick) costs **38.6 ms on the GPU and 156 ms on the CPU**.

   Fields bounded to areas or chunks would be needed (not prototyped).

4. **Region IDs on the CPU are mandatory and cheap.** The check costs 40–110 ns per request and makes unreachable requests fail in O(1). Without it, one unreachable A* query floods its whole region: **42 ms** small, **437 ms** big, single thread. Incremental region upkeep costs 1.1 ms (small) or 10 ms (big) at 0.001% toggles.

5. **Exactness is achievable.** The GPU distance Fields and the GPU move Kernel match the CPU bit for bit: after build, after incremental repair at every toggle rate, and over 2,700 Ticks of 3000 walking units. This holds on both Worlds.

**Recommended default architecture:** CPU region IDs (always on) → shared destinations use flow Fields on the GPU Backend when one exists. The CPU flow Fields are the bit-exact fallback with the same u16 layout. Unique destinations use CPU HPA* (chunk graph from the region IDs), with a request budget per Tick and a queue. Run the GPU repair in parallel with CPU HPA* planning: the hybrid was the best measured mixed configuration, and the repair hides entirely behind CPU planning. Plain A* is only for short local refinement. Do not build GPU per-unit path queries.

For Worlds the size of 512x512x64, full-World Fields per destination are not viable on either Backend. The next prototype should test bounded or hierarchical Fields, not a faster full-World relaxation.

## Method

### World

- Row-major tile index `x + W*(y + H*z)`. Two sizes:
  - **small** 256x256x32 = 2.1M tiles.
  - **big** 512x512x64 = 16.8M tiles.
- Generator (seed 12345):
  - 12x12 rooms joined by a spanning tree of doors, plus extra doors (35%) and halls (15%) so the World is loop-rich.
  - 2% sealed rooms (unreachable pockets).
  - Rock blobs up to 30% blocked.
  - Stairs: `W*H/1024` per z pair.
  - 2% of tiles are dynamic: doors plus 1% random.
- Result: the main region holds 87–88% of passable tiles. The max distance from a goal is 1568 (small) and 3031 (big).
- An earlier maze-like generator (main region 62%) made heuristic search look artificially bad, so it was replaced.
- Moves:
  - 4-neighbour in-plane, plus vertical between vertically adjacent stair tiles. Uniform cost 1.
  - Fixed tie-break order `-x,+x,-y,+y,-z,+z` everywhere: CPU BFS, GPU, unit descent and A* path recovery.

### Units and Tick

Units are Entities with SoA unmanaged state: position, goal, Field, status, and a path stream. One Tick runs:

1. Toggles.
2. Region update.
3. Service repair (`OnToggles`).
4. Gather requests.
5. Plan (`Process`).
6. Move.

Every unit moves at most one tile per Tick.

### Workloads

| Name | Destinations | Churn | Unreachable requests |
|---|---|---|---|
| A | 64 shared | 0 | 0 |
| B | unique | 0 | 0 |
| C-s | 64 shared | 3% per Tick | 0 |
| C-u | unique | 3% per Tick | 0 |
| D-s | 64 shared | 3% per Tick | 10% |
| D-u | unique | 3% per Tick | 10% |
| M | first half shared, second half unique | 3% per Tick | 10% |

- Shared destinations: 58 goals in the main region plus 6 inside sealed pockets.
- In the D workloads, 10% of requests target a pocket and must fail fast.
- Arrived and Unreachable units request a new goal. Units blocked by a toggled tile re-request their goal (Repath).
- The spawn burst (every unit requests a path on Tick 0) is timed separately.

### Toggles

Rates are 0, 0.001%, 0.01%, 0.1% and 1% of all tiles per Tick, capped at half the dynamic tiles (21k on small, 168k on big).

Toggles **oscillate**: even Ticks toggle a fresh random set and odd Ticks revert it. This keeps the World at its generated state instead of drifting to "half the doors closed", and both Ticks of a pair count as toggle Ticks.

A run with an odd Tick count left the last set applied. This was found partway through, fixed with one unmeasured reverting Tick, and sweep and threads were rerun.

### Timing

- Release build, 5 warm-up Ticks (3 in sweep, 2 in threads).
- Measured: 30 Ticks small and 20 big in the matrix, 15 in sweep, 10 in threads.
- Reported: mean, p50, p99, max of wall time per Tick, plus per-phase means.
- Cores = process CPU time ÷ wall time over the measured Ticks.
- GPU Kernel time comes from timestamp queries.
- Memory = live unmanaged allocations, plus device buffers for the GPU.

### Approaches

1. **CPU region IDs** (`Cpu/Regions.cs`).
   - Each 16³ chunk is split into local connected components by flood fill (stairs included). Each component is a node of the abstract graph. Edges join touching components of face-adjacent chunks.
   - Union-find over the graph gives region IDs, so reachability is an O(1) check.
   - Incremental update relabels only dirty chunks, then rebuilds the small graph and union-find.
2. **CPU A* with region early-out** (`Cpu/AStar.cs`, `AStarService`).
   - Manhattan heuristic (consistent), bucket queue keyed by f, LIFO tie-break.
   - Paged sparse g-value storage per worker. No parent array: the path is recovered by g-descent.
   - Requests are partitioned over 16 workers.
   - Plain A* was run at 10k units only, because a single spawn burst of 10k takes 17 s on small and 280 s on big.
3. **CPU HPA*** (`Cpu/Hpa.cs`, `HpaService`) on the same 16³ chunk graph.
   - Abstract A* over component centroids.
   - Then a windowed refinement: BFS restricted to the next 4 abstract nodes, refined again as the unit walks.
   - Windows are re-planned when a chunk version changes.
   - Paths are not optimal: mean ratio 1.020 small and 1.059 big, worst 1.21 and 1.36.
4. **CPU flow Fields with incremental repair** (`Cpu/FlowFields.cs`). One exact u16 BFS Field per shared destination.
   - Repair is exact in two phases:
     1. Invalidate to a fixed point.
     2. Reseed from finite neighbours, then a sorted-seed + FIFO BFS that also handles decreases.
   - If repair work goes over N/12 queue pushes, that Field is rebuilt instead.
   - One job per Field across 16 threads. Units descend the gradient.
5. **GPU wavefront distance Fields + move Kernel** (`Gpu/GpuFlow.cs`), on wgpu/Vulkan in WGSL.
   - Fields are u16 packed two per u32: the same bytes as the CPU layout.
   - 16x16x1 tile blocks; each 128-thread workgroup loads an 18x18 halo into shared memory.
   - Each block is relaxed **locally to convergence** before the next global step, with Gauss-Seidel order inside the block. This is safe because relaxation is monotone.
   - Global worklists of active blocks, deduped with atomicMax marks. Indirect dispatch sized by a prep Kernel.
   - Up to 32 Fields per batch (2 GiB binding limit); batches run in lockstep.
   - Convergence is checked by reading back a small history every 32 iterations.
   - Repair is the GPU equivalent of the CPU algorithm: a toggle Kernel seeds the lists, an invalidation fixed point runs, then relaxation. Above N/2000 toggles it falls back to a full rebuild.
   - The move Kernel descends all units in one dispatch, with the same tie-break as the CPU.
6. **GPU per-unit queries.** Measured as "one Field per unique goal", batched 32 per dispatch. That is the most GPU-friendly form of a per-unit query. Extrapolation is in the results.
7. **Hybrid** (`HybridService`).
   - Units `[0, n/2)` use a flow service (CPU or GPU) and the rest use HPA*.
   - In the GPU hybrid, the GPU repair runs on its own task while CPU HPA* plans the unique half. The main thread joins before Move.

**Deferred variant:** the GPU move submitted at Tick T is read back and applied at the start of Tick T+1. The cost is one Tick of position latency. Field repair stays synchronous.

### Correctness checks

`verify` mode passes all checks on both Worlds (`results/verify-*.txt`):

- Region IDs equal global BFS components, after the full build and after incremental updates at every rate.
- GPU build equals CPU BFS.
- A* lengths equal BFS distances.
- A* walk equals flow walk.
- HPA* units all arrive.
- CPU incremental repair equals a fresh BFS at 4 toggle rates.
- GPU repair equals CPU repair at 4 rates.
- GPU move Kernel equals CPU move for 3000 units over about 2,700 Ticks.
- 20k units for 60 Ticks with toggles and 3% churn: the GPU pipeline equals the CPU pipeline exactly.

## Results

Machine: Intel i9-9900KF (8 cores / 16 threads), NVIDIA RTX 2060 SUPER (8 GB, driver 610.88, Vulkan), Windows 11 Pro 26200, .NET 11.0.0 RC. All numbers are ms per Tick unless stated. All runs use 16 threads unless stated.

### Matrix, small World (256x256x32), 21 toggles per Tick (0.001%), `results/matrix-small.txt`

Each cell is mean / p99 ms per Tick.

| Approach | Units | A | B | C-s | C-u | D-s | D-u |
|---|---|---|---|---|---|---|---|
| cpu-astar | 10k | 5.8 / 33 | 3.8 / 25 | 575 / 672 | 615 / 766 | 564 / 628 | 611 / 807 |
| cpu-hpa | 10k | 6.2 / 13 | 6.3 / 14 | 21.4 / 25 | 21.0 / 27 | 20.7 / 26 | 22.5 / 30 |
| cpu-hpa | 50k | 21.6 / 56 | 22.1 / 62 | 91.7 / 116 | 93.9 / 113 | 92.2 / 111 | 95.0 / 112 |
| cpu-hpa | 100k | 41.3 / 113 | 41.2 / 117 | 178 / 214 | 195 / 235 | 202 / 288 | 184 / 222 |
| cpu-flow | 10k | 12.1 / 46 | – | 15.9 / 40 | – | 18.0 / 71 | – |
| cpu-flow | 50k | 10.7 / 31 | – | 14.9 / 39 | – | 14.4 / 36 | – |
| cpu-flow | 100k | 16.7 / 49 | – | 12.0 / 36 | – | 17.3 / 52 | – |
| gpu-flow (sync) | 10k | 6.2 / 16 | – | 7.2 / 18 | – | 8.0 / 33 | – |
| gpu-flow (sync) | 50k | 6.6 / 16 | – | 7.2 / 17 | – | 7.8 / 21 | – |
| gpu-flow (sync) | 100k | 7.7 / 21 | – | 7.3 / 15 | – | 9.2 / 25 | – |
| gpu-flow (deferred) | 10k | 6.2 / 16 | – | 7.2 / 18 | – | 7.8 / 33 | – |
| gpu-flow (deferred) | 50k | 6.5 / 15 | – | 7.1 / 16 | – | 7.8 / 22 | – |
| gpu-flow (deferred) | 100k | 7.5 / 20 | – | 7.2 / 15 | – | 9.1 / 25 | – |

Flow approaches apply only to shared destinations, hence the `–` cells. Spawn bursts (all units request on Tick 0):

- A*: 17–19 s for 10k.
- HPA*: 0.5 s for 10k, 5 s for 100k.
- Flow: 8–20 ms for 100k. Assignment is just a region check.

Phase breakdown at 100k units, C-s:

| | Regions | Repair | Gather | Process | Move | Cores | Memory |
|---|---|---|---|---|---|---|---|
| cpu-flow | 1.14 | 8.68 | 0.50 | 0.11 | 1.56 | 6.0 | 465 MB |
| gpu-flow-deferred | 1.54 | 4.80 | 0.47 | 0.10 | 0.28 | 0.7 | 19 MB host + 283 MB device |

Repair dominates both. CPU repair cost depends heavily on where the toggles land:

- Probe, 21 toggles, 64 Fields: mean 6.7, p50 1.3, p99 28 ms.
- Different 30-Tick runs of the same configuration varied by ±40% in mean repair cost.

### Matrix, big World (512x512x64), 168 toggles per Tick (0.001%), `results/matrix-big.txt`

Each cell is mean / p99 ms per Tick.

| Approach | Units | A | B | C-s | C-u | D-s | D-u |
|---|---|---|---|---|---|---|---|
| cpu-astar | 10k | 28 / 221 | 69 / 290 | 8,922 / 10,372 | 9,034 / 10,107 | 8,767 / 9,948 | 10,052 / 11,397 |
| cpu-hpa | 10k | 45.6 / 93 | 45.0 / 91 | 297 / 350 | 296 / 371 | 287 / 355 | 296 / 364 |
| cpu-hpa | 50k | 161 / 418 | 169 / 428 | 1,447 / 1,573 | 1,548 / 1,724 | 1,515 / 1,835 | BIGHPA50DU |
BIGROWS

- Region upkeep alone is 8.7–10.5 ms per Tick at this rate.
- A* spawn: 257–296 s for 10k. HPA* spawn: 8 s for 10k, 38–47 s for 50k.
- A* scratch memory: 4.4 GB for 16 workers (whole-World page pools).

### Toggle-rate sweep, small World, 100k units (A* at 10k), `results/sweep-small.txt`

Each cell is mean / p99 ms per Tick.

| Toggles per Tick | cpu-astar 10k C-u | cpu-hpa C-u | cpu-flow C-s | gpu-flow-deferred C-s | Regions (incremental) |
|---|---|---|---|---|---|
| 0 | 654 / 781 | 164 / 186 | **2.3 / 4.0** | **1.2 / 1.4** | 0 |
| 21 (0.001%) | 599 / 709 | 165 / 178 | 11.2 / 39 | **7.0 / 15.3** | 1.1 |
| 210 (0.01%) | 598 / 666 | 170 / 183 | 86 / 161 | 29 / 56 | 2.8 |
| 2,097 (0.1%) | 700 / 805 | 179 / 198 | 198 / 219 | 95 / 104 | 5.9 |
| 20,972 (1%) | 815 / 1,830 | 215 / 461 | 176 / 196 | 62 / 96 | 6.4 |

- At 1%, half of all dynamic tiles flip every Tick and 40k units per Tick re-path.
- The GPU is faster at 1% than at 0.1% because 20,972 toggles is above `RebuildAbove` (N/2000 = 1,048), so it does a straight rebuild. The 2,097 row is also above the threshold, but its mean (95 ms) is higher than the 1% mean (62 ms); the cause was not investigated.

BIGSWEEP

### Repair vs rebuild, 64 Fields, `results/probe-*.txt`

Each cell is mean ms per Tick. GPU cells give wall time with Kernel time in parentheses.

| | Small CPU | Small GPU | Big CPU | Big GPU |
|---|---|---|---|---|
| Full build | 130 | 87.0 (85.0) | 3,654 | 957 (952) |
| Build, 1 Field | 19.1 (1 thread) | 4.5 (3.1) | 184 (1 thread) | 17.5 (15.3) |
| Repair at 0.001% | **6.7** (p50 1.3, p99 28) | **4.4** (2.7) | 156 (p99 408) | 38.6 (32.9) |
| Repair at 0.01% | 84 (19 of 64 Fields rebuilt) | 18.0 (14.4) | 2,118 (31 of 64 rebuilt) | 230 (223) |
| Repair at 0.1% | 192 (58 of 64 rebuilt) | 87 (rebuild path) | 3,876 | 947 (rebuild path) |
| Repair at 1% | 164 | 61 (rebuild path) | 3,248 | 669 (rebuild path) |

- Incremental repair only wins at the lowest rate.
- On the CPU, repair at 0.1% (192 ms) costs **more than a full rebuild** (130 ms). Each Field spends up to its N/12 budget before falling back, and the toggles touch every Field. A smarter fallback would rebuild up front when the toggle count is high. The GPU already does this with `RebuildAbove`.

### GPU iterations, dispatches and world diameter

| World | Max distance (≈ diameter from a goal) | Iterations, 1 Field | Iterations, 64 Fields | Dispatches, 64 Fields | Submits | Block visits per block |
|---|---|---|---|---|---|---|
| small | 1,568 | 115 | 127 | 518 | 5 | 3.7 |
| big | 3,031 | 166 | 257 | 1,158 | 10 | 4.9 |

- Block-local iteration cuts the number of global steps to about **diameter / 12**.
- Plain one-hop Jacobi relaxation would need exactly *maxDistance* full sweeps of every Field. That is a bandwidth lower bound of **≥1.9 s (small) and ≥29 s (big)** per 64-Field build at the card's 448 GB/s peak. Without the block-local scheme, GPU distance Fields would lose to CPU BFS outright.
- Gauss-Seidel inside a block beats Jacobi inside a block: small 87 vs 132 ms, big 957 vs 1,186 ms.
- Submits per readback check (8, 32 or 128 iterations per submit) changed the build by under 5%. Submit count is not the bottleneck.

### GPU Kernel vs transfer vs dispatch overhead

| Item | Small | Big |
|---|---|---|
| Empty submit + fence wait | 0.070 | 0.058 |
| Upload 400 KB of unit Field ids + submit + wait | 0.159 | 0.142 |
| Move Kernel 100k units + 400 KB readback + map (sync) | 0.326 (Kernel 0.112) | 0.288 (Kernel 0.117) |
| Repair at 0.001%: wall − Kernel | 1.7 of 4.4 | 5.7 of 38.6 |
| Full build: wall − Kernel | 2.0 of 87 | 5.2 of 957 |

All values in ms. Per Tick at 100k units in the matrix:

- Upload 0.23 ms, submit 0.05, apply 0.11.
- BeginTick waits 0.29 ms for the deferred result.

The fixed cost is about **0.6–0.7 ms per Tick for 100k units**, and about **1.7 ms per repair** for its 5 readback round trips. That is small next to the repair Kernels themselves. Transfer is not the problem; repair compute is.

### Approach 6: GPU per-unit queries (measured as one Field per goal)

The cheapest GPU form of "a path for this unit" is a distance Field from its goal. Batching 32 goals per dispatch measured:

| | 1 goal (latency) | 32 goals batched | Per goal | 300 requests (10k units, 3% churn) | 3,000 requests (100k units) |
|---|---|---|---|---|---|
| small | 4.5 ms | 46.9 ms | 1.47 ms | ~440 ms | ~4.4 s |
| big | 17.5 ms | 541 ms | 16.9 ms | ~5.1 s | ~51 s |

CPU HPA* answers the same 300 requests in about 21 ms (small) or 297 ms (big). On the GPU, each extra goal costs a full-World Field: 4 MB small, 33.5 MB big. A batch of 32 holds 143 MB or 1.1 GB of device memory.

A thread-per-query GPU A* was **not implemented**. On these maps CPU A* expands 488k tiles (small) or 3.1M tiles (big) per reachable query. A GPU A* would need that much open/closed state per query: about 2 MB or 12 MB of g-values alone, or 6–37 GB for 3,000 concurrent queries. It would also run long, divergent, serial heap loops on each thread. GPU per-unit queries do not fit this problem.

### Hybrid (workload M), `results/hybrid-small.txt`

Each cell is mean / p99 ms per Tick.

| Units | cpu-astar | cpu-hpa | hyb-cpuflow+hpa | hyb-gpuflow+hpa (overlapped) |
|---|---|---|---|---|
| 10k | 602 / 713 | 19.6 / 24 | 20.7 / 40 | **13.5 / 21** |
| 50k | – | 88.5 / 104 | 65.9 / 98 | **51.6 / 78** |
| 100k | – | 171 / 206 | 108 / 146 | **100 / 120** |

- In the GPU hybrid the repair phase on the main thread drops to **0.2 ms**. The GPU repair (about 5 ms) runs while CPU HPA* plans the unique half, and the join never waited ("waited for GPU repair 0.00 ms").
- The unique half sets the total; the shared half with flow Fields is nearly free.
- Memory at 100k: GPU hybrid 376 MB host, CPU hybrid 634 MB.

Big-World hybrid: not run (the big-World flow-Field and HPA* rows already break the budget; a hybrid would repeat that).

### Thread scaling and single-thread cost, small World, `results/threads-small.txt`

| Approach | 1 thread | 4 threads | 16 threads | Speed-up |
|---|---|---|---|---|
| cpu-astar, 10k C-u | 3,728 | 1,023 | 571 | 6.5x |
| cpu-hpa, 100k C-u | 1,532 | 402 | 161 | 9.5x |
| cpu-flow, 100k C-s (mean; p50; p99) | 112; 19; 704 | 32; 6.9; 183 | 22; 5.8; 101 | 5.1x |
| gpu-flow-deferred, 100k C-s (matrix) | – | – | 7.2 | uses 0.6–2.9 cores |

Values are mean ms per Tick. The machine has 8 physical cores and 16 SMT threads, so up to about 9.5x is good scaling.

Single-threaded, CPU flow repair alone is 100 ms per Tick, and the spawn burst for 100k units under HPA* takes 50 s. Some multi-threaded designs also used less CPU time than this; for example, cpu-flow at 100k C-s used 6.0–9.6 cores over the run.

Big-World thread scaling: not run.

### Memory

| | Small | Big |
|---|---|---|
| Region IDs + chunk graph | 10 MB | 80 MB |
| 64 CPU flow Fields (u16) | 268 MB | 2,147 MB |
| 64 GPU flow Fields + lists (device) | 283 MB | 2,250 MB |
| HPA* at 100k units (paths + 16 worker scratch) | 150 MB | 257 MB (50k) |
| A* at 10k units (16 worker page pools sized to the World) | 567 MB | 4,421 MB |

On the big World, 64 Fields use 2.25 GB of the 8 GB card. Double-buffering the Fields to defer repair would need 4.5 GB.

### Deferred latency

- In every deferred run the GPU result submitted at T was ready at the start of T+1 ("readyAtT+1 30/30"). BeginTick waited 0.12–0.30 ms.
- Deferring saves only the move readback: 0.71 → 0.28 ms per Tick at 100k units. The cost is one Tick of position latency.
- Repair stays synchronous because units need valid Fields this Tick, so deferral does not hide the dominant cost.
- The hybrid's overlap (GPU repair in parallel with CPU HPA*) is the form of concurrency that paid off.

### Where each approach breaks 16 ms (16 threads)

**Small World:**

| Approach | Where it breaks 16 ms |
|---|---|
| A* | About 8 requests per Tick (1.9 ms each, parallel). Any churn breaks it. |
| HPA* | About 230 requests per Tick (about 0.07 ms each). 10k units at 3% churn is already 21 ms. |
| CPU flow | Mean is under 16 ms at 0.001% toggles for 10k–100k units; p99 is not (36–52 ms). Breaks at 0.01% toggles (86 ms). |
| GPU flow | Mean 7–9 ms at 0.001%; p99 15–25 ms. Breaks at 0.01% (29 ms). Unit count barely matters (6.2 → 7.7 ms from 10k to 100k). |

**Big World:**

| Approach | Where it breaks 16 ms |
|---|---|
| A* | Less than 1 request per Tick. |
| HPA* | About 16 requests per Tick. |
| CPU and GPU flow | Every non-zero toggle rate (repair: 156 ms CPU, 38.6 ms GPU). |
| Region upkeep | 9–10 ms at 0.001%, 27 ms at 0.01%. |

## Surprises

1. **Moving units is free; repairing Fields is everything.**
   - The GPU move Kernel for 100k units is 0.11 ms, and the CPU move is 1.6 ms.
   - Repair is 5–9 ms on small at the lowest toggle rate and grows quickly from there.
   - Any design discussion that centres on "per-unit work on the GPU" is aimed at the wrong cost.
2. **Naive GPU relaxation is hopeless; the block-local scheme is essential.**
   - One-hop relaxation needs one sweep per unit of distance: 1,568 or 3,031 sweeps, at least 1.9 s or 29 s.
   - Converging inside 16x16 blocks in shared memory cut this to about diameter/12 global steps, and Gauss-Seidel inside a block was another 20–35%.
3. **CPU incremental repair can cost more than a rebuild.** At 0.1% toggles, every Field is touched; each burns its N/12 budget and then rebuilds anyway. The result is 192 ms against a 130 ms full build.
4. **Plain A* is far worse than its reputation on loop-rich 3D maps.** It expands about 488k tiles per query on the small World and 3.1M on the big one: about 40% and 30% of the main region. A Manhattan heuristic is weak when walls force long detours and stairs are sparse.
5. **On the big World HPA* is about 14x more expensive per request than on small, not 2–3x.** The abstract graph has 217k nodes against 26k, and centroid-Manhattan costs are a weak guide. Abstract search dominates; a second, coarser level would be the obvious next step.
6. **Without region IDs, unreachable requests cost the most of anything measured.** A* without regions spends 42 ms (small) or 437 ms (big) per unreachable request. With region IDs the D workloads cost the same as the C workloads.
7. **Submits and transfers are cheap on this Backend.** Empty submit + fence takes about 0.06 ms, and a 400 KB upload about 0.15 ms. Reading convergence back every 8 or every 128 iterations made no measurable difference.
8. **Deferring the GPU result buys almost nothing** (0.4 ms). Overlapping GPU repair with CPU planning gains a lot (the whole repair).

## Shortcuts and caveats

- **World style matters.** The generator is rooms and doors plus rock blobs with sparse stairs. More open terrain would favour A* and HPA*. Many small enclosed spaces would favour region IDs. The first generator was a maze (main region 62%, max distance 3054 on small); it made heuristics look worse and was replaced.
- **Toggle model.** Toggles are random dynamic tiles spread uniformly over the World, which is the worst case for chunk-dirtying. At 0.1% and above every chunk is dirty. Clustered toggles (a door, a dig site) would repair more cheaply on both Backends.
- **Oscillating toggles.** Every Tick has toggles: even Ticks apply and odd Ticks revert. The rate in the tables is the per-Tick rate.
- **Plain A* was run at 10k units only.** The spawn burst takes 17 s on small and up to about 296 s on big. Its cost per request is linear, so 50k and 100k can be extrapolated.
- **The `unreach/t` column for cpu-astar is a cumulative counter** (reporting bug). The request streams are identical across approaches.
- **Repair is synchronous and covers all 64 Fields every Tick.** Not tried:
  - Time-slicing repair across Ticks.
  - Lazy repair of only the Fields that units are on.
  - Bounded Fields.
  - Double-buffered asynchronous repair.

  Any of these could change the absolute numbers. The CPU vs GPU ratio would probably hold.
- **GPU timings come from a single Vulkan Backend on one card.** There was no comparison against DX12 or another vendor. The RTX 2060 SUPER's 448 GB/s and 8 GB set the big-World limits.
- **HPA* is a single level.** Paths are within 2–6% of optimal on average, but not exact. Only flow Fields and A* are exact.
- **Variance.** CPU flow repair cost varied by about ±40% between runs of the same configuration, so 30 Ticks of 0.001% toggles is a small sample. Read the tables for orders of magnitude, not 10% differences.
- **Big World threads mode** not run; the benchmark process was abandoned before completing.

## How to rerun

```
cd prototypes/gpu-pathfinding
dotnet build -c Release
dotnet bin/Release/net11.0/GpuPathfinding.dll verify small      # correctness (also: big)
dotnet bin/Release/net11.0/GpuPathfinding.dll probe small       # building blocks: regions, repair vs rebuild, GPU iterations/overheads
dotnet bin/Release/net11.0/GpuPathfinding.dll matrix small 30   # approaches x workloads x 10k/50k/100k
dotnet bin/Release/net11.0/GpuPathfinding.dll hybrid small
dotnet bin/Release/net11.0/GpuPathfinding.dll sweep small       # toggle rates 0..1%
dotnet bin/Release/net11.0/GpuPathfinding.dll threads small     # 1/4/16 threads
```

- Text output goes to stdout. The `results/*.txt` files are redirected captures. CSV rows are appended to `results/{matrix,hybrid,sweep,threads}.csv`, with a world column.
- The wgpu-native v29.0.1.1 DLL is pinned under `native/` and copied to the output.
- The small suite takes about 15 minutes. The big suite takes several hours, mostly the plain A* and HPA* spawn bursts.

## Machine

- Intel Core i9-9900KF (8C/16T, 3.6–5.0 GHz), 16 hardware threads used.
- NVIDIA GeForce RTX 2060 SUPER 8 GB (driver 610.88), wgpu-native v29.0.1.1 on Vulkan.
- Windows 11 Pro 10.0.26200.
- .NET 11.0.0 (RC), Release, TieredPGO on, workstation non-concurrent GC.
