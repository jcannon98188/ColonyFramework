---
id: TASK-45
title: Padding-safe hashing outside Fields; stabilise allocation-accounting tests
status: To Do
assignee: []
created_date: '2026-11-02 05:12'
labels:
  - needs-triage
dependencies: []
ordinal: 72000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From TASK-44: (1) apply ValueLayout.DataMaskOf to Core Components w/o generated layout, padded Resources/events, ProcGen ChunkProvider.HashChunk/GenerationValidator, Diagnostics WorldDiff. (2) Flaky under parallel load on integration/v1: AllocationHarnessTests (2), HashTreeTests.Ticks_that_do_not_ask_for_the_tree_allocate_nothing.
<!-- SECTION:DESCRIPTION:END -->
