---
id: TASK-40
title: Change-stamping for host writes and Unloaded-chunk handling in flow Fields
status: In Progress
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-01 22:13'
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
- [ ] #1 Writes made through the World API outside a System are change-stamped (or rejected with a clear error), so ChangeFilter-driven indexes update; documented and tested including the host-write case in the spatial and work index tests
- [ ] #2 Flow Field search stops at Unloaded chunks and reports a NeedsLoading style status naming the chunk, as the HPA path already does; tested with a streaming scenario
- [ ] #3 No World hash or golden changes for existing scenarios; zero-allocation gates still pass
- [ ] #4 A saved streamed World whose IChunkSource is supplied by the host can pass LoadOptions.VerifyWorldHash (host supplies the source before verification, or regenerable Unloaded chunks are excluded from the saved hash), found by TASK-27
<!-- AC:END -->
