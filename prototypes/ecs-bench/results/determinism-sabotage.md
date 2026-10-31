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
| 10 | b38eb86bb79e8fb9 | 5777a06051db1d15 | fa0f56f97d78efa6 | a9c2706808291804 | NO |
| 50 | cec810139ff6a646 | 8320a6cf8173f502 | 1ab56e2b929b73de | f6d8b34655043a37 | NO |
| 100 | b06e1d92ef610696 | 9240a0d8c9657580 | e44407d86e9eff1f | 10ea673499b0b04d | NO |
| 250 | aa05a7a3cc013377 | 7b3a36d1f55b4577 | 5c2768621ca60cfd | de9d7c657d9a7ede | NO |
| 500 | 45f6b02630ddc330 | c9586624c46d7435 | 21ba3df045e1646a | b9115af9771b9de2 | NO |

Determinism FAIL (hash changes between checkpoints: True)

## Determinism: colonists=100000, food=5000, debris=100000
| Tick | 1 thr | 2 thr | 4 thr | 16 thr | equal |
|---|---|---|---|---|---|
| 1 | 98c110667380751b | 98c110667380751b | 98c110667380751b | 98c110667380751b | yes |
| 10 | 35956e1c958c71ff | 575131122a4975e5 | 857e1064cb22cbc0 | 70be8afd6841a48c | NO |
| 50 | c89c10e2e1123ca7 | 67c79af22486f8f6 | 92f087042a29cf24 | 99804fb6365c686e | NO |
| 100 | 79fd0135c3ecbe56 | c6c7d59e5be51b4b | 349f5ac8d801c8de | 61ddefa5be1b68fc | NO |
| 250 | 36389dbe0b3dfe54 | ef4c4909f3628ea8 | 59cc26157876db97 | f7e2ca1907773c55 | NO |
| 500 | 9e75cd3ac2a420f6 | c41ec70a71a36a05 | 2d20445f415377f6 | e14168497673592a | NO |

Determinism FAIL (hash changes between checkpoints: True)

Process peak working set: 63.1 MiB

