---
id: TASK-9
title: 'Relations, hierarchy, buffer Components, string table and blob store'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
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
ordinal: 36000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec sections 4.1 and 4.2: references are plain EntityId fields with a built-in non-fragmenting relation index for reverse lookup, parent/child is a built-in Relation with optional cascade delete, and Claim will later be a built-in Relation. Variable data is handled by inline fixed arrays, buffer Components that overflow to a World-owned size-class allocator, and items as Entities. An interned string table (32-bit StringId, deterministic order) and an immutable blob store (BlobRef<T>) are saved with the World and are needed by Definitions and persistence.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A Relation can be declared between Entity types, reverse lookup returns targets in deterministic order, and removing an Entity updates the index
- [ ] #2 Parent/child hierarchy with cascade delete removes descendants deterministically and emits no managed allocation
- [ ] #3 Buffer Components store inline up to a declared capacity and overflow to the World allocator; overflow counts are observable for diagnostics
- [ ] #4 StringId interning yields identical IDs for identical insertion sequences and BlobRef<T> blobs are immutable once stored
- [ ] #5 Rebuildable index state (relation index) is excluded from the World hash while the saved data (string table, blobs, Relations) is included
<!-- AC:END -->
