---
id: TASK-40
title: Change-stamping for host writes and Unloaded-chunk handling in flow Fields
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-02 02:25'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-13
  - TASK-21
  - TASK-26
ordinal: 67000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Host writes through World.Get<TilePos> are not change-stamped, so the spatial index and work index silently go stale unless the host calls Rebuild or writes from a System with Writes access. Separately, flow Fields treat every chunk as resident, so a field can cross an Unloaded chunk. Both are silent-wrong-answer hazards found by the spatial, work index and flow Field tickets.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Writes made through the World API outside a System are change-stamped (or rejected with a clear error), so ChangeFilter-driven indexes update; documented and tested including the host-write case in the spatial and work index tests
- [x] #2 Flow Field search stops at Unloaded chunks and reports a NeedsLoading style status naming the chunk, as the HPA path already does; tested with a streaming scenario
- [x] #3 No World hash or golden changes for existing scenarios; zero-allocation gates still pass
- [x] #4 A saved streamed World whose IChunkSource is supplied by the host can pass LoadOptions.VerifyWorldHash (host supplies the source before verification, or regenerable Unloaded chunks are excluded from the saved hash), found by TASK-27
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 1e6518b. Host reads through World.Get/GetBuffer/query columns outside the engine now change-stamp (GetReadOnly/ReadColumn avoid it); flow Fields keep per-chunk residency, never enter Unloaded chunks, expose NeedsLoading/MissingChunk/FlowStepStatus.NeedsLoading; LoadOptions.PrepareWorld runs before VerifyWorldHash so streamed saves with host-supplied chunk sources verify. Gaps: JoinPackage does not expose PrepareWorld; zero-alloc gate flakes under whole-solution load -> TASK-42.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Host writes are change-stamped so indexes see them next Tick, flow Fields treat Unloaded chunks as walls and report NeedsLoading, and streamed worlds with host-supplied chunk sources pass hash verification on load.
<!-- SECTION:FINAL_SUMMARY:END -->
