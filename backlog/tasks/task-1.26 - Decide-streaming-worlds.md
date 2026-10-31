---
id: TASK-1.26
title: Decide streaming worlds
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:28'
updated_date: '2026-10-31 01:33'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.25
parent_task_id: TASK-1
ordinal: 27000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Decide streaming: active simulated region, hibernation and catch-up rules for Entities in unloaded chunks, ChunkProvider contract, interaction with saves and with the pathfinding-at-scale design (region IDs, chunk graph, Field residency). v1 is bounded but designed for streaming. GPU is out of v1.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all ten proposals accepted). Builds on the World/Field, save/load, procgen, input/replay and pathfinding-at-scale decisions. CPU-only; GPU residency is not in v1.

**Scope (Q1):** v1 ships the seams and a basic working implementation: a residency manager driven by Game focus points, backed by the save container for unload/reload and the generator for first load. It is exercised in the bounded World by forcing chunks out and back; the bounded World with everything resident is the "never unload" policy.

**Residency (Q2):** each chunk is Active (simulated each Tick), Hibernating (resident, not simulated) or Unloaded (on disk, or not yet generated). Transitions happen only in a Streaming Phase at a Tick boundary, in canonical chunk order, driven by focus points and radii. Focus points are Game inputs submitted as Input Commands, so the streaming schedule is deterministic and replayable, and transitions are recorded.

**Entities (Q3):** Entities are chunk-owned by position and hibernate/unload with their chunk, except Entities a Game marks pinned (always resident, for example colonists or caravans), which live in a global pinned store and may be positioned in unloaded space. Entity IDs stay stable across unload and reload.

**Catch-up (Q4):** frozen by default. Systems opt in by declaring a deterministic function of (state, elapsed Ticks); catch-up runs in the Streaming Phase on activation in canonical order under a per-Tick work budget (spread over Ticks when large), with a cap on replayed Ticks beyond which the System's function handles long absence.

**Boundaries (Q5):** a ring of Hibernating chunks always surrounds Active chunks so halo reads are valid. Stencil exchange across the boundary treats the frozen side as a fixed boundary condition (documented limitation).

**Pathfinding (Q6):** Hibernating chunks keep full pathfinding structures. Unloaded chunks keep a small saved per-chunk summary (portals and per-class region labels). A path request through an Unloaded chunk fails with a deterministic "needs loading" result carrying the chunk coordinate.

**Persistence (Q7):** an unloaded chunk's state is exactly its save section, so unload/reload equals save/load of one chunk, with a manifest listing existing chunks. A full save writes resident chunks and copies unloaded sections unchanged. Never-generated chunks are not stored and are generated on demand from (seed, coordinate). The World hash covers generated chunks in canonical order, with unloaded chunks contributing their stored hash.

**Loading (Q8):** loads and generation run on background workers with a bounded queue; the Streaming Phase applies completed loads at Tick boundaries with a fixed delay of N Ticks (configurable). A load not ready by its deadline stalls the Tick rather than applying late, and diagnostics report the stall. Games can submit prefetch hints.

**Budgets (Q9):** resident chunk count is bounded by a World budget from the memory model, with deterministic eviction (farthest from focus points first, ties by chunk coordinate), Hibernating before Active, and a budget-pressure event to the Game.

**Unbounded Worlds (Q10):** out of v1. Streaming exists for memory and load time. Chunk coordinates are kept 64-bit-safe so unbounded Worlds can be added later.

**Handed forward:** the spec assembly must state the Streaming Phase placement among the fixed Phases; package layout places the residency manager, loader workers and catch-up API; testing adds forced evict/reload round trips, replay of streaming schedules and load-deadline stall cases.
<!-- SECTION:FINAL_SUMMARY:END -->
