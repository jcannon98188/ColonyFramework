---
id: TASK-43
title: Fields halo and row-span kernel API
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 22:13'
updated_date: '2026-11-02 02:55'
labels:
  - ready-for-agent
  - implementation
dependencies: []
ordinal: 70000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up to TASK-38: the Fields stencil pass reached 1.46x (target 2x) because the per-cell StencilInput.Read(x,y,z) API costs tier checks and index math per call. A kernel indexing its own halo directly ran Life in 14 Mcycles vs 29 through Read. Add a public halo / row-span kernel API, switch the FieldScenario kernels to it without changing any hash, and reduce RunParallel dispatch overhead (8 dispatches per Tick for in-place stencils with 40 chunks). Also decide whether StencilScratch blocks should be freed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A public halo or row-span kernel API exists with docs and tests, preserving edge-chunk semantics
- [x] #2 FieldScenario kernels use it with world hashes and goldens unchanged
- [x] #3 colonist-100k Fields share of the single-worker Tick is at least 2x faster than the pre-TASK-38 baseline (16.1 ms), measured with the A/B method in CONVENTIONS
- [x] #4 0 B allocation per Tick preserved
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at b703d34. StencilHalo<T> ref struct, StencilHaloKernel<T>, Stencil.DoubleBuffered/InPlace halo overloads; FieldScenario kernels use halo rows, hashes and goldens unchanged. colonist-100k Fields share 1 worker 9.38 -> 1.07 ms (8 workers 2.72 -> 0.31) against the integration/v1 baseline. ThreadStatic scratch replaced by FieldSet-owned StencilScratchPool freed on Dispose. Gaps: zero-alloc gates and LoopbackLockstepTests multi-process test flake under whole-solution load (TASK-42); spec Fields section lacks halo API.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Public halo/row-span stencil kernel API; scenario kernels switched with hashes unchanged; Fields share of the 100k Tick far below the 2x target; scratch is now owned and freed by the World.
<!-- SECTION:FINAL_SUMMARY:END -->
