---
id: TASK-15
title: Input Commands and the replay log
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 13:49'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-14
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 42000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec sections 7.2 and 7.3: Input Commands are typed schema-registered unmanaged structs submitted from any thread and stamped with an effective Tick; at each Tick boundary a canonical list sorted by (effective Tick, source ID, per-source sequence) is built and handler Systems apply it in the Input Phase through command buffers. Rejection is deterministic and emits CommandRejected; rejected commands stay in the log. The replay log reuses the save container: header, per-Tick canonical input lists, periodic checkpoint hashes, optional full-save checkpoints, with record, playback with hash verification, seek via checkpoints and fork to live input. This is also the wire form Game authors will use for lockstep networking (section 7.4), so it must be stable and serialisable.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Commands submitted concurrently from several threads yield the same canonical list and the same World hash as sequential submission
- [ ] #2 A recorded replay played back reproduces every checkpoint hash; a deliberately altered input fails verification at the first diverging checkpoint
- [ ] #3 Seeking to a checkpoint and forking to live input continues deterministically
- [ ] #4 A rejected command emits CommandRejected with source, sequence and reason and remains in the log; handlers draw RNG keyed by (source, sequence, use index)
- [ ] #5 Replay equivalence and a simulated two-peer lockstep scenario are added to the determinism harness
<!-- AC:END -->
