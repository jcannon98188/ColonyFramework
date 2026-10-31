---
id: TASK-9
title: 'Relations, hierarchy, buffer Components, string table and blob store'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 13:01'
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
- [x] #1 A Relation can be declared between Entity types, reverse lookup returns targets in deterministic order, and removing an Entity updates the index
- [x] #2 Parent/child hierarchy with cascade delete removes descendants deterministically and emits no managed allocation
- [x] #3 Buffer Components store inline up to a declared capacity and overflow to the World allocator; overflow counts are observable for diagnostics
- [x] #4 StringId interning yields identical IDs for identical insertion sequences and BlobRef<T> blobs are immutable once stored
- [x] #5 Rebuildable index state (relation index) is excluded from the World hash while the saved data (string table, blobs, Relations) is included
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged into integration/v1 at 06138b7 (2026-10-31). Full solution Release green twice (619 total, 1 skipped AOT). APIs: builder.AddRelation(name) -> RelationType, buffer.Relate/Unrelate, world.TargetOf/SourcesOf (sources in ascending Entity slot order regardless of insertion order), RebuildRelationIndex; AddHierarchy(cascade), SetParent/ClearParent, ParentOf/ChildrenOf, cascade delete removes descendants only, cycles destroyed once, zero-alloc on a 2000-node tree; AddBufferComponent<T, InlineN<T>> (Inline4/8/16/32 shipped; any unmanaged struct whose size is a multiple of the element size works), overflow to World-owned BufferHeap (size classes 16 B..64 KiB from SlabAllocator chunks, 32-bit handles, one lock), world.BufferCounters (Overflows/Grows/Frees/BlocksInUse/BytesInUse); string table and blob store with insertion-order-deterministic StringId/BlobRef, blobs copied and read-only, serial-only interning (chunk jobs not supported, documented). Relation index is maintained inline in the serial structural ops at sync points (no Late System, no per-Entity removal feed; spatial index untouched). Dead relation target -> StructuralFailure(Relate, Stale). Hash: relation index excluded (verified via internal clear seam); Relations, strings, blobs included; buffer hash is logical (length + elements) so heap handles never affect it. World hash layout changed (string table + blob store sections after Resources, before extensions): goldens regenerated, FieldHashTests updated. SlabAllocator.Rent/Return now locked. Follow-ups: zero-alloc gates flake under the parallel whole-solution run (AllocationTests.A_100k_entity... 2/12 Release runs with a constant 3384 bytes; TickRecordTests.Recording_and_reading... 1/3 Debug runs), pass in isolation - put zero-alloc gates in a serial collection or investigate; concurrent intern path can come with Definitions (TASK-16); Frees counts grow-frees too.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Relations with a built-in rebuildable reverse index, parent/child hierarchy with deterministic zero-alloc cascade delete, buffer Components with inline storage and World-owned size-class overflow (observable counters), interned string table and immutable blob store, all hash-covered correctly (index excluded, saved data included) and thread-count invariant. Merged at 06138b7.
<!-- SECTION:FINAL_SUMMARY:END -->
