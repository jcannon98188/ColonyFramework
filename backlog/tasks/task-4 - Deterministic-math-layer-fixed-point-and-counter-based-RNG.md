---
id: TASK-4
title: 'Deterministic math layer, fixed-point and counter-based RNG'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 02:34'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-3
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 6 makes Tier 1 bit-exact determinism the contract (ADR 0002): the same build, seed and inputs must hash identically across thread counts, SSE/AVX/AVX-512 and Windows/Linux x86-64. That is impossible if simulation code reaches MathF, Vector<T> or System.Random, so the Framework needs its own math layer before any System is written. Pathfinding costs, TilePos offsets and generation noise are specified as fixed-point, and all randomness is counter-based (Philox-like) addressed by (World seed, stream key, counter) so adding a consumer never shifts another consumer. The hash primitive used for World and chunk hashes belongs here too.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Fixed32 (16 fractional bits) and Fixed64 types with vector forms, deterministic trig and sqrt tables, and saturating conversions; golden-value tests cover edge cases (overflow, negative zero, rounding)
- [ ] #2 A float math layer with a fixed logical SIMD width, fixed-tree reductions, no FMA contraction, defined float-to-int saturation and own transcendental functions; every SIMD path has a Vector128 fallback and tests prove the wide and fallback paths produce identical bits
- [ ] #3 A counter-based RNG addressed by (seed, stream key, counter) with tests showing streams are independent and reproducible across runs and thread counts
- [ ] #4 A hash primitive suitable for chunk and World hashing with committed golden hashes for fixed inputs
- [ ] #5 Public API is safe (no pointers); the only unsafe code is internal
<!-- AC:END -->
