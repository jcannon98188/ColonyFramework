# Colony ECS prototype benchmark
- Runtime: .NET 11.0.0-rc.2.26504.105, Microsoft Windows 10.0.26200, X64
- Logical processors: 16
- Vector256 accelerated: True, AVX2: True, AVX-512F: False
- GC: server=False, latency=Batch, dynamic code (JIT)=True
- Scenario: colonists=100000, food=5000, debris=100000, warmup=500, ticks=1000, seed=12345

## Bench: colonists=100000, food=5000, debris=100000
Idle control: 104 B allocated process-wide during 1 s sleep; back-to-back query delta 0 B

Waves:
Pre: [0:FoodIndex] -> playback
Update: [1:Hunger, 2:Move] -> [3:Eat] -> [4:Wander, 5:Starvation] -> playback
Late: [6:Stats, 7:FoodSpawner, 8:Churn] -> playback

[threads=1 run=0] mean 2.916 ms; alloc total bytes: process 336, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=2 run=0] mean 2.567 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=4 run=0] mean 1.789 ms; alloc total bytes: process 336, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=8 run=0] mean 1.950 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=12 run=0] mean 1.950 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=16 run=0] mean 2.435 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=1 run=1] mean 2.862 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=2 run=1] mean 2.258 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=4 run=1] mean 2.026 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=8 run=1] mean 1.839 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=12 run=1] mean 1.870 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=16 run=1] mean 2.494 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=1 run=2] mean 2.896 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=2 run=2] mean 2.348 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=4 run=2] mean 2.069 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=8 run=2] mean 2.000 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=12 run=2] mean 1.896 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5
[threads=16 run=2] mean 2.584 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 28.3; colonists 100000; below-full-health 0; eaten/tick 195.5; food 5000; alive 205000; archetypes 4; structural ops/tick C10182 D10182 A10000 R10000 stale13.5

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Run | Alloc B/Tick (process-wide) | Alloc B/Tick (Tick threads: main+workers) | GCs | Phase wall ms: waves+playback (Pre / Update / Late) | Structural ops/Tick | Peak chunks / slabs reserved | Working set |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 2.916 | 2.794 | 4.824 | 6.068 | run 0 | 0.3 | 0.0 | 0 | 0.027+0.000 / 0.913+0.010 / 0.654+1.311 | 40364 | 84 / 8 MiB | 49 MiB |
| 2 | 2.567 | 2.532 | 3.362 | 3.845 | run 0 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.497+0.011 / 0.726+1.307 | 40364 | 84 / 8 MiB | 56 MiB |
| 4 | 1.789 | 1.746 | 2.624 | 2.768 | run 0 | 0.3 | 0.0 | 0 | 0.026+0.000 / 0.285+0.009 / 0.167+1.302 | 40364 | 84 / 8 MiB | 55 MiB |
| 8 | 1.950 | 1.928 | 2.799 | 3.163 | run 0 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.201+0.010 / 0.403+1.310 | 40364 | 84 / 8 MiB | 61 MiB |
| 12 | 1.950 | 1.899 | 2.816 | 3.086 | run 0 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.176+0.011 / 0.387+1.348 | 40364 | 84 / 8 MiB | 57 MiB |
| 16 | 2.435 | 2.456 | 3.469 | 3.807 | run 0 | 0.7 | 0.0 | 0 | 0.028+0.000 / 0.649+0.012 / 0.346+1.399 | 40364 | 84 / 8 MiB | 63 MiB |
| 1 | 2.862 | 2.773 | 3.821 | 4.096 | run 1 | 0.7 | 0.0 | 0 | 0.025+0.000 / 0.902+0.010 / 0.649+1.277 | 40364 | 84 / 8 MiB | 60 MiB |
| 2 | 2.258 | 2.188 | 3.323 | 3.779 | run 1 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.499+0.010 / 0.424+1.299 | 40364 | 84 / 8 MiB | 58 MiB |
| 4 | 2.026 | 1.984 | 2.983 | 3.138 | run 1 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.280+0.010 / 0.409+1.302 | 40364 | 84 / 8 MiB | 62 MiB |
| 8 | 1.839 | 1.818 | 2.400 | 2.799 | run 1 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.201+0.010 / 0.286+1.316 | 40364 | 84 / 8 MiB | 68 MiB |
| 12 | 1.870 | 1.820 | 2.891 | 3.408 | run 1 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.179+0.011 / 0.300+1.353 | 40364 | 84 / 8 MiB | 63 MiB |
| 16 | 2.494 | 2.490 | 3.480 | 3.671 | run 1 | 0.7 | 0.0 | 0 | 0.028+0.000 / 0.672+0.013 / 0.390+1.391 | 40364 | 84 / 8 MiB | 53 MiB |
| 1 | 2.896 | 2.785 | 4.198 | 4.889 | run 2 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.908+0.010 / 0.653+1.298 | 40364 | 84 / 8 MiB | 54 MiB |
| 2 | 2.348 | 2.297 | 3.381 | 3.599 | run 2 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.498+0.009 / 0.499+1.315 | 40364 | 84 / 8 MiB | 48 MiB |
| 4 | 2.069 | 2.046 | 2.454 | 3.036 | run 2 | 0.7 | 0.0 | 0 | 0.026+0.000 / 0.286+0.010 / 0.457+1.289 | 40364 | 84 / 8 MiB | 51 MiB |
| 8 | 2.000 | 1.954 | 2.866 | 3.066 | run 2 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.202+0.010 / 0.421+1.341 | 40364 | 84 / 8 MiB | 48 MiB |
| 12 | 1.896 | 1.851 | 2.745 | 2.966 | run 2 | 0.7 | 0.0 | 0 | 0.027+0.000 / 0.176+0.012 / 0.337+1.344 | 40364 | 84 / 8 MiB | 52 MiB |
| 16 | 2.584 | 2.539 | 3.577 | 3.712 | run 2 | 0.7 | 0.0 | 0 | 0.028+0.000 / 0.697+0.012 / 0.420+1.427 | 40364 | 84 / 8 MiB | 48 MiB |

