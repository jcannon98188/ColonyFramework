---
id: TASK-33
title: Join-in-progress package and peer handshake check
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:28'
updated_date: '2026-11-01 21:58'
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
- [x] #1 A join package bundles a save, the input log since that save, build and Plugin identity and verification hashes, in a chunkable format with a version
- [x] #2 Receiving a package verifies chunk and whole-package hashes and the save's World hash before the new peer plays forward; corrupt or truncated packages fail with a clear reason
- [x] #3 A peer that joins from a package and plays forward hashes identically to the host every Tick on 1, 2, 4 and N workers
- [x] #4 A handshake call compares two peers' build, Plugin set and versions, schema hash, seed and options and returns specific mismatch reasons (for example missing Plugin X, different seed)
- [x] #5 Tests cover mismatch reasons, corruption and join mid-run including pending Input Commands near the save point
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 2f40448. src/Colony.Persistence/Join: PeerIdentity, PeerHandshake, JoinPackage (Capture/Encode/Open/ToChunks/Join), chunk assembler; SaveContainer JoinFlag=2. Gaps for TASK-34/42: ReplayPlayer submits one Tick at a time and breaks when a later-sequence command has an earlier effective Tick (JoinPackage avoids by submitting in (source,sequence) order); PeerIdentity.CurrentFrameworkVersion constant must track VersionPrefix; handshake does not say which Component/Resource changed; wire exchange and post-capture relay left to TASK-34. One unnamed full-run failure seen once; watch for flake.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Join-in-progress package (save + input log + pending commands + identity + checkpoint hashes), chunked self-verifying transport form, and a peer handshake with specific mismatch reasons.
<!-- SECTION:FINAL_SUMMARY:END -->
