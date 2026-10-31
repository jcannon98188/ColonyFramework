---
id: TASK-2
title: 'Dev daemon: warm Roslyn compilations and test feedback for agents'
status: To Do
assignee: []
created_date: '2026-10-31 01:41'
updated_date: '2026-10-31 01:56'
labels:
  - needs-triage
dependencies:
  - TASK-10
  - TASK-11
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Development-only tool (tools/, not shipped, never referenced by src/): a daemon holding warmed Roslyn compilations in memory, with a thin MCP front end, to speed unit-test and generator/analyzer feedback for implementing agents. Start only once the first packages, source generator and test suite exist: first measure the real build/test loop times, then decide what to cache (compilations, test host reuse, affected-test selection, diagnostics queries). Fresh implementation; no code reused from the user's employer. To be listed among the implementation tickets by the task 'Assemble the architecture spec'.
<!-- SECTION:DESCRIPTION:END -->
