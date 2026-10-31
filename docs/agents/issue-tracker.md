# Issue tracker: Backlog.md

Issues and specs for this repo live in [Backlog.md](https://github.com/MrLesk/Backlog.md), managed through the `backlog` CLI. There is no remote tracker.

## Conventions

- Tasks are markdown files under `backlog/`. **Never edit them directly**; always go through the `backlog` CLI so metadata and history stay consistent.
- Before task lifecycle work, read the matching guide: `backlog instructions overview`, `task-creation`, `task-execution`, `task-finalization`.
- Use `backlog <command> --help` before running an unfamiliar command.
- Triage roles are recorded as **labels** on the task (see `triage-labels.md`).
- Configured statuses: `Draft`, `To Do`, `In Progress`, `Done`.
- A feature spec is a task of `--type feature` (or a doc via `backlog doc create`); its implementation tickets are subtasks created with `-p <spec-task-id>`.

## When a skill says "publish to the issue tracker"

Run `backlog task create "<title>" -d "<description>" -l <labels> --ac "<criterion>"`. For tickets belonging to a spec, add `-p <spec-task-id>`; for ordering, add `--depends-on <ids>`. Use `--draft` for work not yet ready to triage.

## When a skill says "fetch the relevant ticket"

Run `backlog task view <taskId> --plain` (e.g. `TASK-12`). The user will normally pass the ID directly. Use `backlog task list --plain` to browse and `backlog search` to find.

## Triage operations

- Apply or change a label: `backlog task edit <taskId> -l <label>` (check `backlog task edit --help` for add/remove flags).
- Add a comment or conversation note: append to the task's implementation notes via `backlog task edit <taskId> --notes "..."` (see `--help` for append behaviour).
- Close as won't-fix: label `wontfix`, then set status `Done` with a final summary explaining why.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a parent task with one **child** subtask per ticket.

- **Map**: a task titled `Map: <effort>` holding the Notes / Decisions-so-far / Fog body in its description.
- **Child ticket**: subtask created with `-p <map-id>`, question in the description, and the type (`research`/`prototype`/`grilling`/`task`) as a label.
- **Blocking**: `--depends-on <ids>`. A ticket is unblocked when every dependency is `Done`.
- **Frontier**: subtasks of the map that are `To Do`, unblocked, and unassigned; lowest ID wins.
- **Claim**: set status `In Progress` before any work.
- **Resolve**: record the answer in the task's final summary, set status `Done`, then append a context pointer (gist + task ID) to the map's Decisions-so-far.
