---
id: TASK-8
title: 'Resources, typed events, enable bits and System rates'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 08:05'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-7
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 35000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec sections 4.1 and 4.3: Resources are World-owned singleton unmanaged structs in a keyed store with declared access, saved with the World; events are typed queues held as Resources, written through per-System buffers merged in System order and cleared at a stated point, with no callbacks; frequent state flips use enable bits rather than add/remove tags; Systems run every N Ticks with an optional offset defaulting from the System ID and skip chunks via change versions or an asleep bit. These complete the data model Systems rely on before Fields and persistence are built on top.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Resources are registered with declared access, participate in scheduler conflict detection and are included in the World hash
- [x] #2 Typed event queues written from parallel Systems are observed in System order then record order, cleared at the documented point, and are deterministic across thread counts
- [x] #3 Enable bits per Component exist, iteration respects them, and flipping a bit causes no structural change
- [x] #4 Every-N-Ticks with offset runs Systems on the expected Ticks; chunks skipped by change version or asleep bit are counted and never visited
- [x] #5 All of the above pass the determinism harness scenario across thread counts with zero steady-state allocation
- [x] #6 A structural op that loses a same-Phase conflict (spec 4.1 first-in-stable-order) produces a deterministic failure event delivered the next Phase, naming the Entity and the op
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged into integration/v1 at 3ed91c4 (2026-10-31); conflict with TASK-13 in World.Playback resolved by keeping both the sync-point ChangeCounter bump and the StructuralEpoch bump. Full solution Release green twice (498 total, 1 skipped AOT). Resource id 0 in every schema is the built-in persistent StructuralFailure queue (WorldBuilder.StructuralFailureQueueName); user Resources start at 1. Event queues live in per-(System,job) CommandBuffers (Kind.Emit) and merge at the Phase boundary so they never conflict; value Resources conflict like Components. Queues clear at the End boundary before End's buffers merge (End-emitted events survive into the next Tick). Conflict vs Stale: structural epoch (one per Phase merge / host Playback) plus generation check in EntityIndex; failure event carries Entity, Op, Component, SystemId (-1 for host), Reason, delivered next Phase, cleared after the Tick. Enable bitsets sit in-chunk after the Entity column (64-byte aligned) so chunk capacities shrank slightly (colonist 2672); World and schema hash values changed (no saves existed). Scheduler honours enable bits at chunk granularity; row-level filtering via EnabledRows, or SystemDefinition.IgnoreEnableBits. Transient Resources and non-persistent queues are excluded from the World hash (spec silent; documented). Follow-ups: TASK-11 generator does not yet emit ReadsResources/WritesResources/IgnoreEnableBits from attributes (generator follow-up, consider for the code-review fix pass); World.SetEnabled from a chunk job onto another chunk is unguarded (documented as not allowed); Fields FieldHashTests gained expected.Append(0) for the empty id-0 queue.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
author: @jason
created: 2026-10-31 03:00
---
Hand-forward from TASK-6: spec 4.1 conflicting-ops rule (first-in-stable-order wins, loser gets a deterministic failure event next Phase) is not implemented because events did not exist; implement it here once typed events land. Stale ops are currently skipped and counted in StructuralCounters.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Resources (value + typed event queues) with declared access and conflict detection, persistent Resources in the World hash; events from parallel Systems observed in System then record order, cleared at the End boundary, identical across thread counts; per-Component enable bits (non-structural flips, chunk-level skipping, EnabledRows); Every-N with offset plus change-version/asleep/disabled chunk skipping with exact counters; deterministic StructuralFailure events (Conflict vs Stale) delivered next Phase. Colonist scenario green on 1/2/4/N threads with zero steady-state allocation. Merged at 3ed91c4.
<!-- SECTION:FINAL_SUMMARY:END -->
