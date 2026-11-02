---
id: TASK-24
title: ProcGen noise utilities
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 03:46'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-4
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 51000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 9.3: generation is a pure function of (seed, chunk coordinate), so noise must be integer or fixed-point; float noise is excluded from the deterministic path. The shipped utilities are value, gradient, simplex, cellular, fBm, domain warping and region-keyed spot sampling, all keyed through the counter-based RNG so adding a consumer never shifts another. These depend only on the math layer and can be built while Core is in progress.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Value, gradient, simplex, cellular, fBm, domain warping and region-keyed spot sampling are implemented in fixed-point with committed golden hashes per function and seed
- [x] #2 Evaluating any coordinate in any order on any thread yields identical results
- [x] #3 Vector paths and the scalar fallback produce bit-identical output
- [x] #4 A benchmark reports throughput per utility
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
New package src/Colony.ProcGen (depends on Core) with tests/Colony.ProcGen.Tests. API: NoiseKey (seed, Hash64(stage name)), static Noise (value, gradient, simplex, cellular F1/F2+cell, fBm, domain warp; 2D and 3D; Fixed32 in/out, point and batch forms), FbmSettings/NoiseBasis, Spot/SpotSampler (2D; region-keyed through RandomSource(seed, Hash64.Combine(stage, region))). Kernels written once over IIntLanes<T> (Lanes1/Lanes128/Lanes256); per-lattice-point hash is two murmur3 fmix32 rounds so it runs on int32 lanes. 40 committed XXH64 golden hashes (20 outputs x 2 seeds). Throughput (Release, AVX2, Vector256/scalar Msamples/s): Value2D 130/58, Simplex2D 51/31, Cellular2D 42/6, Fbm2D 4-oct 13/4, Simplex3D 34/18. Benchmark criterion met by a throughput-report test; the BenchmarkDotNet project waits for TASK-30 per conventions. Caveats: fBm coordinate*frequency wraps at the Fixed32 range (documented), spot sampling is 2D only, kernel changes require regenerating goldens and bumping the generation version once TASK-25 introduces it.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed-point noise utilities merged to integration/v1 (e6778a7): value, gradient, simplex, cellular, fBm, domain warping and region-keyed spot sampling with scalar/Vector128/Vector256 paths proven bit-identical, order and thread independence tests, 40 golden hashes, zero steady-state allocation, throughput report. 79 ProcGen tests; solution 377/377.
<!-- SECTION:FINAL_SUMMARY:END -->
