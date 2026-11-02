---
id: TASK-36
title: 'Guide and loopback sample: lockstep multiplayer on Colony'
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:30'
updated_date: '2026-11-02 02:35'
labels:
  - ready-for-agent
  - documentation
dependencies:
  - TASK-34
  - TASK-35
ordinal: 63000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Game authors need a worked path from the Framework's lockstep enablers to a running two-peer game. Write it last, once the test network, hash tree, join package, session state machine and permissions exist, so it documents the final API.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A guide in docs covers input delay, stalls, checkpoints, desync handling, join in progress and permissions, linking to the relevant APIs
- [x] #2 A two-process HelloColony sample runs two peers over loopback sockets (sockets live in the sample only), stays in sync for a long run, and demonstrates a join mid-run
- [x] #3 A CI test runs the loopback sample for a bounded number of Ticks and asserts matching hashes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 00abe9b. docs/guides/lockstep-networking.md; samples/HelloLockstep (TcpMesh sockets, PeerHost game loop, 3-process test with mid-run join) plus samples/HelloLockstep.Tests. Gaps: join margin is a fixed Tick count (a real Game needs a lobby protocol); tests use fixed sleeps and 60 s timeouts, may be slow on loaded CI; JIT only, no AOT.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Lockstep networking guide and a loopback TCP sample where three processes stay hash-identical with one joining mid-run via a join package.
<!-- SECTION:FINAL_SUMMARY:END -->
