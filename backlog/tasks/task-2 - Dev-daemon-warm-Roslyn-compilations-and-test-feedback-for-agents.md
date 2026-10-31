---
id: TASK-2
title: 'Dev daemon: warm Roslyn compilations and test feedback for agents'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:41'
updated_date: '2026-10-31 12:41'
labels:
  - wontfix
dependencies:
  - TASK-10
  - TASK-11
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Development-only tool (tools/, not shipped, never referenced by src/): a daemon holding warmed Roslyn compilations in memory, with a thin MCP front end, to speed unit-test and generator/analyzer feedback for implementing agents. Start only once the first packages, source generator and test suite exist: first measure the real build/test loop times, then decide what to cache (compilations, test host reuse, affected-test selection, diagnostics queries). Fresh implementation; no code reused from the user's employer. To be listed among the implementation tickets by the task 'Assemble the architecture spec'.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Decision 2026-10-31 (orchestrator, after TASK-10 and TASK-11 landed, per the ticket's 'measure first' rule). Measured Debug inner loop on the 16-core dev box with the Roslyn compiler server (VBCSCompiler) and MSBuild node reuse already warm: solution build after touching Colony.Core 1.8 s; no-op build 1.8 s; test project build after touching a test 1.0 s; dotnet test per project 1.5-6.2 s (Core 6.2 s); full solution test 48 s, dominated by Colony.Testing.Tests scenario/AOT runs. Compilation is already served by a persistent warm compiler, so a Roslyn daemon would save well under a second per iteration; the remaining cost is test execution, which agents already cut by running the touched project's tests during red/green and the full solution only before reporting. An MCP front end would add a tool to learn and maintain for no measurable gain. Decision: do not build. Revisit only if per-project test time exceeds ~30 s or the generator/analyzer feedback loop (Colony.Generated.Tests, 1.5 s) degrades.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Not built: measurements show the warm Roslyn compiler server already makes compilation ~1-2 s; test execution dominates and is handled by per-project test runs during development. Guidance recorded for implementing agents (run the touched project's tests in the loop, full solution before hand-off).
<!-- SECTION:FINAL_SUMMARY:END -->
