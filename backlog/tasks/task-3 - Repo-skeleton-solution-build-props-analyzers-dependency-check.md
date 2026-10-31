---
id: TASK-3
title: 'Repo skeleton: solution, build props, analyzers, dependency check'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:52'
updated_date: '2026-10-31 02:34'
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
- [x] #1 One solution builds with the pinned .NET 11 SDK via global.json, with central package management and a shared Directory.Build.props carrying analyzers, AOT/trim flags and warnings-as-errors
- [x] #2 Folder layout matches the spec: src, tests, benchmarks, samples, docs, prototypes (not built by the solution), tools (never referenced by src)
- [x] #3 Colony.Core and Colony.Core.Tests exist; the test project runs one passing test on Microsoft.Testing.Platform
- [x] #4 A locally runnable dependency check fails the build when a package references another outside the spec section 3 rules (Core depends only on the BCL; nothing depends on Diagnostics, Testing or Plugins)
- [x] #5 A Colony.Generators scaffold (netstandard2.0) is referenced by Colony.Core.Tests and produces no diagnostics
- [x] #6 A GitHub Actions workflow runs build, tests and the dependency check on pull requests and pushes to main, on a Windows x64 and Linux x64 JIT matrix
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
CI service decided after the repo got its remote: GitHub Actions (spec sections 10.3 and 14). Nightly AOT/ISA/ARM64/soak jobs and the self-hosted benchmark runner are later tickets' concern; this ticket only needs the PR matrix.

Test framework: xUnit v3 4.0.2 on Microsoft.Testing.Platform (global.json test runner = MTP). Solution format .slnx; UseArtifactsOutput (artifacts/). Test-project plumbing lives in root Directory.Build.targets keyed on IsTestProject so new test csprojs need only ProjectReferences. RootNamespace for src packages is Colony. Dependency rules are a code table in tools/Colony.DependencyCheck/DependencyRules.cs (allow lists per package, global ban on referencing Diagnostics/Testing/Plugins, src only references src, nothing references prototypes, Core has no runtime PackageReference). Generator targets Roslyn 4.14.0 so older IDEs still load it. Gaps handed forward: spec section 3 is silent on what Abstractions, Diagnostics, Testing, Native and the meta-package may reference (clarified by the orchestrator in the spec after this ticket); the CI workflow is unverified on real runners until the first PR.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Repo skeleton merged to integration/v1 (af5b60f). Colony.slnx with Colony.Core, Colony.Generators (netstandard2.0 incremental generator scaffold consumed as an analyzer by Colony.Core.Tests), Colony.Core.Tests, and tools/Colony.DependencyCheck with its own 20 tests; global.json pins SDK 11.0.100-rc.2; Directory.Build.props/Packages.props carry analyzers, AOT/trim flags and warnings-as-errors; .github/workflows/ci.yml runs restore/build/dependency-check/test on windows-latest and ubuntu-latest. Verified: dotnet build 0 warnings 0 errors, dotnet test 21/21 passed, dependency check exits 0 on the repo and 1 on an injected Core->Generators reference.
<!-- SECTION:FINAL_SUMMARY:END -->
