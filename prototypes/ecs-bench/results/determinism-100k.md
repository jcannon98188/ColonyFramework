# Colony ECS prototype benchmark
- Runtime: .NET 11.0.0-rc.2.26504.105, Microsoft Windows 10.0.26200, X64
- Logical processors: 16
- Vector256 accelerated: True, AVX2: True, AVX-512F: False
- GC: server=False, latency=Batch, dynamic code (JIT)=True
- Scenario: colonists=100000, food=5000, debris=0, warmup=500, ticks=1000, seed=12345

## Determinism: colonists=100000, food=5000, debris=0
| Tick | 1 thr | 2 thr | 4 thr | 16 thr | equal |
|---|---|---|---|---|---|
| 1 | 26561efbd22154e5 | 26561efbd22154e5 | 26561efbd22154e5 | 26561efbd22154e5 | yes |
| 10 | b38eb86bb79e8fb9 | b38eb86bb79e8fb9 | b38eb86bb79e8fb9 | b38eb86bb79e8fb9 | yes |
| 50 | cec810139ff6a646 | cec810139ff6a646 | cec810139ff6a646 | cec810139ff6a646 | yes |
| 100 | b06e1d92ef610696 | b06e1d92ef610696 | b06e1d92ef610696 | b06e1d92ef610696 | yes |
| 250 | aa05a7a3cc013377 | aa05a7a3cc013377 | aa05a7a3cc013377 | aa05a7a3cc013377 | yes |
| 500 | 45f6b02630ddc330 | 45f6b02630ddc330 | 45f6b02630ddc330 | 45f6b02630ddc330 | yes |

Determinism PASS (hash changes between checkpoints: True)

## Determinism: colonists=100000, food=5000, debris=100000
| Tick | 1 thr | 2 thr | 4 thr | 16 thr | equal |
|---|---|---|---|---|---|
| 1 | 98c110667380751b | 98c110667380751b | 98c110667380751b | 98c110667380751b | yes |
| 10 | 35956e1c958c71ff | 35956e1c958c71ff | 35956e1c958c71ff | 35956e1c958c71ff | yes |
| 50 | c89c10e2e1123ca7 | c89c10e2e1123ca7 | c89c10e2e1123ca7 | c89c10e2e1123ca7 | yes |
| 100 | 79fd0135c3ecbe56 | 79fd0135c3ecbe56 | 79fd0135c3ecbe56 | 79fd0135c3ecbe56 | yes |
| 250 | 36389dbe0b3dfe54 | 36389dbe0b3dfe54 | 36389dbe0b3dfe54 | 36389dbe0b3dfe54 | yes |
| 500 | 9e75cd3ac2a420f6 | 9e75cd3ac2a420f6 | 9e75cd3ac2a420f6 | 9e75cd3ac2a420f6 | yes |

Determinism PASS (hash changes between checkpoints: True)

Process peak working set: 63.1 MiB

