---
id: TASK-36
title: 'Guide and loopback sample: lockstep multiplayer on Colony'
status: To Do
assignee: []
created_date: '2026-11-01 21:30'
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
- [ ] #1 A guide in docs covers input delay, stalls, checkpoints, desync handling, join in progress and permissions, linking to the relevant APIs
- [ ] #2 A two-process HelloColony sample runs two peers over loopback sockets (sockets live in the sample only), stays in sync for a long run, and demonstrates a join mid-run
- [ ] #3 A CI test runs the loopback sample for a bounded number of Ticks and asserts matching hashes
<!-- AC:END -->
