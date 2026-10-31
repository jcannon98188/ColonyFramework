---
id: TASK-1.7
title: 'Decide scheduler, tick model and parallelism'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.5
parent_task_id: TASK-1
ordinal: 8000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How do Systems get scheduled: dependency analysis from declared Component access, per-system tick rates, parallel job execution, phases/ordering, pause and speed multipliers, how Plugins add Systems. Fixed-step Ticks are already decided.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all eight proposals accepted):

1. Systems: source-generated; author writes a method/struct with a typed query, generator emits static Run entry, declared access set (Components and Resources) and ordering metadata. No reflection, no virtual dispatch in hot path, Native AOT-friendly. Plugins register Systems through the same metadata.
2. Ordering: fixed ordered Phases (e.g. Begin, Simulate, Late, End) with After/Before constraints inside a Phase; command buffers merge at each Phase boundary; stable topological sort with ties broken by stable System ID (registered name); cycles and ambiguous conflicting pairs reported at startup.
3. Parallelism: both across non-conflicting Systems and across chunks within a System, on the Framework's own fixed work-stealing worker pool (no per-Tick allocation); chunks are the unit of work; configurable thread count; results independent of thread count; pool behind an interface so hosts can supply their own.
4. Rates and idle work: `every N Ticks` with optional offset (default stable offset derived from System ID to spread load); chunk-level skipping via per-chunk change versions and an optional "asleep" enable bit, for Systems that opt in.
5. Loop: the Framework never owns the loop; host calls World.Tick(); provided TickClock helper handles accumulator, pause, speed multiplier and catch-up cap; wall-clock time never enters the simulation.
6. Determinism under parallelism: a System writes only its declared columns (partitioned by chunk); cross-Entity effects go through command buffers or events; reductions combine per-chunk results in chunk order; access declarations checked in debug builds; determinism test mode runs the same Ticks at 1 and N threads and compares World hashes.
7. Events: typed event queues as Resources, written via per-System buffers merged in System order, readable by later Phases within the Tick, cleared at a stated point, saved only if marked persistent; no direct callbacks.
8. Cross-Tick work: deferred-completion Systems start in Tick T and apply results at a fixed later Tick T+k; GPU specifics go to the GPU Backend ticket. A System exception aborts the Tick, marks the World faulted naming the System, no per-System catch in release.

Passed on: Decide determinism contract (hash-compare test mode, command-buffer merge order now defined), Decide GPU Backend (deferred-completion model), Decide Plugin architecture (System registration via generated metadata, Native AOT).

**Amendment (CPU-only v1):** the GPU deferred-completion specifics are out of v1; deferred-completion Systems remain for CPU work (pathfinding requests, background loads). Added from later decisions: a Streaming Phase at the Tick boundary and an Input Phase (see the input and streaming decisions).
<!-- SECTION:FINAL_SUMMARY:END -->
