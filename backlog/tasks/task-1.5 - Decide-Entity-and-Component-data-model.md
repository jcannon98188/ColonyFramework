---
id: TASK-1.5
title: Decide Entity and Component data model
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.1
parent_task_id: TASK-1
ordinal: 6000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What is the core ECS data model: Entity identity (ID + generation), storage layout, Component rules (unmanaged structs, handles/interned IDs for strings and references), tags, relationships/hierarchy, structural-change handling (command buffers), query API shape. Informed by the ECS survey.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all nine proposals accepted):

1. Storage: archetype/chunk base, fixed-size 64-byte aligned columns (structure of arrays); opt-in non-fragmenting mode (toggle bit or sparse pool) for Components that flip often.
2. Entity ID: 64-bit (32-bit index + 32-bit generation); stable across save/load; index safe to hand to GPU; stale IDs fail the generation check.
3. Transient state: model frequent states as enable bits or data fields; add/remove tag Components only for rare structural changes. Framework provides enableable Components and documents the rule.
4. References: plain EntityId fields in unmanaged Components; built-in non-fragmenting relation index for reverse lookup; parent/child hierarchy is a built-in relation with optional cascade delete.
5. Structural changes: during a Tick only via per-System command buffers, merged at defined sync points in System order then record order (deterministic); immediate changes only outside a Tick.
6. Query API: source-generated typed queries as primary API (AOT-friendly) with declared read/write access feeding the scheduler; chunk-level iteration is the fast path, per-Entity ref access on top; cached by default, uncached ad-hoc available.
7. Variable-length data: inline fixed arrays for small bounded data, buffer Components for variable data, items as Entities. Buffer memory design deferred to the memory model ticket.
8. Component identity: stable registered name emitted by the source generator plus layout hash, not the CLR type; save/load and Plugin tickets build compatibility rules on it.
9. Resources: first-class World-owned singleton unmanaged structs in a keyed store, saved with the World, with declared read/write access like Components.

Open items passed on: buffer memory (memory model), command-buffer merge order proof (determinism), schema hash/migration rules (save/load).

**Amendment (CPU-only v1):** the 'index safe to hand to GPU' rationale for 32-bit indices no longer drives the design; the ID scheme is unchanged.
<!-- SECTION:FINAL_SUMMARY:END -->
