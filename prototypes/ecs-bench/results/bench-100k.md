# Colony ECS prototype benchmark
- Runtime: .NET 11.0.0-rc.2.26504.105, Microsoft Windows 10.0.26200, X64
- Logical processors: 16
- Vector256 accelerated: True, AVX2: True, AVX-512F: False
- GC: server=False, latency=Batch, dynamic code (JIT)=True
- Scenario: colonists=100000, food=5000, debris=0, warmup=500, ticks=1000, seed=12345

## Bench: colonists=100000, food=5000, debris=0
Idle control: 104 B allocated process-wide during 1 s sleep; back-to-back query delta 0 B

Waves:
Pre: [0:FoodIndex] -> playback
Update: [1:Hunger, 2:Move] -> [3:Eat] -> [4:Wander, 5:Starvation] -> playback
Late: [6:Stats, 7:FoodSpawner] -> playback

[threads=1 run=0] mean 1.022 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=2 run=0] mean 0.557 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=4 run=0] mean 0.331 ms; alloc total bytes: process 336, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=6 run=0] mean 0.259 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=0] mean 0.233 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=0] mean 0.200 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=16 run=0] mean 0.477 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=1 run=1] mean 1.019 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=2 run=1] mean 0.554 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=4 run=1] mean 0.318 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=6 run=1] mean 0.253 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=1] mean 0.223 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=1] mean 0.199 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=16 run=1] mean 0.420 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=1 run=2] mean 1.008 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=2 run=2] mean 0.560 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=4 run=2] mean 0.316 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=6 run=2] mean 0.249 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=8 run=2] mean 0.220 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=12 run=2] mean 0.195 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5
[threads=16 run=2] mean 0.596 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 105000; archetypes 2; structural ops/tick C182 D182 A0 R0 stale13.5

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Run | Alloc B/Tick (process-wide) | Alloc B/Tick (Tick threads: main+workers) | GCs | Phase wall ms: waves+playback (Pre / Update / Late) | Structural ops/Tick | Peak chunks / slabs reserved | Working set |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1.022 | 0.935 | 1.911 | 2.052 | run 0 | 0.7 | 0.0 | 0 | 0.025+0.000 / 0.891+0.008 / 0.093+0.005 | 364 | 51 / 4 MiB | 39 MiB |
| 2 | 0.557 | 0.510 | 1.023 | 1.097 | run 0 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.469+0.007 / 0.050+0.005 | 364 | 51 / 4 MiB | 40 MiB |
| 4 | 0.331 | 0.308 | 0.657 | 0.765 | run 0 | 0.3 | 0.0 | 0 | 0.026+0.000 / 0.263+0.008 / 0.028+0.005 | 364 | 51 / 4 MiB | 42 MiB |
| 6 | 0.259 | 0.240 | 0.440 | 0.489 | run 0 | 0.7 | 0.0 | 0 | 0.028+0.000 / 0.195+0.008 / 0.021+0.006 | 364 | 51 / 4 MiB | 44 MiB |
| 8 | 0.233 | 0.217 | 0.410 | 0.886 | run 0 | 0.7 | 0.0 | 0 | 0.030+0.000 / 0.170+0.008 / 0.018+0.006 | 364 | 51 / 4 MiB | 43 MiB |
| 12 | 0.200 | 0.184 | 0.340 | 0.773 | run 0 | 0.7 | 0.0 | 0 | 0.031+0.000 / 0.140+0.008 / 0.015+0.006 | 364 | 51 / 4 MiB | 45 MiB |
| 16 | 0.477 | 0.196 | 1.126 | 1.162 | run 0 | 0.7 | 0.0 | 0 | 0.032+0.000 / 0.347+0.009 / 0.083+0.007 | 364 | 51 / 4 MiB | 46 MiB |
| 1 | 1.019 | 0.930 | 1.930 | 2.070 | run 1 | 0.7 | 0.0 | 0 | 0.024+0.000 / 0.890+0.007 / 0.093+0.005 | 364 | 51 / 4 MiB | 43 MiB |
| 2 | 0.554 | 0.510 | 1.017 | 1.070 | run 1 | 0.7 | 0.0 | 0 | 0.025+0.000 / 0.467+0.007 / 0.050+0.005 | 364 | 51 / 4 MiB | 45 MiB |
| 4 | 0.318 | 0.291 | 0.566 | 0.618 | run 1 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.252+0.007 / 0.028+0.005 | 364 | 51 / 4 MiB | 50 MiB |
| 6 | 0.253 | 0.238 | 0.451 | 0.485 | run 1 | 0.7 | 0.0 | 0 | 0.028+0.000 / 0.192+0.008 / 0.021+0.005 | 364 | 51 / 4 MiB | 48 MiB |
| 8 | 0.223 | 0.212 | 0.362 | 0.388 | run 1 | 0.7 | 0.0 | 0 | 0.029+0.000 / 0.164+0.008 / 0.017+0.006 | 364 | 51 / 4 MiB | 46 MiB |
| 12 | 0.199 | 0.187 | 0.318 | 0.537 | run 1 | 0.7 | 0.0 | 0 | 0.030+0.000 / 0.142+0.008 / 0.014+0.006 | 364 | 51 / 4 MiB | 48 MiB |
| 16 | 0.420 | 0.179 | 1.073 | 1.131 | run 1 | 0.7 | 0.0 | 0 | 0.031+0.000 / 0.303+0.009 / 0.070+0.006 | 364 | 51 / 4 MiB | 46 MiB |
| 1 | 1.008 | 0.927 | 1.912 | 2.147 | run 2 | 0.7 | 0.0 | 0 | 0.024+0.000 / 0.880+0.007 / 0.092+0.005 | 364 | 51 / 4 MiB | 47 MiB |
| 2 | 0.560 | 0.514 | 1.034 | 1.074 | run 2 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.471+0.007 / 0.051+0.005 | 364 | 51 / 4 MiB | 46 MiB |
| 4 | 0.316 | 0.289 | 0.571 | 0.618 | run 2 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.249+0.007 / 0.028+0.005 | 364 | 51 / 4 MiB | 39 MiB |
| 6 | 0.249 | 0.237 | 0.448 | 0.508 | run 2 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.189+0.007 / 0.021+0.005 | 364 | 51 / 4 MiB | 38 MiB |
| 8 | 0.220 | 0.206 | 0.356 | 0.381 | run 2 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.163+0.007 / 0.017+0.005 | 364 | 51 / 4 MiB | 39 MiB |
| 12 | 0.195 | 0.186 | 0.340 | 0.425 | run 2 | 0.7 | 0.0 | 0 | 0.030+0.000 / 0.138+0.008 / 0.014+0.006 | 364 | 51 / 4 MiB | 38 MiB |
| 16 | 0.596 | 0.439 | 1.128 | 2.984 | run 2 | 0.7 | 0.0 | 0 | 0.031+0.000 / 0.434+0.009 / 0.116+0.007 | 364 | 51 / 4 MiB | 40 MiB |

