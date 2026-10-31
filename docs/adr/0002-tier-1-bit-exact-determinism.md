---
status: accepted
---
# Tier 1 determinism: bit-identical across threads, instruction sets and Windows/Linux x86-64

For the same Framework build, Plugin set, seed and input log, World state is bit-identical every Tick regardless of thread count, SSE/AVX/AVX-512 or Windows/Linux x86-64. This buys replays, lockstep networking, desync debugging and golden-hash tests, at the price of a Framework math layer (fixed logical SIMD width, fixed-order reductions, no FMA contraction, own transcendentals, fixed-point types) and analyzers banning `MathF`, `Vector<T>`, wall-clock time and unordered collections in Systems.

## Considered options

- **Best-effort determinism (same machine only):** much cheaper to build, but makes replays, networking and cross-platform saves unreliable.
- **Tier 1 on x86-64 with ARM64/macOS best effort (chosen):** extending the guarantee to ARM64 was deferred because it needs separately verified float behaviour.

## Consequences

Plugins must declare Deterministic or NonDeterministic; a NonDeterministic Plugin drops the replay guarantee. Float results from hardware-width-dependent operations are not allowed in simulation code, so game logic that wants speed uses fixed-point or the math layer.