Summary: median over 3 run(s) per thread count

| Threads | Mean ms | p50 ms | p99 ms | Max ms (worst run) | Speedup (mean) | Speedup (p50) | Fits 16 ms (p99)? |
|---|---|---|---|---|---|---|---|
| 1 | 2.896 | 2.785 | 4.198 | 6.068 | 1.00x | 1.00x | yes |
| 2 | 2.348 | 2.297 | 3.362 | 3.845 | 1.23x | 1.21x | yes |
| 4 | 2.026 | 1.984 | 2.624 | 3.138 | 1.43x | 1.40x | yes |
| 8 | 1.950 | 1.928 | 2.799 | 3.163 | 1.48x | 1.44x | yes |
| 12 | 1.896 | 1.851 | 2.816 | 3.408 | 1.53x | 1.50x | yes |
| 16 | 2.494 | 2.490 | 3.480 | 3.807 | 1.16x | 1.12x | yes |

Per-System CPU ms per Tick, summed over threads (jobs per Tick):

| Threads | FoodIndex | Hunger | Move | Eat | Wander | Starvation | Stats | FoodSpawner | Churn |
|---|---|---|---|---|---|---|---|---|---|
| 1 (run 0) | 0.027 (1) | 0.013 (49) | 0.046 (49) | 0.610 (49) | 0.217 (49) | 0.020 (49) | 0.088 (49) | 0.004 (1) | 0.558 (32) |
| 2 (run 0) | 0.026 (1) | 0.019 (49) | 0.054 (49) | 0.619 (49) | 0.217 (49) | 0.024 (49) | 0.089 (49) | 0.004 (1) | 1.349 (32) |
| 4 (run 0) | 0.025 (1) | 0.019 (49) | 0.055 (49) | 0.619 (49) | 0.218 (49) | 0.028 (49) | 0.089 (49) | 0.004 (1) | 0.556 (32) |
| 8 (run 0) | 0.026 (1) | 0.024 (49) | 0.072 (49) | 0.688 (49) | 0.236 (49) | 0.035 (49) | 0.092 (49) | 0.006 (1) | 2.684 (32) |
| 12 (run 0) | 0.026 (1) | 0.026 (49) | 0.103 (49) | 0.815 (49) | 0.277 (49) | 0.046 (49) | 0.098 (49) | 0.007 (1) | 3.737 (32) |
| 16 (run 0) | 0.028 (1) | 0.029 (49) | 0.145 (49) | 0.876 (49) | 0.297 (49) | 0.061 (49) | 0.102 (49) | 0.007 (1) | 2.599 (32) |
| 1 (run 1) | 0.025 (1) | 0.013 (49) | 0.043 (49) | 0.605 (49) | 0.215 (49) | 0.020 (49) | 0.088 (49) | 0.004 (1) | 0.553 (32) |
| 2 (run 1) | 0.026 (1) | 0.020 (49) | 0.055 (49) | 0.621 (49) | 0.218 (49) | 0.025 (49) | 0.090 (49) | 0.005 (1) | 0.743 (32) |
| 4 (run 1) | 0.026 (1) | 0.023 (49) | 0.069 (49) | 0.625 (49) | 0.218 (49) | 0.028 (49) | 0.089 (49) | 0.004 (1) | 1.460 (32) |
| 8 (run 1) | 0.025 (1) | 0.024 (49) | 0.077 (49) | 0.690 (49) | 0.236 (49) | 0.034 (49) | 0.091 (49) | 0.005 (1) | 1.950 (32) |
| 12 (run 1) | 0.026 (1) | 0.028 (49) | 0.116 (49) | 0.816 (49) | 0.276 (49) | 0.048 (49) | 0.095 (49) | 0.006 (1) | 2.714 (32) |
| 16 (run 1) | 0.028 (1) | 0.035 (49) | 0.174 (49) | 0.873 (49) | 0.295 (49) | 0.062 (49) | 0.104 (49) | 0.009 (1) | 3.207 (32) |
| 1 (run 2) | 0.025 (1) | 0.014 (49) | 0.043 (49) | 0.608 (49) | 0.216 (49) | 0.020 (49) | 0.088 (49) | 0.004 (1) | 0.557 (32) |
| 2 (run 2) | 0.026 (1) | 0.018 (49) | 0.054 (49) | 0.618 (49) | 0.218 (49) | 0.024 (49) | 0.089 (49) | 0.004 (1) | 0.896 (32) |
| 4 (run 2) | 0.025 (1) | 0.020 (49) | 0.067 (49) | 0.628 (49) | 0.219 (49) | 0.027 (49) | 0.089 (49) | 0.004 (1) | 1.619 (32) |
| 8 (run 2) | 0.026 (1) | 0.026 (49) | 0.081 (49) | 0.691 (49) | 0.237 (49) | 0.037 (49) | 0.092 (49) | 0.005 (1) | 2.931 (32) |
| 12 (run 2) | 0.026 (1) | 0.027 (49) | 0.100 (49) | 0.814 (49) | 0.276 (49) | 0.049 (49) | 0.095 (49) | 0.006 (1) | 3.036 (32) |
| 16 (run 2) | 0.028 (1) | 0.031 (49) | 0.145 (49) | 0.878 (49) | 0.296 (49) | 0.063 (49) | 0.101 (49) | 0.010 (1) | 3.598 (32) |

Process peak working set: 69.0 MiB

