---
id: TASK-1.22
title: Decide job assignment and colony AI building blocks
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:40'
updated_date: '2026-10-31 00:09'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.20
parent_task_id: TASK-1
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What does the Framework provide for job/task assignment and colonist decision-making, and what is left to Game authors? Cover: utility AI, behavior trees, GOAP/HTN planners as optional building blocks vs one blessed model, needs/mood Components, job and reservation (claiming) primitives that fix the double-claim problem found by the ECS prototype, work-priority tables, hauling/stockpile matching, spatial and reachability queries (region IDs, path costs from the pathfinding service), thinking-time budgets and staggered/sleeping AI under the scheduler, determinism and save rules, Plugin extension points. Pathfinding at scale is a separate, later decision; this ticket should state what it needs from it. Inputs: research on colony AI, data model, scheduler, determinism.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all eleven proposals accepted). Informed by the colony AI research (docs/research/colony-ai-and-jobs.md) and the ECS prototype's double-eat bug.

**Model (Q1):** building blocks, not a blessed AI model. The Framework supplies the claim primitive, work index, decision scheduling, reachability hook and anti-thrash guards; decision logic is ordinary Systems over Components.

**Helpers (Q2):** two optional allocation-free helpers in a separate package: a lexicographic priority-table chooser (priority class, work type order, distance) and utility scoring (considerations, curves, inertia and cooldown weights) for needs and leisure. Behavior trees and GOAP/HTN are not shipped in v1; they can be Plugin libraries built on the same claim and work-index blocks.

**Claims (Q3):** Claim is a built-in Relation owned by the target (claimant, target, slot) with capacity, optional amount, and release on job end, claimant death or target destruction. Two phases: agents propose in parallel, then a deterministic resolve step (sorted by priority key then Entity ID) grants or denies. Denied agents rescan next decision.

**Resolve cost (Q4):** resolution runs per target chunk bucket in parallel with ordered output, over batched sorted lists with no per-claim allocation. A losing proposal is a no-op retried next decision; no locks or rollback. Claim counters feed diagnostics.

**Work index (Q5):** an index of work, not agents. Work items are Entities with a work type and position; the Framework keeps per-work-type, per-chunk lists of available (unclaimed, valid) items with deterministic ordering and change tracking. Agents query "nearest N available of types" via bucket/spiral search bounded by radius and candidate cap, on the spatial index's chunk buckets.

**Hauling (Q6):** a generic optional supply/demand matcher (sources with amount, sinks with capacity and priority) producing haul work items, claiming both ends. Item model, stockpile rules and filters are Game author content.

**Decision scheduling (Q7):** agents are woken, not polled: on a hashed per-agent cadence or a trigger (job finished, need threshold, interrupt). A per-System decision budget per Tick carries remainders forward in deterministic order, reusing scheduler rates, offsets and sleeping bits.

**Anti-thrash (Q8):** replaceable Framework defaults: commitment window (urgent interrupts excepted), per-agent cooldowns on recently failed targets, and a circuit breaker on job-start rate; thrash counters in diagnostics.

**Needs from pathfinding (Q9):** (1) Reachable(from, to, movement class) in constant time via incrementally maintained region/connected-component IDs; (2) approximate path cost between positions per movement class; (3) deferred-completion path request/result API with a per-Tick budget. Failed reachability is cached per target and movement class. The later pathfinding-at-scale decision is bound to these.

**Content (Q10):** needs, mood, work types, priority tiers, skills, job content and starvation policy are Game author design. The test content package carries a documented example (needs with decay, utility System) but no required schema.

**Determinism, saves, Plugins (Q11):** claims, work items and decision cadence are ordinary Components/Relations (saved, hashed, migrated). Rebuildable data (work index lists, reachability caches) is not saved and is rebuilt on load. Decision randomness uses counter-based RNG keyed by agent and Tick. Plugins register work types, considerations, claim slot types and decision Systems; the default decision System is Overridable.

**Handed forward:** pathfinding-at-scale must provide the three queries above; package layout places the AI helpers package separately; the work index builds on the spatial index and diagnostics records add claim and thrash counters.
<!-- SECTION:FINAL_SUMMARY:END -->
