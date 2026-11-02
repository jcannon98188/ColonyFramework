---
id: TASK-22
title: Claims and decision scheduling
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 13:49'
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
- [x] #1 Parallel proposals against the same target resolve identically across thread counts with the specified ordering and capacity rules
- [x] #2 Claims release on job end, claimant destruction and target destruction, and are saved and restored as ordinary Relations
- [x] #3 Decision cadence and budget wake the expected agents per Tick and carry remainders forward deterministically
- [x] #4 Anti-thrash defaults are present, measurable in a thrash scenario, and replaceable by a Game
- [x] #5 Claim resolution of the previous decision round runs in the Begin Phase as specified
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged into integration/v1 at c82ce82 (2026-10-31). Full solution Release green twice (642 total, 1 skipped AOT); no Core changes, hash layout untouched. New package src/Colony.Colony (spec section 3 'Colony' package, Core-only reference), namespace Colony.Jobs (Colony.Colony would shadow the root namespace); tests/Colony.Colony.Tests (23). API: builder.AddClaims(ClaimOptions) -> ClaimSet (Relation colony.claim, Components ClaimProposal/ClaimGrant/ClaimAgentState, ClaimCapacity on targets, events ClaimGranted/Denied/Lost, Resource ClaimStats, counters colony.claims.*); claims.TryPropose / claims.Release from Game Systems; ClaimPolicy abstract (Default/None/WithOptions); builder.AddDecisionState() + AddDecisionSystem(DecisionSystemDefinition) -> DecisionBudget Resource; DecisionCadence.IsDue. Resolver: gather per chunk job -> counting sort by target bucket -> RunParallel per bucket sorted (Target, Priority desc, Claimant) -> serial apply in bucket order; colony.claims.resolve runs in Begin. Buckets are Target.Index >> BucketShift because Core exposes no Entity chunk-location API (spec says target chunk; same determinism guarantees). Claim events clear at the End boundary so they must be read by a Simulate/Late System. Thrash scenario (120 Ticks): defaults Denied 528k / Granted 7,151 vs ClaimPolicy.None Denied 1.17M / Granted 1,942. Zero steady-state allocation with 4 workers. AC #2 left unchecked: release on job end / claimant destruction / target destruction is tested, and Claims are proven to be plain Relations with no side tables (hash + RebuildRelationIndex), but the save/restore round trip waits on TASK-14 - add a ClaimScenario round trip there or in the fix pass. Follow-ups: link ClaimScenario into tests/Colony.Testing.AotHost; Core API for Entity chunk location would allow true per-chunk bucketing.

AC #2 save/restore clause closed by TASK-14: ContinuationTests covers the claims scenario (save at T, continue to T+K, hash-identical) and the claims-small golden save round-trips.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Claims as a built-in Relation with two-phase deterministic assignment (parallel proposals, Begin-Phase resolve sorted by priority then Entity ID within capacity), release on job end / claimant death / target destruction, hashed decision cadence with per-System budget and remainder carry-forward, and replaceable anti-thrash defaults measurably cutting denials in a thrash scenario. Thread-count invariant, zero-alloc. Merged at c82ce82; save/restore round trip deferred to TASK-14.
<!-- SECTION:FINAL_SUMMARY:END -->
