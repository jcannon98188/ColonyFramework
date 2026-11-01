---
id: TASK-33
title: Join-in-progress package and peer handshake check
status: To Do
assignee: []
created_date: '2026-11-01 21:28'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-15
  - TASK-16
  - TASK-14
ordinal: 60000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec 7.4 lists join-in-progress via save plus input log and build/Plugin identity for peer matching, but Game authors would assemble these by hand. A single verifiable package plus a handshake comparison removes the most error-prone part of lockstep multiplayer.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A join package bundles a save, the input log since that save, build and Plugin identity and verification hashes, in a chunkable format with a version
- [ ] #2 Receiving a package verifies chunk and whole-package hashes and the save's World hash before the new peer plays forward; corrupt or truncated packages fail with a clear reason
- [ ] #3 A peer that joins from a package and plays forward hashes identically to the host every Tick on 1, 2, 4 and N workers
- [ ] #4 A handshake call compares two peers' build, Plugin set and versions, schema hash, seed and options and returns specific mismatch reasons (for example missing Plugin X, different seed)
- [ ] #5 Tests cover mismatch reasons, corruption and join mid-run including pending Input Commands near the save point
<!-- AC:END -->