Summary: median over 3 run(s) per thread count

| Threads | Mean ms | p50 ms | p99 ms | Max ms (worst run) | Speedup (mean) | Speedup (p50) | Fits 16 ms (p99)? |
|---|---|---|---|---|---|---|---|
| 1 | 1.019 | 0.930 | 1.912 | 2.147 | 1.00x | 1.00x | yes |
| 2 | 0.557 | 0.510 | 1.023 | 1.097 | 1.83x | 1.82x | yes |
| 4 | 0.318 | 0.291 | 0.571 | 0.765 | 3.20x | 3.19x | yes |
| 6 | 0.253 | 0.238 | 0.448 | 0.508 | 4.03x | 3.90x | yes |
| 8 | 0.223 | 0.212 | 0.362 | 0.886 | 4.57x | 4.40x | yes |
| 12 | 0.199 | 0.186 | 0.340 | 0.773 | 5.11x | 5.00x | yes |
| 16 | 0.477 | 0.196 | 1.126 | 2.984 | 2.14x | 4.74x | yes |

Per-System CPU ms per Tick, summed over threads (jobs per Tick):

| Threads | FoodIndex | Hunger | Move | Eat | Wander | Starvation | Stats | FoodSpawner |
|---|---|---|---|---|---|---|---|---|
| 1 (run 0) | 0.024 (1) | 0.010 (49) | 0.035 (49) | 0.596 (49) | 0.225 (49) | 0.017 (49) | 0.088 (49) | 0.004 (1) |
| 2 (run 0) | 0.025 (1) | 0.018 (49) | 0.042 (49) | 0.606 (49) | 0.227 (49) | 0.020 (49) | 0.088 (49) | 0.004 (1) |
| 4 (run 0) | 0.026 (1) | 0.027 (49) | 0.058 (49) | 0.646 (49) | 0.243 (49) | 0.025 (49) | 0.090 (49) | 0.004 (1) |
| 6 (run 0) | 0.028 (1) | 0.046 (49) | 0.085 (49) | 0.671 (49) | 0.251 (49) | 0.031 (49) | 0.091 (49) | 0.004 (1) |
| 8 (run 0) | 0.030 (1) | 0.075 (49) | 0.132 (49) | 0.711 (49) | 0.261 (49) | 0.042 (49) | 0.093 (49) | 0.005 (1) |
| 12 (run 0) | 0.031 (1) | 0.114 (49) | 0.199 (49) | 0.796 (49) | 0.284 (49) | 0.060 (49) | 0.095 (49) | 0.005 (1) |
| 16 (run 0) | 0.031 (1) | 0.151 (49) | 0.299 (49) | 0.841 (49) | 0.304 (49) | 0.089 (49) | 0.097 (49) | 0.005 (1) |
| 1 (run 1) | 0.024 (1) | 0.009 (49) | 0.034 (49) | 0.599 (49) | 0.224 (49) | 0.016 (49) | 0.087 (49) | 0.004 (1) |
| 2 (run 1) | 0.025 (1) | 0.018 (49) | 0.042 (49) | 0.604 (49) | 0.226 (49) | 0.020 (49) | 0.088 (49) | 0.004 (1) |
| 4 (run 1) | 0.026 (1) | 0.025 (49) | 0.054 (49) | 0.622 (49) | 0.232 (49) | 0.024 (49) | 0.089 (49) | 0.004 (1) |
| 6 (run 1) | 0.027 (1) | 0.047 (49) | 0.088 (49) | 0.658 (49) | 0.244 (49) | 0.032 (49) | 0.090 (49) | 0.004 (1) |
| 8 (run 1) | 0.029 (1) | 0.075 (49) | 0.135 (49) | 0.688 (49) | 0.250 (49) | 0.042 (49) | 0.091 (49) | 0.004 (1) |
| 12 (run 1) | 0.030 (1) | 0.126 (49) | 0.228 (49) | 0.780 (49) | 0.279 (49) | 0.062 (49) | 0.094 (49) | 0.005 (1) |
| 16 (run 1) | 0.031 (1) | 0.135 (49) | 0.291 (49) | 0.842 (49) | 0.300 (49) | 0.080 (49) | 0.097 (49) | 0.006 (1) |
| 1 (run 2) | 0.023 (1) | 0.009 (49) | 0.032 (49) | 0.593 (49) | 0.223 (49) | 0.016 (49) | 0.087 (49) | 0.004 (1) |
| 2 (run 2) | 0.026 (1) | 0.018 (49) | 0.042 (49) | 0.608 (49) | 0.228 (49) | 0.020 (49) | 0.089 (49) | 0.004 (1) |
| 4 (run 2) | 0.026 (1) | 0.025 (49) | 0.054 (49) | 0.615 (49) | 0.229 (49) | 0.024 (49) | 0.089 (49) | 0.004 (1) |
| 6 (run 2) | 0.027 (1) | 0.046 (49) | 0.090 (49) | 0.646 (49) | 0.237 (49) | 0.032 (49) | 0.090 (49) | 0.004 (1) |
| 8 (run 2) | 0.027 (1) | 0.072 (49) | 0.128 (49) | 0.697 (49) | 0.252 (49) | 0.042 (49) | 0.092 (49) | 0.004 (1) |
| 12 (run 2) | 0.030 (1) | 0.104 (49) | 0.198 (49) | 0.782 (49) | 0.280 (49) | 0.056 (49) | 0.094 (49) | 0.005 (1) |
| 16 (run 2) | 0.031 (1) | 0.111 (49) | 0.273 (49) | 0.841 (49) | 0.305 (49) | 0.076 (49) | 0.099 (49) | 0.007 (1) |

Process peak working set: 50.6 MiB

