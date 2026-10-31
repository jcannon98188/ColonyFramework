# Colony ECS prototype benchmark
- Runtime: .NET 11.0.0-rc.2.26504.105, Microsoft Windows 10.0.26200, X64
- Logical processors: 16
- Vector256 accelerated: True, AVX2: True, AVX-512F: False
- GC: server=False, latency=Batch, dynamic code (JIT)=False
- Scenario: colonists=100000, food=5000, debris=0, warmup=500, ticks=1000, seed=12345

## Bench: colonists=100000, food=5000, debris=0
Idle control: 0 B allocated process-wide during 1 s sleep; back-to-back query delta 0 B

Waves:
Pre: [0:FoodIndex] -> playback
Update: [1:Hunger, 2:Move] -> [3:Eat] -> [4:Wander, 5:Starvation] -> playback
Late: [6:Stats, 7:FoodSpawner] -> playback

[threads=1 run=0] mean 1.087 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=0] mean 0.236 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=0] mean 0.212 ms; alloc total bytes: process 336, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=1 run=1] mean 1.080 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=1] mean 0.242 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=1] mean 0.213 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=1 run=2] mean 1.076 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=2] mean 0.230 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=2] mean 0.215 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Run | Alloc B/Tick (process-wide) | Alloc B/Tick (Tick threads: main+workers) | GCs | Phase wall ms: waves+playback (Pre / Update / Late) | Structural ops/Tick | Peak chunks / slabs reserved | Working set |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1.087 | 0.988 | 2.052 | 2.412 | run 0 | 0.7 | 0.0 | 0 | 0.023+0.000 / 0.941+0.008 / 0.108+0.006 | 364 | 51 / 4 MiB | 17 MiB |
| 8 | 0.236 | 0.225 | 0.408 | 0.440 | run 0 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.174+0.008 / 0.021+0.007 | 364 | 51 / 4 MiB | 19 MiB |
| 12 | 0.212 | 0.199 | 0.345 | 0.473 | run 0 | 0.3 | 0.0 | 0 | 0.029+0.000 / 0.150+0.008 / 0.017+0.008 | 364 | 51 / 4 MiB | 19 MiB |
| 1 | 1.080 | 0.995 | 2.047 | 2.214 | run 1 | 0.7 | 0.0 | 0 | 0.023+0.000 / 0.936+0.007 / 0.108+0.007 | 364 | 51 / 4 MiB | 21 MiB |
| 8 | 0.242 | 0.233 | 0.390 | 0.486 | run 1 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.181+0.007 / 0.020+0.007 | 364 | 51 / 4 MiB | 19 MiB |
| 12 | 0.213 | 0.200 | 0.350 | 0.405 | run 1 | 0.7 | 0.0 | 0 | 0.029+0.000 / 0.151+0.008 / 0.017+0.008 | 364 | 51 / 4 MiB | 21 MiB |
| 1 | 1.076 | 0.987 | 2.029 | 2.182 | run 2 | 0.7 | 0.0 | 0 | 0.023+0.000 / 0.934+0.007 / 0.107+0.006 | 364 | 51 / 4 MiB | 19 MiB |
| 8 | 0.230 | 0.212 | 0.378 | 0.418 | run 2 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.169+0.007 / 0.020+0.007 | 364 | 51 / 4 MiB | 21 MiB |
| 12 | 0.215 | 0.199 | 0.370 | 0.613 | run 2 | 0.7 | 0.0 | 0 | 0.029+0.000 / 0.152+0.008 / 0.017+0.008 | 364 | 51 / 4 MiB | 19 MiB |

Summary: median over 3 run(s) per thread count

| Threads | Mean ms | p50 ms | p99 ms | Max ms (worst run) | Speedup (mean) | Speedup (p50) | Fits 16 ms (p99)? |
|---|---|---|---|---|---|---|---|
| 1 | 1.080 | 0.988 | 2.047 | 2.412 | 1.00x | 1.00x | yes |
| 8 | 0.236 | 0.225 | 0.390 | 0.486 | 4.57x | 4.39x | yes |
| 12 | 0.213 | 0.199 | 0.350 | 0.613 | 5.07x | 4.95x | yes |

Per-System CPU ms per Tick, summed over threads (jobs per Tick):

| Threads | FoodIndex | Hunger | Move | Eat | Wander | Starvation | Stats | FoodSpawner |
|---|---|---|---|---|---|---|---|---|
| 1 (run 0) | 0.023 (1) | 0.010 (49) | 0.035 (49) | 0.585 (49) | 0.284 (49) | 0.016 (49) | 0.100 (49) | 0.005 (1) |
| 8 (run 0) | 0.026 (1) | 0.067 (49) | 0.122 (49) | 0.697 (49) | 0.344 (49) | 0.039 (49) | 0.111 (49) | 0.006 (1) |
| 12 (run 0) | 0.028 (1) | 0.135 (49) | 0.226 (49) | 0.776 (49) | 0.395 (49) | 0.055 (49) | 0.124 (49) | 0.006 (1) |
| 1 (run 1) | 0.023 (1) | 0.010 (49) | 0.034 (49) | 0.581 (49) | 0.283 (49) | 0.016 (49) | 0.099 (49) | 0.005 (1) |
| 8 (run 1) | 0.026 (1) | 0.094 (49) | 0.159 (49) | 0.685 (49) | 0.336 (49) | 0.047 (49) | 0.109 (49) | 0.006 (1) |
| 12 (run 1) | 0.029 (1) | 0.151 (49) | 0.247 (49) | 0.767 (49) | 0.388 (49) | 0.057 (49) | 0.123 (49) | 0.007 (1) |
| 1 (run 2) | 0.023 (1) | 0.010 (49) | 0.034 (49) | 0.580 (49) | 0.282 (49) | 0.016 (49) | 0.098 (49) | 0.005 (1) |
| 8 (run 2) | 0.026 (1) | 0.067 (49) | 0.121 (49) | 0.670 (49) | 0.325 (49) | 0.037 (49) | 0.106 (49) | 0.006 (1) |
| 12 (run 2) | 0.029 (1) | 0.133 (49) | 0.227 (49) | 0.778 (49) | 0.396 (49) | 0.054 (49) | 0.124 (49) | 0.006 (1) |

Process peak working set: 21.3 MiB
