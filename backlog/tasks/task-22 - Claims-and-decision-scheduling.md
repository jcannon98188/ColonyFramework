---
id: TASK-22
title: Claims and decision scheduling
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 13:01'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-9
  - TASK-11
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 49000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 9.2 (decided in the job assignment ticket): Claim is a built-in Relation owned by the target (claimant, target, slot) with capacity, optional amount and release on job end, claimant death or target destruction. Assignment runs in two phases so it parallelises deterministically: agents propose in parallel, then a resolve step sorted by priority key then Entity ID per target chunk bucket grants or denies; denied agents rescan next decision. Agents wake on a hashed cadence or trigger under a per-System decision budget that carries remainders forward. Anti-thrash defaults (commitment window, per-agent cooldowns on failed targets, job-start circuit breaker) are replaceable. Needs, mood, skills and job content stay Game territory.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Parallel proposals against the same target resolve identically across thread counts with the specified ordering and capacity rules
- [ ] #2 Claims release on job end, claimant destruction and target destruction, and are saved and restored as ordinary Relations
- [ ] #3 Decision cadence and budget wake the expected agents per Tick and carry remainders forward deterministically
- [ ] #4 Anti-thrash defaults are present, measurable in a thrash scenario, and replaceable by a Game
- [ ] #5 Claim resolution of the previous decision round runs in the Begin Phase as specified
<!-- AC:END -->
