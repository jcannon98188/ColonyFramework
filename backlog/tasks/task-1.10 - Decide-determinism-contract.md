---
id: TASK-1.10
title: Decide determinism contract
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.7
  - TASK-1.9
parent_task_id: TASK-1
ordinal: 11000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What exactly is guaranteed: bit-for-bit CPU determinism given seed and inputs (RNG streams, iteration order, float policy, parallel reductions), and the opt-in rules for GPU Fields (fixed-point/integer math vs documented non-determinism)? What do replays and saves promise?
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all ten proposals accepted; Q7 clarified that Morton order applies only to the order of whole Field chunks, never to cells inside a chunk, which stay row-major).

**Guarantee (Q1):** Tier 1 (guaranteed): for a given Framework build plus the same Plugin set, seed and input log, World state is bit-identical at every Tick, across thread counts, CPU models (SSE/AVX/AVX-512) and Windows/Linux on x86-64. Tier 2 (best effort): ARM64/macOS, tested in CI, mismatches are bugs not promises. No guarantee across Framework versions; save compatibility belongs to save/load.

**Float policy (Q2):** simulation float goes through a Framework math layer: fixed logical SIMD width regardless of hardware, fixed-tree-order reductions, no implicit FMA contraction (FMA only where results are identical), float-to-int via helpers with defined saturation, own deterministic sin/cos/exp etc. (never MathF). Analyzer forbids Math/MathF transcendentals, Vector<T> and hardware-width-dependent ops in Systems.

**Fixed-point (Q3):** ship 32-bit (16 fractional bits) and 64-bit fixed-point types with vectors, deterministic trig/sqrt tables. Used by Exact GPU kernels and TilePos sub-tile offsets.

**RNG (Q4):** counter-based RNG (Philox-like), stream addressed by (World seed, stream key, counter), key typically (System ID, Entity ID, Tick). No global Random; identical implementation on CPU and WGSL; analyzer bans System.Random.

**Other sources (Q5):** analyzer bans DateTime/Stopwatch, Guid.NewGuid, reference-type GetHashCode and unordered managed collections in Systems. All orderings end with Entity ID tiebreak. Framework hashing uses fixed algorithms (xxHash-style).

**Plugins (Q6):** Framework enforces what it can (shipped analyzers, 1-vs-N-thread hash harness any Plugin can run); rest is the Plugin author's obligation. Plugins declare Deterministic (default) or NonDeterministic; any NonDeterministic Plugin makes the World report non-deterministic and loses the replay guarantee.

**World hash (Q7):** computed from chunk hashes: Field chunks in Morton order of chunk coordinate (cells within a chunk hashed in row-major storage order), entity archetype chunks in archetype-ID then chunk order. Covers Component columns, deterministic Fields, Resources, RNG state; excludes padding, Approximate Fields, change versions and caches. Available at any Tick, cheap enough to run every N Ticks, recorded in replay logs for desync detection.

**Replays (Q8):** initial state (or save) + seed + per-Tick input command log; guaranteed under Tier 1 when all Plugins are Deterministic and no Approximate Field feeds simulation. Framework provides log format and a verifier that re-runs and compares World hashes; recording the log is the Game author's job.

**Approximate Fields feeding simulation (Q9):** allowed but explicit: a System reading an Approximate Field declares it, marking the World non-deterministic in the hash report. Recommended: Approximate Fields for display/soft effects, or quantise before they feed decisions; strict route is Exact kernels.

**Saves (Q10):** load-then-continue is bit-identical to never saving; save holds all future-affecting state (Components, Fields, Resources, RNG state, Tick count) and nothing derived from addresses or capacity. Format and versioning belong to the save/load ticket.

**Handed forward:** save/load must include RNG state and Plugin set identity and verify the World hash on load; Plugin ticket must carry the Deterministic/NonDeterministic declaration and analyzer packaging; testing/benchmark strategy must include the 1-vs-N-thread and cross-CPU-width hash tests and CPU-vs-GPU Exact kernel parity; package layout must place the math layer, fixed-point types and RNG.

**Amendment (CPU-only v1):** with no GPU in v1, Q9 (Approximate Fields feeding simulation) and CPU-vs-GPU Exact-Kernel parity have no v1 subject; the Exact/Approximate classification is kept as the rule for any future GPU package, where only Exact Kernels run in deterministic Worlds. Fixed-point types remain for TilePos offsets, pathfinding costs and deterministic generation noise.
<!-- SECTION:FINAL_SUMMARY:END -->
