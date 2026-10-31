# Colony ECS prototype benchmark
- Runtime: .NET 11.0.0-rc.2.26504.105, Microsoft Windows 10.0.26200, X64
- Logical processors: 16
- Vector256 accelerated: True, AVX2: True, AVX-512F: False
- GC: server=False, latency=Batch, dynamic code (JIT)=True
- Scenario: colonists=1000000, food=50000, debris=0, warmup=500, ticks=1000, seed=12345

## Bench: colonists=1000000, food=50000, debris=0
Idle control: 104 B allocated process-wide during 1 s sleep; back-to-back query delta 0 B

Waves:
Pre: [0:FoodIndex] -> playback
Update: [1:Hunger, 2:Move] -> [3:Eat] -> [4:Wander, 5:Starvation] -> playback
Late: [6:Stats, 7:FoodSpawner] -> playback

[threads=1 run=0] mean 7.923 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=2 run=0] mean 4.674 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=4 run=0] mean 3.419 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=8 run=0] mean 2.978 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=12 run=0] mean 3.080 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=16 run=0] mean 3.638 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=1 run=1] mean 7.824 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=2 run=1] mean 4.649 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=4 run=1] mean 3.122 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=8 run=1] mean 2.816 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=12 run=1] mean 2.797 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=16 run=1] mean 3.519 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=1 run=2] mean 7.779 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=2 run=2] mean 4.659 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=4 run=2] mean 3.150 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=8 run=2] mean 2.768 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=12 run=2] mean 2.770 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6
[threads=16 run=2] mean 3.434 ms; alloc total bytes: process 672, main 0, workers 0; avg hunger 29.0; colonists 1000000; below-full-health 0; eaten/tick 1990.3; food 50000; alive 1050000; archetypes 2; structural ops/tick C1775 D1775 A0 R0 stale215.6

