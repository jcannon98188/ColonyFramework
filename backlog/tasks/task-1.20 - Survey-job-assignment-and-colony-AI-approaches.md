---
id: TASK-1.20
title: Survey job assignment and colony AI approaches
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:40'
updated_date: '2026-10-30 23:47'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How do Dwarf Fortress, RimWorld, Oxygen Not Included, Factorio-style bots, The Sims-like and academic/industry sources do job/task assignment, needs, priorities and colonist decision-making at scale (utility AI, behavior trees, GOAP/HTN, work-priority tables, reservation/claiming of targets, job queues, hauling, preemption, thinking-time budgets)? Pitfalls (double-claiming, job thrash, starvation, O(n*m) assignment scans, unreachable jobs) and what scales to 100k+ agents. Output: docs/research/colony-ai-and-jobs.md with cited primary sources.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Findings in `docs/research/colony-ai-and-jobs.md`. Claims are labelled primary, decompile, community wiki, secondary or inference; the report's "Source quality and gaps" section says what was read in full.

- Pull, not push: every verifiable shipped colony sim has idle agents pull work (priority table, then closest valid target). Dwarf Fortress moved to job postings that dwarves apply to (2014 dev log). Factorio bots use a global allocator only because robots are interchangeable. No colony sim found uses global optimal assignment (Hungarian, auctions).
- Priority is lexicographic, not utility: RimWorld orders by think-node priority, work-type order, work-giver order, then distance; Oxygen Not Included by priority class, sub-priority, proximity. Utility scoring is used for needs and leisure (The Sims), not work.
- Claiming is separate from choosing: RimWorld (1.0 third-party decompile) picks optimistically then reserves at job start; a reservation records claimant, job, target, layer, pawn capacity and stack amount, released at job end; failed reserve aborts and rescans. This avoids the prototype's double-eat bug.
- Thrash is handled by commitment: RimWorld circuit breaker (more than 10 jobs in 10 Ticks forces Wait); The Sims Medieval decides only on an empty action queue; Game AI Pro recommends inertia weights and cooldowns.
- Scan cost dominates (candidate scans, unreachable targets, thrash), not decision logic: DF fixes "jobs that check too many items"; Factorio moved to per-chunk lists and spiral search; RimWorld thinks on a 30-Tick hashed cadence; Factorio forces robot updates every 20 or 60 Ticks.
- GOAP and HTN do not fit mass agents: Project Highrise dropped its planner for authorial scripts with open-loop action queues.
- Inferences for 100k+ agents (flagged as inference): wake agents by trigger or cadence; index the work, not the agents; two-phase claiming (parallel proposals, deterministic sorted resolve, claims as Relations owned by an Entity); reachability cached per movement class; thrash guards and per-System decision budgets as Framework defaults. Candidate Framework building blocks: claim primitive, work index, reachability query, decision scheduling, anti-thrash, diagnostics. Needs, work types, priority tiers, job content and starvation policy left to Game authors.

Gaps (low confidence): Oxygen Not Included internals; DF dev log read only partly and v50 design unverified; RimWorld decompile is 1.0 and third-party; Halo 2 and Sims 3 papers behind login; Unreal Mass pages were landing pages only; no per-agent AI cost figures in any source.

Feeds the jobs and colony AI decision ticket. Note the user also asked about A*/Dijkstra/GOAP-style AI helpers; GOAP is covered here as a poor fit for mass agents, and search algorithms belong with the pathfinding work.
<!-- SECTION:FINAL_SUMMARY:END -->
