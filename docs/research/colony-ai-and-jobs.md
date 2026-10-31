# Colony AI and jobs: decision-making, assignment and claiming at scale

Research question: how do Dwarf Fortress, RimWorld, Oxygen Not Included, Factorio-style bots, The Sims-like games and academic/industry sources do job and task assignment, needs, priorities and colonist decision-making at scale? Which approaches suit 100k+ agents in a data-oriented/ECS setting?

Researched 2026-10-30. Terms follow `GLOSSARY.md` (World, Entity, Component, Resource, Relation, System, Field, Tick, Plugin, Definition, Game author). Builds on `colony-sim-prior-art.md` (section 2 listed job internals as a gap) and `prototype-ecs-bench.md` (surprise 7: command buffers cannot claim an Entity).

## Source quality and gaps

Labels: **[P]** primary (developer's own talk, chapter, blog or paper); **[D]** third-party decompile of shipped code (primary evidence of behaviour, but version-pinned and not endorsed by the developer); **[W]** community wiki; **[S]** secondary; **[I]** my inference, not sourced.

What was actually read in full: the RimWorld 1.0 decompile (`JobGiver_Work`, `ReservationManager`, `ThinkNode_PrioritySorter`, `Pawn_JobTracker`), Bay 12 dev log 2014 (first 100k of 172k characters), Factorio FFF-374 and FFF-421, Orkin's GDC 2006 paper, Forbus and Wright's Sims object paper, Game AI Pro chapters (Behavior Selection Overview, Utility Theory intro, Utility Considerations, HTN example, 1000 NPCs at 60 FPS), Gerkey and Matarić's task-allocation taxonomy.

Gaps, stated honestly:

- **Oxygen Not Included:** no developer talk or readable source was found. Everything is from the community wiki (priority behaviour) and a stack trace in a Klei bug report. ONI internals (the chore system) are **GAP**. Confidence low.
- **Dwarf Fortress internals:** the source is closed. Only Tarn Adams' 2014 dev log (job postings) and interviews are primary. The 2014 log is about the pre-Steam version. Whether v50 kept the "job posting" design is **UNVERIFIED**. The DF wiki Job page returned HTTP 429 again.
- **RimWorld decompile is version 1.0 (2018).** Current RimWorld (1.5/1.6) may differ. The current code was not read. Tynan Sylvester has no primary write-up of the job system that I could find.
- **Sims 3/4 and Halo 2/Killzone:** I did not read the GDC Vault talks (paywalled/login). Claims about them are second-hand via Game AI Pro chapters and Orkin's paper.
- **Unreal Mass / StateTree / Smart Objects:** the Epic pages fetched were landing pages without detail. Only the existence of Find/Claim/Use tasks is established (via Epic's API listing seen in search results, not read in full). Confidence low.
- **Unity DOTS / Overwatch ECS AI:** not covered.
- **No source was found giving an AI cost per agent in microseconds for any colony sim.** The only measured number I have is this repo's own prototype.

## 1. Executive summary

1. **Every shipped colony sim I could verify uses a pull model.** Idle agents scan for work; nothing assigns work to agents globally. RimWorld pulls per pawn (closest valid target per work type, in priority order). DF (2014) moved to "job postings" that dwarves apply to. ONI is priority-class then sub-priority then proximity or hidden tiebreak [W]. The global-assignment alternative (Hungarian, auctions) is well understood academically but I found no colony sim that ships it.
2. **Priority is a two- or three-level lexicographic table, not a utility score.** RimWorld: think-node priority, then per-pawn work-type order, then work-giver order within a type, then closest target. ONI: priority class (1-9), then sub-priority, then hidden tiebreak or proximity. DF 2014: numerically prioritised designations with a "do now" bypass. Players can read and edit a table. Utility scoring is used for needs and leisure (The Sims), not for work.
3. **Claiming is a separate explicit mechanism.** RimWorld has a `ReservationManager` that records (claimant, job, target, layer, maxPawns, stackCount). Reserving happens when a job starts, not when it is chosen. A failed reserve aborts the job. Several pawns can pick the same target in the same scan and only the first to start wins. DF logs show "install colony jobs respect each other's targets" style fixes. So every verified game has an optimistic-pick, pessimistic-claim split. Factorio bots are the exception: the allocator is global and single-threaded, so there is no race.
4. **The scaling killers in practice are scan cost, unreachable targets and thrash, not decision logic.** RimWorld's 1.0 `CanReserve` is a linear scan of one list of all reservations. Its work scan is O(work givers x candidate things) per idle pawn. DF devlogs repeatedly fix "jobs that check too many items". Factorio's old global idle-robot list was replaced by per-chunk lists and spiral search because it did not scale to thousands of robots.
5. **Time budgeting is by cadence, not by per-frame deadline.** RimWorld pawns run their constant think tree only every 30 Ticks and pick a new job only when idle. Factorio forces robot updates only every 20 Ticks moving or 60 Ticks standing. The Sims Medieval and Project Highrise decide only when the action queue is empty. None of the sources I read used a hard per-frame AI time slice, except the LOD Trader (Game AI Pro) which allocates detail under a CPU budget.
6. **Thrash is handled by commitment, not by smarter scoring.** Utility literature offers inertia weights, cooldowns and "decide only when the queue is empty". RimWorld adds a hard detector: more than 10 jobs started in 10 Ticks forces an error-recovery Wait job.
7. **Planners (GOAP/HTN) are for few, tactically complex agents.** Orkin's F.E.A.R. and Killzone-style systems serve combat. Project Highrise tried a planner for 1000 NPCs and dropped it, but for design reasons (authorial control), not speed. Colony sims in the sample use priority trees with one-step "give me a job" lookups followed by a fixed sequence of steps (RimWorld Toils), which is closer to a scripted plan than to search.
8. **For 100k+ agents in an ECS the shape that fits is: indexed work (push), batched claim resolution, staggered decisions.** This is my inference [I], in section 7. The prototype already measured a naive nearest-food scan at 0.60 ms/Tick for ~2% hungry agents at 100k; scan structure, not ECS overhead, will decide this.

## 2. Per-game findings

### 2.1 RimWorld

**Decision architecture [D].** Each pawn has a think tree (XML-defined, a tree of `ThinkNode`s). A `ThinkNode_PrioritySorter` evaluates each child's `GetPriority(pawn)`, tries the highest first, and moves on if it yields no job. Children are shuffled first so ties break randomly. `JobGiver_Work` returns a priority that depends on the pawn's current timetable slot (Work 9, Anything 5.5, Sleep 3, Joy 2), so work competes with leisure and sleep through the same priority-sorter. (Source: josh-m/RW-Decompile, `Verse.AI/ThinkNode_PrioritySorter.cs`, `RimWorld/JobGiver_Work.cs`, game version 1.0.)

**Work selection [D].** `JobGiver_Work.TryIssueJobPackage` walks the pawn's work givers in the pawn's own order (the player-set priority table, flattened). For each giver it: (a) tries `NonScanJob`, else (b) if the giver is a "scanner", asks for the closest valid thing: `ClosestThingReachable` with a `HasJobOnThing` validator, over either a candidate list the giver supplies (`PotentialWorkThingsGlobal`) or the map's `listerThings` index by `ThingRequest`. It stops at the first priority tier where a target was found, so lower tiers are not scanned. Cell-based work (e.g. sowing, mining designations) loops over all candidate cells and keeps the closest or highest-scored. Reachability is checked with `CanReach` inside the loop. (Same sources.)

**Cost implications [D, I].** Each idle pawn runs this loop. Cost is roughly O(giver tiers x candidates) per idle pawn, with a reachability check per candidate unless the region search bails first. Scanner subclasses can bound the search (`LocalRegionsToScanFirst` is a hook to search nearby regions before going global). The 2013 "Reachability at last" post (see `colony-sim-prior-art.md` section 1) is the origin of the region-based fast reject for unreachable targets.

**Priority table [W, D].** The player edits a pawn x work-type grid (1-4, or disabled). In the decompile this becomes `WorkGiversInOrderNormal` and `...Emergency` lists on the pawn. An early forum post says priorities used to be numeric per type, which supports a "table, lexicographic" lineage [S].

**Reservation [D].** `ReservationManager` (one per map) holds a flat `List<Reservation>`. A reservation is (claimant pawn, job, target, layer, maxPawns, stackCount). `CanReserve` is a linear pass over the list: it returns true if the claimant already holds it, otherwise counts other claimants on the same target and layer and compares against `maxPawns` and remaining stack. `Reserve` re-checks and appends. Player-forced jobs may override other pawns' reservations. `Release*` runs on job end. On load, reservations with a destroyed target or claimant are deleted with an error. (Source: `Verse.AI/ReservationManager.cs`.)

Notes on what this means:

- Reservations are by **target and layer**, with a capacity (`maxPawns`) and a partial-stack amount. This is richer than a boolean claim. It allows "3 pawns can sleep in this bunk bed" and "two pawns each carry half this stack".
- A reservation is bound to a **job**, so job end releases it. This is the lifetime rule that prevents leaked claims.
- The list scan is O(reservations) per check. A Rim73 performance mod changelog says it rescaled the manager so that it gets faster with more reservations [S: Steam changelog seen via search; not read in full], which suggests this was a measured hotspot. Treat as hearsay.

**Job lifecycle and preemption [D, P-ish].** A job is run by a `JobDriver` that yields `Toil`s (walk, work, carry...). `TryMakePreToilReservations` is where the driver reserves targets, and failure aborts before the job begins. Toils have failure conditions (e.g. cannot reach, target destroyed). The RimWorld modding wiki describes this lifecycle [W, https://rimworldwiki.com/wiki/Modding_Tutorials/Code_MendingJob]; the decompile confirms the call order in `Pawn_JobTracker.StartJob`.

`Pawn_JobTracker.JobTrackerTick` [D]:
- Every 30 Ticks (hashed per pawn, so spread across the Tick range) the pawn re-runs the **constant** think tree, a small tree for urgent reactions. If it yields a job that is not a continuation of the current one, the pawn **interrupts** with `InterruptForced`.
- Otherwise the pawn only runs the main think tree when it has no job (`curJob == null`) or a job ends, or when a job with `expiryInterval` expires (used for periodic re-evaluation).
- Jobs carry a `playerInterruptible` flag. Player-forced jobs bypass reservations held by others.
- **Thrash guard:** it counts jobs started this Tick, and if more than 10 are started in one Tick, or 10 in the last 10 Ticks, it logs and starts an error-recovery job (a 250-Tick Wait). `JobCondition.Errored` also starts a Wait. This is evidence the developers saw job ping-pong in practice and added a hard circuit breaker.

**Staggering [D].** `IsHashIntervalTick(30)` spreads per-pawn work by pawn ID hash. The map also has `TickRare` (every 250 Ticks) used for slow things; I did not read that code, so its exact use for AI is **UNVERIFIED**.

**Gaps:** version drift since 1.0; no performance numbers; whether current RimWorld caches work-giver results is unknown.

### 2.2 Dwarf Fortress

**What primary sources say:**

- Tarn Adams' 2014 dev log (Bay 12, https://www.bay12games.com/dwarves/dev_2014.html, December entries) [P]. He describes moving away from "jobs choosing dwarves independently" to invisible **job postings** that dwarves look through and apply to (12/03, 12/13 for medical jobs). He added priorities (7 levels planned, 12/06), the ability for important jobs to pull a working dwarf off a lesser one (12/14, 12/18), an "immediately" priority for do-it-now jobs (12/18) and numeric priorities for designations (12/18). He notes that hauling had been flooding dwarves with "irrelevant hauling jobs" (12/13) and added item "swiping" between jobs: workshop and building jobs can take items that are queued for hauling (12/16, 12/18).
- Same log, performance and stability items [P]: fixes for "jobs that check too many items" (07/27), automation jobs no longer using every workshop at once (07/30), reducing the number of automation jobs created (08/03), job cancel spam (07/20), stuck jobs when paths were interrupted (08/03), "install colony" jobs respecting each other's targets (08/10: a claim fix), and lag culprits including stale activities and enemy detection (09/15, 09/17).
- Stack Overflow Blog interview [P, https://stackoverflow.blog/2021/12/31/700000-lines-of-code-20-years-and-one-developer-how-dwarf-fortress-is-built/]. Pathing uses region numbers to skip hopeless A* searches; the game is single-threaded apart from graphics; Adams says it is at the edge of what it can support. The article does not discuss job assignment.

**What the wiki adds [W, seen via search summaries in the prior-art doc; the Job page was rate-limited]:** labors are permissions, not jobs; hauling queue size scales with the number of dwarves holding haul labors; dwarves cancel jobs with specific reasons; community tools (DFHack autolabor) exist because manual labor management is tedious at scale [DFHack docs not read in full due to a redirect]. Players report FPS death at around 100-200 population [S: Steam threads]; the cause is disputed, and a bug-tracker comment says population alone does not explain it. Pathing and "everything" are blamed. **UNVERIFIED.**

**Takeaways:** the same architecture as RimWorld (permission table, priorities, pull) with two additions worth noting: (1) postings decouple "work exists" from "who does it", which is what lets one System index work, and (2) item "swiping" shows contested resources between job types are a recurring design problem.

### 2.3 Oxygen Not Included (low confidence, community sources only)

- Priority classes 1-9 per errand category per Duplicant, a "disabled" state, a default of 5; within a priority a **sub-priority** (1-9); then a hidden tiebreaker, or **proximity** when the player enables it. The wiki's own advice is that hidden priorities suit small colonies and proximity suits large ones [W, https://oxygennotincluded.wiki.gg/wiki/Priority].
- Basic needs (toilet, food, sleep) pre-empt priorities except in Red Alert [W, same page].
- Klei forum threads complain that priorities "don't always behave predictably" [S: search summary]. One consequence: a lexicographic table gives deterministic order but players over-set priorities, so tiebreak behaviour matters.
- A Klei bug-tracker stack trace shows a path `Brain.UpdateChores` to `ChoreDriver.SetChore` to `Chore.Begin` [S, snippet only]. This suggests the same pull structure (each brain updates its own chore), but I could not confirm the internals.

**GAP:** no verified source for how errands are indexed, how many Duplicants a scan covers, or what the cost is. Do not rely on this section for design.

### 2.4 Factorio logistic robots (the "global allocator" counter-example)

- Before 2.0, one global list of idle robots; each task took the nearest idle robot and removed it from the list. 500 items meant 500 separate tasks, often assigned to distant robots [P, FFF-374, https://direct.factorio.com/blog/post/fff-374].
- FFF-374 replaced this with robot **task queues** and **earliest-arrival** selection: for each candidate robot, estimate arrival time (idle: distance over speed; busy: projected end-of-queue position plus flight). Each chunk stores the busy robots whose queues will finish there, and the search spirals outward from the task's chunk. The post says thousands of busy robots run well but gives no numbers [P].
- FFF-421 (Optimizations 2.0) [P, https://direct.factorio.com/blog/post/fff-421]: robots stored a move intention and the renderer interpolates, while the logical update runs rarely (forced after 20 Ticks moving or 60 Ticks standing still). About 10,000 stationed robots in a test save. Overall gain about 15% on that save, 10-25% on robot-heavy saves. Idle roboports are switched off (about 1 ms to 0.025 ms/Tick in the test save).
- A Factorio staff post (forum, 2.0.60 bug report) says the logistic robot "to-do list" was disabled because overhead scaled poorly with network size [S: seen via search summary only; **UNVERIFIED**].

Lessons: (a) a global allocator is fine because robots are interchangeable; colonists are not (skills, permissions, needs). (b) The spatially bucketed "who can serve this" index and spiral search is the pattern that scales. (c) Do not update agents whose state is "moving along a straight intent" or "idle" every Tick. Event-driven wake-up or very sparse updates beat per-Tick polling.

### 2.5 The Sims and similar

- **Smart objects with advertisements [P].** Forbus and Wright's paper (2001, https://qrg.northwestern.edu/papers/Files/Programming_Objects_in_The_Sims.pdf): behaviour lives in objects, each with a set of behaviours, a "possible?" check and **advertisements** stating which needs the behaviour satisfies. A Sim picks, from all behaviours of all objects, the one that maximizes its current happiness. Ad strength **attenuates with distance** and is scaled per Sim by personality. Behaviours can be queued ("allow consecutive"). The paper's own caveat: Sims cannot learn that an advertised benefit did not materialize ("skeptical Sims" listed as an extension idea) and a too-attractive object can trap Sims.
- **Decide when the queue is empty [P].** David Graham's utility chapter (Game AI Pro, Ch. 9) notes that in The Sims Medieval a Sim decides only when its interaction queue is empty, then commits until the action succeeds or fails [P, http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter09_An_Introduction_to_Utility_Theory.pdf]. This is the Sims' answer to thrash.
- **Not read:** Richard Evans' Sims 3 talk (GDC Vault 1012294, login wall). Personality as conditionals (philarchive EVARPT) not read. Confidence in Sims 3 specifics: low.
- **Scale caveat:** Sims games run roughly 1-8 simulated agents in full detail. This is the opposite regime to a colony of thousands. See Project Highrise below.

### 2.6 Project Highrise (1000 NPCs at 60 FPS)

Robert Zubek, Game AI Pro 3 Ch. 34 [P, http://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter34_1000_NPCs_at_60_FPS.pdf]. The most directly relevant industry source on mass agents:

- Tried a **propositional planner** (preconditions and effects as bit vectors, plan caching). It was fast enough and only a handful of distinct plans were ever created because NPC types lived stereotyped lives. The author dropped it anyway because he wanted to author whole daily routines and keep designer control, i.e. the wrong level of abstraction.
- Replaced it with **daily scripts**: time blocks plus probabilistic one-shots, expanded into short action sequences.
- **Open-loop execution:** actions go in a queue and run in order. Each action may check its own failure conditions. On failure the queue is flushed and a fallback script queued. The system evaluates the world only when the queue is empty. Because selection is cheap, "just start over" replaces careful failure handling.
- Removed hunger and tiredness from NPCs: hidden state made it hard for designer and player to understand why populations behaved as they did. Quote of the lesson: the usefulness of detailed NPC representation is inversely proportional to how many NPCs the player manages (paraphrase).
- Pathfinding was moved to a domain-specific hierarchy (floor plates joined by connectors) rather than generic HPA*.

This is a "needs-light, schedule-heavy" design. A colony sim with needs (hunger, sleep, mood) is the opposite choice, so the cost of needs evaluation must be designed in (section 5).

## 3. Decision architectures compared

| Approach | Strength | Cost / risk at scale | Source | Fit for colonist work choice |
|---|---|---|---|---|
| **Priority table + pull scan** (RimWorld, ONI, DF) | Player-readable, deterministic, cheap per decision if the scan is indexed | Scan cost per idle agent; every agent re-scans the same work | [D], [W], [P] | The proven baseline |
| **Utility AI** (response curves, weighted considerations) | Good for needs and leisure; smooth trade-offs; cheap ordering of considerations with early-out | Tuning is hard; oscillation near ties needs inertia, cooldowns or commitment bonus; scoring every action for every agent is O(agents x actions) | Game AI Pro Ch. 9 and Ch. 13 [P] | Needs and "what to do when no work" |
| **Smart-object advertisements** (Sims) | Decouples agent from content; content-driven | Same all-pairs scoring unless spatially indexed; trap behaviours | Forbus and Wright [P] | Leisure, comfort, recreation |
| **Behavior tree** | Authoring and debugging; reusable nodes | Re-evaluating from the root each decision is slow when processing power is limited (Game AI Pro BSA chapter says so); node state is awkward to store in an ECS | Game AI Pro Ch. 4 [P] | Fine as the *definition* format if evaluated rarely |
| **GOAP** | Emergent multi-step plans; per-action cost | A* over action space per agent; planning each decision is expensive without caching; best for few agents | Orkin 2006 [P] | Poor for mass colonists |
| **HTN** | Faster than GOAP in the authors' experience (Transformers: Fall of Cybertron); partial planning, authored hierarchy | Needs authored domain; replan-on-state-change bugs (plans re-triggering on their own effects) documented | Humphreys, Game AI Pro Ch. 12 [P] | Possible for small hero units |
| **Scripts + open-loop queues** (Highrise, Sims queue) | Cheapest; very predictable | Brittle to changing worlds; only fine if failure is cheap to restart | Zubek [P] | For off-screen or background agents (low LOD) |
| **Global optimal assignment** (Hungarian O(m n^2), auctions) | Optimal for single-task agents and single-agent tasks (ST-SR-IA) | Not incremental; ignores per-agent priority tables; no colony sim in the sample uses it | Gerkey and Matarić 2004 [P] | A batch matcher for specific bulk problems (haul sources to sinks), not general work |

Notes: Orkin's F.E.A.R. paper states plainly that a GOAP-style system gains from decoupling goals from actions and from replanning when the world shows an obstacle, but F.E.A.R. runs a handful of combatants. Behavior-tree and GOAP chapters repeatedly say performance concerns grow with agent count [P]. I found no source arguing GOAP for hundreds of colonists.

## 4. Claiming, reservation and job queues

### 4.1 Shapes seen

| Mechanism | Where | Properties |
|---|---|---|
| **Per-target reservation record** bound to the job (claimant, target, layer, capacity) | RimWorld [D] | Capacity >1, partial stacks, released with the job, force-override for player orders |
| **Posting and application** (jobs are postings, dwarves apply) | DF 2014 [P] | Allows prioritisation and pre-emption of workers by important jobs |
| **Global allocator, single writer** | Factorio bots [P] | No races by construction; requires interchangeable agents |
| **Slot claim handles** (Find, then Claim, then Use, then Release) | Unreal Smart Objects / Mass [low-confidence, API listing only] | Claim is explicit and separate from search; forum reports show invalid-handle failures when find and claim are split across states |
| **Optimistic pick, pessimistic claim at job start** | RimWorld [D]: choose in the scan, `TryMakePreToilReservations` can fail, abort and rescan | Cheap in the common case; failed claims waste a scan |

### 4.2 Pitfalls with evidence

- **Double claiming.** In this repo's prototype, ~13.5 per Tick of 195 meals were double-eats because `Destroy` through command buffers is last-writer-agnostic (`prototype-ecs-bench.md`, surprise 7). RimWorld solves it with a serial, immediate reservation check. DF fixed target-respect bugs in 2014 [P].
- **Leaked claims.** RimWorld binds the claim to the job and cleans on job end and on load. A claim without an owner lifetime is the classic source of "dwarf is stuck" [I]. DF reports dwarves who cancel hauling and hold items with no job (bug tracker, not read in full) [S].
- **Contention between job types over the same item.** DF had to add item swiping from haul jobs to workshop jobs [P]. A claim model needs to say whether a lower-priority claim can be stolen.
- **Starvation.** A strict lexicographic table starves low tiers by design. ONI's wiki and forums are full of examples of players tuning priorities to avoid it [S]. DF's "do now" bypass is the escape hatch [P]. No source gives an aging mechanism; **I did not find one in any of the sampled games** [I]. Any Framework offering aging is adding something new.
- **Job thrash.** RimWorld needs a 10-in-10-Ticks breaker [D]. Utility literature recommends commitment weights, cooldowns or decide-when-idle [P]. HTN literature documents plans that self-trigger replans from their own effects [P].
- **Unreachable jobs.** RimWorld's work scan runs a reachability check per candidate; DF checks region numbers before A*; prior-art doc shows both had frame-spike bugs from missing this. DF dev log has repeated "stuck when paths interrupted" fixes [P]. A job that is unreachable for agent A may be reachable for B (different permissions, movement mode), so "unreachable" should be a per-(movement class, target) cache, not a job state [I].
- **O(agents x jobs) scans.** Each of RimWorld's idle pawns scans candidates for each giver tier (cost bound only by region-first search hooks). Factorio's global idle list was replaced for exactly this reason [P]. Gerkey and Matarić give O(m n^2) for the optimal one-shot assignment, which at m = n = 10^5 is not a per-Tick option [P, math from their stated bound; [I] for the scale statement].

## 5. Needs, thinking budgets and staggering

- **Needs as utility.** The Sims lineage (motives plus advertisements) and Game AI Pro utility chapters give: normalised inputs, response curves, weighted sum or product, cheapest considerations first with early-out [P, Ch. 13], inertia/commitment to avoid ping-pong [P, Ch. 9 and 13]. Lewis' chapter specifically recommends a small commitment bonus on the last-chosen decision and runtime/cooldown considerations [P].
- **Hard overrides for survival needs.** ONI (basic needs override priorities), RimWorld (think-tree priority number; the constant think tree handles urgent reactions) [W, D]. Needs that must pre-empt are expressed as higher-priority tiers, not as scored utilities.
- **Cadence.** RimWorld: constant think tree every 30 Ticks (hash-offset per pawn), main tree on job end [D]. Factorio: forced update at 20 or 60 Ticks, interpolate otherwise [P]. Highrise and Sims Medieval: decide when the queue is empty [P]. Unreal Mass: an Entity's StateTree ticks only when signalled [S, community guide, **UNVERIFIED**].
- **Budgeted LOD.** Ben Sunshine-Hill's LOD Trader (Game AI Pro Ch. 14) picks a detail level per AI feature per character each frame to maximise estimated "noticeability" within a CPU budget, in tens of microseconds [P]. The idea transfers: a budget manager instead of fixed distance thresholds. It was designed for dozens to low hundreds of visible characters, not 100k.
- **Per-agent cost.** No colony-sim source gives a number. The only measurement available is this repo's prototype: the full Tick (movement, hunger, a nearest-food lookup for hungry colonists, wander) averaged 1.02 ms single-thread for 100k colonists, of which the Eat lookup was 0.60 ms with about 2% hungry (`prototype-ecs-bench.md`). That is a hint that a cheap indexed decision costs on the order of tens of nanoseconds per agent-Tick when only a small fraction decide per Tick [I, derived]. A decision that scans many candidates is 100x or more expensive, as the 23 ms regression in that prototype showed.

## 6. What scales to 100k+ agents in a data-oriented/ECS setting

Everything in this section is inference [I] from the sources above plus the prototype, unless a source is named.

1. **Event/cadence-driven decisions.** Only agents that are idle, have an expired job, or are in a trigger state enter the decision path. Hash-offset the periodic re-check by Entity index (RimWorld does this; Factorio does the equivalent). In ECS terms: a `NeedsDecision` tag Component (or enable bit) plus a System over that archetype; the decision System sees a small fraction of colonists per Tick.
2. **Index the work, not the agents.** The scan cost in RimWorld is in finding targets. Provide spatial buckets by work type (Factorio's chunk lists and spiral search; DF's region numbers; RimWorld's `listerThings`). A decision becomes a bounded query: "nearest available work item of type T in reachable region R". This is the "push" index that makes pull cheap.
3. **Two-phase claim: propose, then resolve.** Because parallel Systems and command buffers cannot claim an Entity (prototype finding), do: Phase A (parallel) each deciding agent writes a *proposal* (agent, target, rank key) to a per-job buffer; Phase B (serial or sort-by-target, deterministic) grants one winner per target (or up to capacity), writes a claim Relation, and emits a failure event for losers; losers retry next Tick. The rank key must be deterministic, for example (priority tier, distance, Entity id) so results match across thread counts. This preserves the prototype's determinism property. It costs one extra Tick of latency for losers, which RimWorld's retry-on-failed-reserve also has. This is the main new thing the Framework needs; it is the generalisation of the "reservation Resource" idea in the prototype doc.
4. **Claims as Relations with a lifetime owner.** Model the claim as a Relation from job/agent to target, queryable from either end (GLOSSARY: Relation). Destroying either end must release it, which fixes the leaked-claim class. Capacity and partial-stack amounts (RimWorld's `maxPawns`, `stackCount`) are needed for beds, workstations and item stacks.
5. **Reachability as a cached query per movement class.** Reuse the connectivity service recommended in the prior-art doc. Check it inside the work query, before the scan returns a candidate.
6. **Thrash controls as Framework defaults.** A commitment/minimum-run-time Component and a "jobs started in N Ticks" circuit breaker are cheap and generic. Both have direct precedent (RimWorld [D], Game AI Pro [P]).
7. **Batch matching only where interchangeable.** Hauling sources to stockpile sinks, and Factorio-style bots, are close to the ST-SR-IA case. A per-Tick batch matcher over (items to move, haulers) can use greedy nearest or a bucketed auction. For skilled work with priority tables, per-agent pull is the proven design [I].
8. **Per-System budgets and time slicing.** A decision System can be given a max decisions per Tick and carry a cursor, so a sudden spike (all colonists wake at once) spreads over several Ticks instead of causing a spike. The prototype already has a "every N Ticks with offset" rate unused. A budget cursor needs deterministic order (by Entity index), so it stays reproducible.
9. **Don't model needs for everyone at full fidelity.** Highrise's lesson [P]: hidden state across hundreds of agents is hard to understand and debug. Provide per-agent explanation data ("why did this agent choose X") as a first-class debug output. That cost is only worth paying if it is cheap, so make it opt-in.
10. **Avoid planners for the mass tier.** GOAP/HTN/behavior trees can sit on top for a small set of "hero" agents. This is a Game-author tier decision.

Parallelism caveat from the prototype: scaling across cores topped out near 5x and structural-change playback is serial. Claim resolution is another serial phase; keep it a sort-and-scan over proposals (cache friendly), not a pointer chase.

## 7. Implications for the Framework

### Candidate building blocks (generic, hard to get right, game-agnostic)

1. **Claim/reservation primitive** with: deterministic conflict resolution, capacity and partial amounts, lifetime tied to an owner Entity, release on destroy, and a visible failure event. This directly answers the prototype's open problem.
2. **Work index facility:** spatial/region-bucketed lists of "available work" keyed by a Game-author-defined work type, with incremental add/remove, and a bounded "best candidate" query that honours reachability.
3. **Reachability query** per movement class (already recommended in the prior-art doc), exposed to the work query.
4. **Decision scheduling:** wake-up/tag-based decision triggers, per-Entity hashed periodic re-check, per-System decision budgets with a deterministic cursor.
5. **Commitment and anti-thrash:** minimum job duration, job-start rate limiter, and a circuit breaker that parks an agent with a diagnostic.
6. **Priority table container:** a compact per-agent (work type -> priority) table with a deterministic comparison order. The contents of the table are a Game-author concern; the data structure and the "tier first, then distance" ordering helper are generic.
7. **Utility helpers:** response curve evaluation and cheap-first early-out scoring as pure functions or SIMD-friendly column Systems, optional.
8. **Diagnostics:** per-agent "last decision and why", per-System timing, counters for failed claims, unreachable rejects and thrash trips. The prototype already concluded per-System timing should be a Framework feature.

### Leave to Game authors

- The actual needs list, decay rates and response curves.
- The work types, their order, and whether priority tiers exist (RimWorld 1-4 vs ONI 1-9 plus sub-priority vs DF 7 levels).
- Whether decisions are utility, tree, script or plan (and any HTN/GOAP layer for a few hero agents).
- Job content: what a Toil/step is, step failure conditions, and animation.
- Starvation policy (aging, forced rotation). No sampled game ships one, so the Framework should offer a hook, not a default.
- Social and mood systems, personality, stories.

### Open questions for decisions or ADRs

- Is a claim a Relation, a Resource table, or a Component on the target? (Determinism and parallel access favour a Resource owned by the resolve step; Relations favour cleanup on destroy.)
- One extra Tick of latency for losing proposals: acceptable? RimWorld has the same retry but in the same Tick.
- Should reachability-failure caching live per (target, movement class) with invalidation by connectivity version?
- How does Game-author-supplied scoring stay deterministic when it uses floats? (Prototype showed fixed-point and Philox RNG keyed by Entity work; scoring should follow the same rules.)

## 8. Suggested follow-up research (to close gaps)

1. Read current RimWorld decompiled source (own install with ILSpy/dnSpy) to confirm 1.0 findings: caching in work givers, `TickRare` use, reservation data structure changes.
2. ONI: decompile `Assembly-CSharp` chore classes (`ChoreConsumer`, `Chore`, precondition lists) to confirm the pull structure and per-brain cost.
3. DF: finish the 2014 dev log (second half unread), v50 devlogs on job systems, and the DF wiki Job page when rate limiting clears.
4. GDC Vault: Isla "Handling Complexity in the Halo 2 AI" (2005), Evans Sims 3 (2010), Mark and Dill utility talks.
5. Epic Mass docs: Smart Object slot states and Mass StateTree signalling, if the pages load.
6. A small prototype extending `prototypes/ecs-bench`: replace the Eat System's destroy-on-arrival with propose/resolve claim, measure the extra serial phase cost and the retry rate at 100k and 1M colonists, and a stockpile-hauling scenario with unreachable targets.

## Sources

Primary or near-primary (read):

- Bay 12 dev log 2014: https://www.bay12games.com/dwarves/dev_2014.html (first 100k of 172k characters read)
- Stack Overflow Blog, Dwarf Fortress interview: https://stackoverflow.blog/2021/12/31/700000-lines-of-code-20-years-and-one-developer-how-dwarf-fortress-is-built/
- RimWorld 1.0 decompile (third party, ILSpy): https://github.com/josh-m/RW-Decompile (files `RimWorld/JobGiver_Work.cs`, `Verse.AI/ReservationManager.cs`, `Verse.AI/ThinkNode_PrioritySorter.cs`, `Verse.AI/Pawn_JobTracker.cs`, `RimWorld/WorkGiver_Scanner.cs`, commit dated 2018-10-25)
- RimWorld modding wiki, job/toil/reservation lifecycle: https://rimworldwiki.com/wiki/Modding_Tutorials/Code_MendingJob (community, modder-written)
- Factorio FFF-374: https://direct.factorio.com/blog/post/fff-374 ; FFF-421: https://direct.factorio.com/blog/post/fff-421
- Orkin, "Three States and a Plan: The A.I. of F.E.A.R." (GDC 2006): https://pages.cs.wisc.edu/~dyer/cs540/handouts/gdc2006_orkin_jeff_fear.pdf
- Forbus and Wright, "Some Notes on Programming Objects in The Sims" (2001): https://qrg.northwestern.edu/papers/Files/Programming_Objects_in_The_Sims.pdf
- Game AI Pro (free chapters): Behavior Selection Algorithms overview http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter04_Behavior_Selection_Algorithms.pdf ; Graham, Intro to Utility Theory http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter09_An_Introduction_to_Utility_Theory.pdf ; Humphreys, Exploring HTN Planners http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter12_Exploring_HTN_Planners_through_Example.pdf ; Sunshine-Hill, LOD Trader http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter14_Phenomenal_AI_Level-of-Detail_Control_with_the_LOD_Trader.pdf ; Lewis, Utility-Based Considerations http://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter13_Choosing_Effective_Utility-Based_Considerations.pdf ; Zubek, 1000 NPCs at 60 FPS http://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter34_1000_NPCs_at_60_FPS.pdf
- Gerkey and Matarić, "A formal analysis and taxonomy of task allocation in multi-robot systems", IJRR 23(9), 2004: https://cse-robotics.engr.tamu.edu/dshell/cs689/papers/gerkey04formal.pdf (Hungarian method O(mn^2) and auction approaches for ST-SR-IA)

Community / secondary (cited with labels above):

- ONI priorities (wiki.gg): https://oxygennotincluded.wiki.gg/wiki/Priority
- Steam/Klei forum threads and Rim73 changelog (found via search, snippets only; not individually linked)
- GDC Vault listing for Evans' Sims 3 talk: https://gdcvault.com/play/1012294/Modeling-Individual-Personalities-in-The (abstract only)
