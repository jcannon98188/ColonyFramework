
<!-- BACKLOG.MD GUIDELINES START -->
<!-- backlog.md-instructions-version: 1.53.0 -->
<CRITICAL_INSTRUCTION>

## Backlog.md Workflow

This project uses Backlog.md for task and project management.

**At the beginning of each conversation in this project, run `backlog instructions overview` before answering or taking action. Re-read it only if you have not read it yet in the current conversation.**

Use the overview to decide whether to search, read, create, or update Backlog tasks.

Before task lifecycle actions, read the matching detailed guide:
- `backlog instructions task-creation` before creating or splitting tasks
- `backlog instructions task-execution` before planning, changing status or assignee, adding a plan or implementation notes, or implementing task work
- `backlog instructions task-finalization` before checking acceptance criteria, writing final summaries, or moving tasks to terminal statuses

Use `backlog <command> --help` before running unfamiliar commands. Help shows options, fields, and examples.

Do not edit Backlog task, draft, document, decision, or milestone markdown files directly. Use the `backlog` CLI so metadata, relationships, and history stay consistent.

</CRITICAL_INSTRUCTION>
<!-- BACKLOG.MD GUIDELINES END -->

## Agent skills

### Issue tracker

Issues are tracked locally with Backlog.md via the `backlog` CLI (no remote tracker). See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-label vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`), applied as Backlog.md task labels. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.

## Build and test loop

.NET SDK pinned by `global.json`; solution `Colony.slnx`; xUnit v3 on Microsoft.Testing.Platform. The warm Roslyn compiler server already makes builds take 1-2 s, so there is no dev daemon (TASK-2, decided against). While iterating, run only the test project you touch: `dotnet test tests/<Project> -c Debug` (1-6 s). Before handing work off, run `dotnet test Colony.slnx -c Release` (about 50 s) and `dotnet run --project tools/Colony.DependencyCheck`; both must be green. The Native AOT comparison test skips unless `tests/Colony.Testing.AotHost` was published (`dotnet publish tests/Colony.Testing.AotHost -c Release -r win-x64`).
