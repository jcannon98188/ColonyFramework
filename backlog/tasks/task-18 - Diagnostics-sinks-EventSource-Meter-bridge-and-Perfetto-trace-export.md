---
id: TASK-18
title: 'Diagnostics sinks: EventSource/Meter bridge and Perfetto trace export'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-17
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 45000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 10.1: the diagnostics records are only useful once something can read them from outside the process. Two opt-in sinks ship in Colony.Diagnostics on top of the read API: an EventSource/Meter bridge for dotnet-counters and OpenTelemetry, and a Chrome-trace/Perfetto JSON export for timeline inspection. Sinks are host-side and must never affect simulation state or Tick allocation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 With the Meter bridge enabled, dotnet-counters shows per-Tick and per-System counters from a running sample host
- [ ] #2 A Perfetto trace exported from a captured run opens in the Perfetto UI showing Phases, Systems and worker lanes
- [ ] #3 Enabling a sink changes no World hash and introduces no steady-state allocation on Framework threads
- [ ] #4 Sinks consume the versioned record layout and fail clearly on a version mismatch
<!-- AC:END -->
