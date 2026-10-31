# Colony ECS prototype benchmark
- Runtime: .NET 11.0.0-rc.2.26504.105, Microsoft Windows 10.0.26200, X64
- Logical processors: 16
- Vector256 accelerated: False, AVX2: False, AVX-512F: False
- GC: server=False, latency=Batch, dynamic code (JIT)=False
- Scenario: colonists=100000, food=5000, debris=0, warmup=500, ticks=1000, seed=12345

## Bench: colonists=100000, food=5000, debris=0
Idle control: 0 B allocated process-wide during 1 s sleep; back-to-back query delta 0 B

Waves:
Pre: [0:FoodIndex] -> playback
Update: [1:Hunger, 2:Move] -> [3:Eat] -> [4:Wander, 5:Starvation] -> playback
Late: [6:Stats, 7:FoodSpawner] -> playback

[threads=1 run=0] mean 2.053 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=0] mean 0.369 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=0] mean 0.303 ms; alloc total bytes: process 336, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=1 run=1] mean 2.068 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=1] mean 0.373 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=1] mean 0.302 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=1 run=2] mean 2.075 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=2] mean 0.380 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=2] mean 0.307 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Run | Alloc B/Tick (process-wide) | Alloc B/Tick (Tick threads: main+workers) | GCs | Phase wall ms: waves+playback (Pre / Update / Late) | Structural ops/Tick | Peak chunks / slabs reserved | Working set |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 2.053 | 1.973 | 2.995 | 3.214 | run 0 | 0.7 | 0.0 | 0 | 0.023+0.000 / 1.915+0.008 / 0.100+0.007 | 364 | 51 / 4 MiB | 16 MiB |
| 8 | 0.369 | 0.357 | 0.519 | 0.579 | run 0 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.308+0.007 / 0.020+0.007 | 364 | 51 / 4 MiB | 18 MiB |
| 12 | 0.303 | 0.290 | 0.424 | 0.471 | run 0 | 0.3 | 0.0 | 0 | 0.029+0.000 / 0.240+0.008 / 0.017+0.009 | 364 | 51 / 4 MiB | 17 MiB |
| 1 | 2.068 | 1.969 | 3.031 | 3.186 | run 1 | 0.7 | 0.0 | 0 | 0.023+0.000 / 1.929+0.008 / 0.101+0.007 | 364 | 51 / 4 MiB | 19 MiB |
| 8 | 0.373 | 0.357 | 0.523 | 0.565 | run 1 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.310+0.008 / 0.020+0.008 | 364 | 51 / 4 MiB | 18 MiB |
| 12 | 0.302 | 0.289 | 0.436 | 0.462 | run 1 | 0.7 | 0.0 | 0 | 0.028+0.000 / 0.240+0.008 / 0.017+0.008 | 364 | 51 / 4 MiB | 20 MiB |
| 1 | 2.075 | 1.967 | 3.269 | 3.642 | run 2 | 0.7 | 0.0 | 0 | 0.024+0.000 / 1.936+0.008 / 0.101+0.007 | 364 | 51 / 4 MiB | 18 MiB |
| 8 | 0.380 | 0.370 | 0.530 | 0.596 | run 2 | 0.7 | 0.0 | 0 | 0.028+0.000 / 0.316+0.008 / 0.021+0.008 | 364 | 51 / 4 MiB | 19 MiB |
| 12 | 0.307 | 0.294 | 0.457 | 0.636 | run 2 | 0.7 | 0.0 | 0 | 0.029+0.000 / 0.243+0.009 / 0.018+0.009 | 364 | 51 / 4 MiB | 18 MiB |

Summary: median over 3 run(s) per thread count

| Threads | Mean ms | p50 ms | p99 ms | Max ms (worst run) | Speedup (mean) | Speedup (p50) | Fits 16 ms (p99)? |
|---|---|---|---|---|---|---|---|
| 1 | 2.068 | 1.969 | 3.031 | 3.642 | 1.00x | 1.00x | yes |
| 8 | 0.373 | 0.357 | 0.523 | 0.596 | 5.55x | 5.51x | yes |
| 12 | 0.303 | 0.290 | 0.436 | 0.636 | 6.83x | 6.80x | yes |

Per-System CPU ms per Tick, summed over threads (jobs per Tick):

| Threads | FoodIndex | Hunger | Move | Eat | Wander | Starvation | Stats | FoodSpawner |
|---|---|---|---|---|---|---|---|---|
| 1 (run 0) | 0.023 (1) | 0.013 (49) | 0.956 (49) | 0.604 (49) | 0.290 (49) | 0.045 (49) | 0.094 (49) | 0.005 (1) |
| 8 (run 0) | 0.026 (1) | 0.072 (49) | 1.088 (49) | 0.713 (49) | 0.339 (49) | 0.058 (49) | 0.108 (49) | 0.006 (1) |
| 12 (run 0) | 0.028 (1) | 0.104 (49) | 1.212 (49) | 0.837 (49) | 0.399 (49) | 0.073 (49) | 0.124 (49) | 0.006 (1) |
| 1 (run 1) | 0.023 (1) | 0.013 (49) | 0.962 (49) | 0.609 (49) | 0.292 (49) | 0.046 (49) | 0.095 (49) | 0.005 (1) |
| 8 (run 1) | 0.027 (1) | 0.066 (49) | 1.095 (49) | 0.726 (49) | 0.341 (49) | 0.059 (49) | 0.109 (49) | 0.006 (1) |
| 12 (run 1) | 0.028 (1) | 0.108 (49) | 1.207 (49) | 0.829 (49) | 0.397 (49) | 0.073 (49) | 0.124 (49) | 0.007 (1) |
| 1 (run 2) | 0.024 (1) | 0.013 (49) | 0.964 (49) | 0.613 (49) | 0.292 (49) | 0.046 (49) | 0.095 (49) | 0.005 (1) |
| 8 (run 2) | 0.028 (1) | 0.065 (49) | 1.105 (49) | 0.736 (49) | 0.349 (49) | 0.062 (49) | 0.111 (49) | 0.006 (1) |
| 12 (run 2) | 0.029 (1) | 0.102 (49) | 1.215 (49) | 0.840 (49) | 0.404 (49) | 0.075 (49) | 0.126 (49) | 0.006 (1) |

Process peak working set: 20.1 MiB