| Threads | Mean ms | p50 ms | p99 ms | Max ms | Run | Alloc B/Tick (process-wide) | Alloc B/Tick (Tick threads: main+workers) | GCs | Phase wall ms: waves+playback (Pre / Update / Late) | Structural ops/Tick | Peak chunks / slabs reserved | Working set |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 7.923 | 7.632 | 14.873 | 35.112 | run 0 | 0.7 | 0.0 | 0 | 0.296+0.000 / 6.341+0.197 / 0.999+0.090 | 3549 | 505 / 32 MiB | 90 MiB |
| 2 | 4.674 | 4.532 | 8.213 | 19.174 | run 0 | 0.7 | 0.0 | 0 | 0.290+0.000 / 3.593+0.180 / 0.529+0.082 | 3549 | 505 / 32 MiB | 127 MiB |
| 4 | 3.419 | 3.195 | 7.827 | 11.325 | run 0 | 0.7 | 0.0 | 0 | 0.305+0.000 / 2.492+0.204 / 0.328+0.091 | 3549 | 505 / 32 MiB | 162 MiB |
| 8 | 2.978 | 2.872 | 4.765 | 8.239 | run 0 | 0.7 | 0.0 | 0 | 0.319+0.000 / 2.071+0.217 / 0.271+0.100 | 3549 | 505 / 32 MiB | 187 MiB |
| 12 | 3.080 | 3.046 | 4.609 | 8.465 | run 0 | 0.7 | 0.0 | 0 | 0.329+0.000 / 2.121+0.241 / 0.279+0.108 | 3549 | 505 / 32 MiB | 202 MiB |
| 16 | 3.638 | 3.494 | 5.625 | 8.810 | run 0 | 0.7 | 0.0 | 0 | 0.318+0.000 / 2.573+0.226 / 0.419+0.101 | 3549 | 505 / 32 MiB | 219 MiB |
| 1 | 7.824 | 7.598 | 14.129 | 35.177 | run 1 | 0.7 | 0.0 | 0 | 0.291+0.000 / 6.256+0.194 / 0.995+0.087 | 3549 | 505 / 32 MiB | 219 MiB |
| 2 | 4.649 | 4.506 | 8.036 | 19.220 | run 1 | 0.7 | 0.0 | 0 | 0.289+0.000 / 3.560+0.184 / 0.530+0.086 | 3549 | 505 / 32 MiB | 220 MiB |
| 4 | 3.122 | 3.004 | 5.264 | 11.476 | run 1 | 0.7 | 0.0 | 0 | 0.287+0.000 / 2.282+0.181 / 0.292+0.081 | 3549 | 505 / 32 MiB | 216 MiB |
| 8 | 2.816 | 2.686 | 4.812 | 9.298 | run 1 | 0.7 | 0.0 | 0 | 0.320+0.000 / 1.946+0.213 / 0.237+0.100 | 3549 | 505 / 32 MiB | 227 MiB |
| 12 | 2.797 | 2.710 | 4.179 | 8.129 | run 1 | 0.7 | 0.0 | 0 | 0.320+0.000 / 1.911+0.222 / 0.239+0.104 | 3549 | 505 / 32 MiB | 209 MiB |
| 16 | 3.519 | 3.473 | 4.600 | 7.928 | run 1 | 0.7 | 0.0 | 0 | 0.307+0.000 / 2.502+0.203 / 0.414+0.093 | 3549 | 505 / 32 MiB | 180 MiB |
| 1 | 7.779 | 7.559 | 13.969 | 34.601 | run 2 | 0.7 | 0.0 | 0 | 0.291+0.000 / 6.220+0.186 / 0.995+0.087 | 3549 | 505 / 32 MiB | 177 MiB |
| 2 | 4.659 | 4.521 | 8.313 | 19.395 | run 2 | 0.7 | 0.0 | 0 | 0.290+0.000 / 3.576+0.179 / 0.530+0.083 | 3549 | 505 / 32 MiB | 179 MiB |
| 4 | 3.150 | 3.018 | 5.515 | 11.235 | run 2 | 0.7 | 0.0 | 0 | 0.292+0.000 / 2.293+0.183 / 0.299+0.082 | 3549 | 505 / 32 MiB | 178 MiB |
| 8 | 2.768 | 2.676 | 4.074 | 8.281 | run 2 | 0.7 | 0.0 | 0 | 0.311+0.000 / 1.906+0.203 / 0.248+0.099 | 3549 | 505 / 32 MiB | 180 MiB |
| 12 | 2.770 | 2.685 | 4.265 | 7.574 | run 2 | 0.7 | 0.0 | 0 | 0.317+0.000 / 1.884+0.218 / 0.249+0.101 | 3549 | 505 / 32 MiB | 178 MiB |
| 16 | 3.434 | 3.419 | 4.615 | 8.369 | run 2 | 0.7 | 0.0 | 0 | 0.310+0.000 / 2.428+0.207 / 0.394+0.094 | 3549 | 505 / 32 MiB | 179 MiB |

Summary: median over 3 run(s) per thread count

| Threads | Mean ms | p50 ms | p99 ms | Max ms (worst run) | Speedup (mean) | Speedup (p50) | Fits 16 ms (p99)? |
|---|---|---|---|---|---|---|---|
| 1 | 7.824 | 7.598 | 14.129 | 35.177 | 1.00x | 1.00x | yes |
| 2 | 4.659 | 4.521 | 8.213 | 19.395 | 1.68x | 1.68x | yes |
| 4 | 3.150 | 3.018 | 5.515 | 11.476 | 2.48x | 2.52x | yes |
| 8 | 2.816 | 2.686 | 4.765 | 9.298 | 2.78x | 2.83x | yes |
| 12 | 2.797 | 2.710 | 4.265 | 8.465 | 2.80x | 2.80x | yes |
| 16 | 3.519 | 3.473 | 4.615 | 8.810 | 2.22x | 2.19x | yes |

Per-System CPU ms per Tick, summed over threads (jobs per Tick):

