---
id: TASK-24
title: ProcGen noise utilities
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
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
- [ ] #1 Value, gradient, simplex, cellular, fBm, domain warping and region-keyed spot sampling are implemented in fixed-point with committed golden hashes per function and seed
- [ ] #2 Evaluating any coordinate in any order on any thread yields identical results
- [ ] #3 Vector paths and the scalar fallback produce bit-identical output
- [ ] #4 A benchmark reports throughput per utility
<!-- AC:END -->
