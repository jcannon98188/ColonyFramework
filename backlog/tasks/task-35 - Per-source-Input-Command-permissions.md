---
id: TASK-35
title: Per-source Input Command permissions
status: In Progress
assignee:
  - '@jason'
created_date: '2026-11-01 21:30'
updated_date: '2026-11-01 21:31'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-15
  - TASK-16
ordinal: 62000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Remote players and debug tools are separate Input Command sources, but nothing limits which commands a source may issue. Deterministic permission checks reduce cheating and bad inputs in multiplayer and also gate debug commands in single-player. Needs a short design pass on how permissions are declared (Plugin registration versus World options) before implementing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A source can be granted or denied specific Input Command types, declared at build time through the builder or Plugin registration
- [ ] #2 A denied command is rejected deterministically at the canonical ordering step, recorded as CommandRejected with a reason, and still appears in the replay log so replays and peers agree
- [ ] #3 Permissions are part of the schema hash and the save manifest, so peers with different permissions fail the handshake with a clear reason
- [ ] #4 Tests cover grant, deny, default policy, replay equivalence and identical hashes on 1, 2, 4 and N workers; debug commands from the inspection server use the same mechanism
<!-- AC:END -->