| Threads | FoodIndex | Hunger | Move | Eat | Wander | Starvation | Stats | FoodSpawner |
|---|---|---|---|---|---|---|---|---|
| 1 (run 0) | 0.295 (1) | 0.176 (489) | 0.872 (489) | 2.291 (489) | 2.564 (489) | 0.362 (489) | 0.952 (489) | 0.037 (1) |
| 2 (run 0) | 0.290 (1) | 0.230 (489) | 1.400 (489) | 2.285 (489) | 2.589 (489) | 0.483 (489) | 0.949 (489) | 0.036 (1) |
| 4 (run 0) | 0.304 (1) | 0.358 (489) | 2.853 (489) | 2.392 (489) | 2.993 (489) | 0.975 (489) | 1.072 (489) | 0.037 (1) |
| 8 (run 0) | 0.318 (1) | 0.747 (489) | 5.547 (489) | 2.783 (489) | 4.737 (489) | 2.024 (489) | 1.674 (489) | 0.042 (1) |
| 12 (run 0) | 0.329 (1) | 1.048 (489) | 9.069 (489) | 3.511 (489) | 7.403 (489) | 3.019 (489) | 2.468 (489) | 0.046 (1) |
| 16 (run 0) | 0.317 (1) | 1.374 (489) | 10.006 (489) | 3.762 (489) | 8.371 (489) | 3.507 (489) | 2.739 (489) | 0.047 (1) |
| 1 (run 1) | 0.291 (1) | 0.166 (489) | 0.846 (489) | 2.265 (489) | 2.549 (489) | 0.356 (489) | 0.949 (489) | 0.036 (1) |
| 2 (run 1) | 0.288 (1) | 0.233 (489) | 1.358 (489) | 2.282 (489) | 2.569 (489) | 0.483 (489) | 0.952 (489) | 0.036 (1) |
| 4 (run 1) | 0.286 (1) | 0.269 (489) | 2.672 (489) | 2.296 (489) | 2.794 (489) | 0.763 (489) | 0.965 (489) | 0.036 (1) |
| 8 (run 1) | 0.319 (1) | 0.572 (489) | 5.551 (489) | 2.634 (489) | 4.445 (489) | 1.664 (489) | 1.425 (489) | 0.040 (1) |
| 12 (run 1) | 0.320 (1) | 1.046 (489) | 8.137 (489) | 3.332 (489) | 6.574 (489) | 2.655 (489) | 2.062 (489) | 0.045 (1) |
| 16 (run 1) | 0.306 (1) | 1.542 (489) | 9.860 (489) | 3.700 (489) | 8.001 (489) | 3.676 (489) | 2.760 (489) | 0.046 (1) |
| 1 (run 2) | 0.290 (1) | 0.170 (489) | 0.831 (489) | 2.256 (489) | 2.538 (489) | 0.351 (489) | 0.948 (489) | 0.037 (1) |
| 2 (run 2) | 0.290 (1) | 0.236 (489) | 1.346 (489) | 2.281 (489) | 2.583 (489) | 0.500 (489) | 0.951 (489) | 0.036 (1) |
| 4 (run 2) | 0.292 (1) | 0.312 (489) | 2.595 (489) | 2.293 (489) | 2.800 (489) | 0.846 (489) | 0.992 (489) | 0.037 (1) |
| 8 (run 2) | 0.311 (1) | 0.686 (489) | 5.145 (489) | 2.630 (489) | 4.262 (489) | 1.879 (489) | 1.526 (489) | 0.040 (1) |
| 12 (run 2) | 0.317 (1) | 1.028 (489) | 8.077 (489) | 3.322 (489) | 6.314 (489) | 2.748 (489) | 2.184 (489) | 0.045 (1) |
| 16 (run 2) | 0.310 (1) | 1.358 (489) | 10.115 (489) | 3.689 (489) | 8.157 (489) | 3.521 (489) | 2.694 (489) | 0.047 (1) |

Process peak working set: 243.3 MiB

