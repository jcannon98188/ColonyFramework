---
id: TASK-1.25
title: Decide pathfinding at scale
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:27'
updated_date: '2026-10-31 01:32'
labels:
  - 'wayfinder:grilling'
dependencies: []
parent_task_id: TASK-1
ordinal: 26000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Decide the CPU pathfinding service design for large z-level Worlds, from docs/research/prototype-gpu-pathfinding.md: bounded or hierarchical flow Fields, a second HPA* level (single level is about 14x costlier per request on the 512x512x64 World), time-sliced or lazy repair, clustered vs uniform toggles, region-ID upkeep, request budgets and queue, and the three queries the jobs decision requires (Reachable by movement class, approximate cost, budgeted deferred path request). GPU is out of v1.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all nine proposals accepted; Q2 cap changed from 8 to a configurable limit). Informed by docs/research/prototype-gpu-pathfinding.md. CPU-only; GPU is not in v1.

**Scope (Q1):** the Framework ships a pathfinding building block: region IDs, a chunk graph with HPA*, shared-goal flow Fields, and the three queries the jobs decision needs (Reachable by movement class, approximate path cost, budgeted deferred path request/result), plus a "next step toward goal" query so Games can follow a flow Field or path without owning that logic. Unit movement stays with the Game author, who gets a path-follow helper.

**Movement classes (Q2):** Game-defined Definitions, each with its own passability rule over World Fields; region IDs and the chunk graph are kept per class and built lazily on first use. The class count is a per-World setting (default 32, hard ceiling 256 via an 8-bit class ID); unused classes cost nothing.

**Bounded Fields (Q3):** flow Fields cover the goal chunks plus a configurable radius, built lazily, with an LRU cap under a Field memory budget in the memory model. Units outside the bound use HPA* to reach the Field's edge, then hand off. Fields exist only for loaded chunks, so this composes with streaming.

**Repair (Q4):** layered: per-chunk dirty marking, lazy repair when a unit reads the Field, a per-Tick work budget with a staleness bound (units use the stale Field until repaired), and an up-front rebuild-vs-incremental threshold by dirty-chunk count (the prototype showed CPU incremental repair can cost more than a rebuild). Only Fields with active readers are repaired.

**Hierarchy (Q5):** two-level chunk graph (a coarser level of for example 4x4x4 chunks per cluster, built lazily and optional by World size). Paths are approximate (documented, within a few percent of optimal), refined near the unit by local A*; exact only via flow Fields or local A*.

**Request budgeting (Q6):** a queue with a per-Tick budget counted in work units (nodes expanded), not wall time. Requests process in canonical order (priority class, then request ID); results arrive N Ticks later (configurable) merged in canonical order; a request that misses its deadline fails with a deterministic event instead of blocking.

**Dynamic obstacles (Q7):** the service consumes the World's per-chunk change feed, with no separate notify API. Region upkeep is incremental: merges are cheap; splits trigger a bounded local relabel with a rebuild of the affected chunk set as the fallback.

**Determinism (Q8):** Tier 1. Integer distances, ties broken by neighbour index, fixed HPA* tie-breaking, work-unit budgets. Authoritative state (regions, Fields in use) is hashed; caches are rebuildable and excluded from saves. The determinism harness covers budget-limited and time-sliced behaviour.

**Not in v1 (Q9):** GPU Fields, any-angle or navmesh pathfinding, crowd avoidance and local steering, multi-agent collision (Game authors add these as Systems); the movement-class and Field interfaces stay open enough to add them.

**Handed forward:** the streaming ticket decides Field and region residency for unloaded chunks and how the chunk graph and regions are saved or rebuilt; package layout places the pathfinding service and path-follow helper; the testing strategy gains pathfinding scenarios (unreachable targets, toggles, budget exhaustion).
<!-- SECTION:FINAL_SUMMARY:END -->
