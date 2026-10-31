---
id: TASK-3
title: 'Repo skeleton: solution, build props, analyzers, dependency check'
status: To Do
assignee: []
created_date: '2026-10-31 01:52'
updated_date: '2026-10-31 02:08'
labels:
  - ready-for-agent
  - implementation
dependencies: []
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 30000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
First implementation ticket of the v1 architecture spec (Backlog doc-1 and doc-2, section 15 item 1). Nothing compiles yet; every later ticket needs a solution it can add a package and a test project to without renegotiating build settings. The spec requires .NET 11 only, lockstep SemVer 0.x versions, IsAotCompatible and IsTrimmable with warnings as errors, no runtime reflection, and strict package layering enforced mechanically rather than by review. The repo has no remote yet, so the dependency check must run locally; license, hosting and CI service are deliberately deferred.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One solution builds with the pinned .NET 11 SDK via global.json, with central package management and a shared Directory.Build.props carrying analyzers, AOT/trim flags and warnings-as-errors
- [ ] #2 Folder layout matches the spec: src, tests, benchmarks, samples, docs, prototypes (not built by the solution), tools (never referenced by src)
- [ ] #3 Colony.Core and Colony.Core.Tests exist; the test project runs one passing test on Microsoft.Testing.Platform
- [ ] #4 A locally runnable dependency check fails the build when a package references another outside the spec section 3 rules (Core depends only on the BCL; nothing depends on Diagnostics, Testing or Plugins)
- [ ] #5 A Colony.Generators scaffold (netstandard2.0) is referenced by Colony.Core.Tests and produces no diagnostics
- [ ] #6 A GitHub Actions workflow runs build, tests and the dependency check on pull requests and pushes to main, on a Windows x64 and Linux x64 JIT matrix
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
CI service decided after the repo got its remote: GitHub Actions (spec sections 10.3 and 14). Nightly AOT/ISA/ARM64/soak jobs and the self-hosted benchmark runner are later tickets' concern; this ticket only needs the PR matrix.
<!-- SECTION:NOTES:END -->
