---
id: TASK-43
title: Fields halo and row-span kernel API
status: To Do
assignee: []
created_date: '2026-11-01 22:13'
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
- [ ] #1 A public halo or row-span kernel API exists with docs and tests, preserving edge-chunk semantics
- [ ] #2 FieldScenario kernels use it with world hashes and goldens unchanged
- [ ] #3 colonist-100k Fields share of the single-worker Tick is at least 2x faster than the pre-TASK-38 baseline (16.1 ms), measured with the A/B method in CONVENTIONS
- [ ] #4 0 B allocation per Tick preserved
<!-- AC:END -->
