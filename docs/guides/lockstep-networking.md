# Lockstep multiplayer on Colony

Colony ships no network code (spec section 7.4). It ships the parts that make a lockstep game small to write: a wire form for per-Tick input, a transport-free session state machine, checkpoint hashes with desync localisation, join-in-progress, and per-source permissions. You supply the sockets and the loop. This guide walks from those parts to a running two-peer game; `samples/HelloLockstep` is the worked example and the CI test runs it.

Vocabulary follows `GLOSSARY.md`: every peer runs the same World, one Input source per seat, and only Input Commands cross the wire.

## 1. The shape

```
 peer 0                                            peer 1
 submit script -> LockstepSession                  LockstepSession <- submit script
                    | NextMessage(1) --bytes--> Receive(bytes) |
                    | Receive(bytes) <--bytes-- NextMessage(0) |
 Tick() when every seat's frame is held            Tick() ...
```

Each peer owns a `World` (all built from the identical `WorldSchema`) and one `LockstepSession` (`Colony.Persistence`). Peer `i` is Input source `i`. The Game owns:

- the transport: any ordered or unordered, reliable or lossy carrier of byte messages,
- the loop: receive, submit, `Tick()`, send, and wall-clock pacing,
- the policy: what to do on a stall, a desync, a refused peer.

The session never opens a socket, starts a thread or reads the clock. Its messages are idempotent (cumulative acks, resent frames), so a lost, duplicated or reordered message is repaired by the next one; you do not need a reliable transport, but the sample uses TCP because it is the least code.

The loop (from `samples/HelloLockstep/PeerHost.cs`):

```csharp
using var session = new LockstepSession(world, new LockstepSessionOptions
{
    LocalPeer = seat, Peers = 2, InputDelay = 3, CheckpointEvery = 10,
    HashTrail = trail, Gate = gate,        // the same trail/gate the World was created with
});

while (world.CurrentTick < ticks)
{
    while (mesh.TryReceive(out var bytes)) session.Receive(bytes);      // transport -> session
    if (scripted < session.OpenInputTick)                               // local commands for a future Tick
        MyGame.Script(session, scripted = session.OpenInputTick);       //   -> session.Submit(...)
    session.Tick();                                                     // false = stalled, try again
    for (int peer = 0; peer < peers; peer++)
        if (peer != seat && session.NextMessage(peer) is { IsEmpty: false } m)
            mesh.Send(peer, m);                                         // session -> transport (copy the span)
}
```

## 2. Setting a World up for a session

Pass the World a shared `TickGate` and a `LockstepHashTrail` (also the Diagnostics listener) and give the same objects to the session:

```csharp
var trail = new LockstepHashTrail();
var gate  = new TickGate();
var world = schema.CreateWorld(new WorldOptions
{
    Seed = sharedSeed, TickGate = gate,
    Diagnostics = new DiagnosticsOptions { HashTrail = true, Listener = trail },
});
```

The session holds the gate and releases exactly one Tick per successful `TryBeginTick`; that is how a missing frame stalls the World without any threading. `World.Tick()` then returns true once. `session.Tick()` bundles `TryBeginTick`, `World.Tick()` and `EndTick`; use the three calls separately if you have other holders of the gate (inspection pause/step).

Everything the peers must agree on is checked in a handshake before the first Tick: framework, save, replay and join format versions, build string, Plugins and their order, `SchemaHash` (which covers Components, Resources, Input Commands and permissions), seed, tick rate and `PeerIdentityOptions.Settings`. A peer that differs is refused and `session.Peer(i).Reasons` says why in words; see `PeerHandshake`, `PeerIdentity`, `LockstepPeerStatus`. Never put wall-clock time, thread ids or machine facts in System code: the determinism analyzers (COL1001-COL1007) exist for this.

## 3. Input delay

A command submitted now takes effect at an explicit future Tick: `session.OpenInputTick`. `LockstepSessionOptions.InputDelay` (default 2) is how many Ticks ahead that is; the frame for Tick `T` is closed when the World reaches `T - InputDelay`, which is when it can be sent. A peer cannot run Tick `T` until it holds every seat's frame for `T`, so the delay is the slack you give the network: with a one-way latency of `L` ms and a Tick every `P` ms you want `InputDelay >= ceil(L / P) + 1`.

`AdaptiveInputDelay { Min, Max, WindowTicks }` lets the session tune it: it grows by one after a window that stalled and shrinks by one after a window in which every frame arrived two Ticks early. The delay is a local choice (frames carry their Tick), so peers may differ. In the UI, show commands as accepted at once and apply them at the delayed Tick; the order of effect is the canonical order the World builds, not arrival order.

Cost model: stalls are the price of a delay that is too small; input lag is the price of one that is too large. Prefer the adaptive form with `Min` at 1-2 and a `Max` that fits your latency budget.

## 4. Stalls

`session.TryBeginTick()` / `Tick()` returns false when a required frame is missing (`session.IsMissingFrame(peer)` names the peer), counting `Stalls` and `ConsecutiveStalls`. The World does not advance, nothing is lost, and the next `Receive` may unblock it. Policy is yours: render a "waiting for player 2" overlay after N consecutive stalls, drop the peer after a timeout (the Framework has no peer-drop policy: you end the session or tear the seat down), keep sending `NextMessage` meanwhile so the other side learns what you hold and resends. A peer that asks for frames older than `FrameRetentionTicks` (default 4096) is marked `Lost`.

Test stall behaviour without a network with `Colony.Testing`'s `TestNetwork` and `NetworkProfile` (`Lan`, `Jittery`, `Lossy`, `Hostile`, ...) and `LockstepHarness`, which runs peers against a seeded fault-injecting network deterministically (`tests/Colony.Persistence.Tests/Lockstep/SessionRig.cs` is a session-level rig).

## 5. Checkpoints

Every `CheckpointEvery` Ticks (default 10; every peer must agree) `EndTick()` records `World.Hash()` for that Tick and the session sends it. When two peers hold hashes for the same Tick they compare them: a mismatch is a desync. A shorter interval finds a desync sooner and costs one `World.Hash()` pass per checkpoint, so pick it against your World size (the hash is the cost; the message is eight bytes). `HashTreesKept = N` also keeps the hash tree of the last N checkpoints (section 6).

## 6. Desync handling

A desync means the simulation is not deterministic or a peer was fed different input. The session stops Ticking (`State == Desynced`), raises `Desynced` with the checkpoint Tick, both hashes and the last agreed checkpoint, then peers exchange diagnoses and `DesyncLocalised` fires with a `LockstepDesync` whose `Describe()` names:

- the first diverging Tick, Phase and System, from the `LockstepHashTrail` (requires `DiagnosticsOptions.HashTrail = true` on the World; hash trail Ticks are slower, so decide whether to keep it on in release builds or only in a "send a bug report" mode),
- with `HashTreesKept > 0`, the chunk, Field or Resource that differs (`WorldHashTree.Compare` / `HashTreeComparison`, e.g. `fields/heat chunk (2, 1, 0)`).

What to do is yours: show the player a message and a save, write the `Describe()` text and both `.tree` files (`HashTreeFile`, `colony-inspect desync A.tree B.tree`) to a log, then end the match. The Framework does not try to resync: rollback and state replication are not promised. Typical causes, in order: a System reading something outside the World (clock, thread id, unordered collection: see the analyzers), a host write to the World between Ticks, two builds with different Plugin sets (caught earlier by the handshake), a locally invented Input Command.

## 7. Join in progress

A new peer takes an empty seat in a running match:

1. The joiner asks a founder for a package. The founder, between Ticks, calls `JoinPackage.Capture(world)` (or `Capture(ReplayRecorder)`, which reuses the latest save) and sends `package.ToChunks(size)`: self-verifying chunks, deliverable in any order and by any carrier.
2. The joiner feeds them to a `JoinPackageAssembler` (`Add` returns false for a bad chunk; `IsComplete`, `Complete()`), then `package.Join(schema, new JoinOptions { WorldOptions = ..., Local = identity })`. That handshakes, loads with World hash verification, replays the logged input forward and checks every checkpoint. Every failure is a `JoinPackageException` with a `JoinFailureReason`.
3. The joiner starts a session with `LockstepSessionOptions { LocalPeer = seat, Peers = N, JoinTick = package.EndTick + margin }` and the *same* gate and trail it gave the World. Its frames before `JoinTick` are implicitly empty on every peer, so the founders need nothing from it until then; the session serves the joiner the retained frames it needs to catch up. The founders configure `ActivePeers` with the seats present at start; the joiner's hello makes its seat `Running` on each founder.

`JoinTick` must be a Tick no founder has run when the joiner's hello arrives (otherwise the joiner is refused with "joins at Tick ... but the local peer already ran Tick ..."). The sample picks `EndTick + 60` and paces its Ticks at a couple of milliseconds; a real Game should measure transfer time or have the founders agree on a join Tick through its own lobby protocol. The package is a normal save: size it with `ToChunks`.

## 8. Permissions

Not every seat should be able to issue every command. Declare it on the `WorldBuilder` (or `PluginRegistration`), where it becomes part of the schema hash, so peers with different tables are refused at the handshake:

```csharp
builder.SetInputAccess(spectatorSource, InputAccess.Deny);        // a whole source
builder.SetInputAccess(kickCommand, InputAccess.Deny);            // a whole command ...
builder.GrantInput(hostSource, kickCommand);                      // ... except for the host
```

A Pair rule wins over source and command rules; Deny beats Allow otherwise; `DefaultInputAccess` sets the fallback. A denied command is not hidden: it stays in the canonical list and the replay frame, is skipped by the handlers, and a `CommandRejected` event with `Reason = CommandRejected.PermissionDenied` is available from the first Phase (show a message from it). A malicious peer therefore cannot desync others by sending a forbidden command; it only gets rejections. See `InputPermissions`, `SavedInputPermissions.Differences`. Permissions guard the simulation, not the network: authentication, encryption and anti-cheat are the transport's job, and in plain lockstep every peer holds the full state.

## 9. What the Framework does not do

No transport, NAT traversal, matchmaking, encryption, peer-drop policy, rollback or state replication. Hosting a relay server that only forwards messages is a valid transport (the session is peer-to-peer in shape but does not care how bytes travel).

## 10. The sample

```
dotnet run -c Release --project samples/HelloLockstep -- --seat 0 --ports 5000,5001,5002 --active 0,1
dotnet run -c Release --project samples/HelloLockstep -- --seat 1 --ports 5000,5001,5002 --active 0,1
# a while later, in a third shell:
dotnet run -c Release --project samples/HelloLockstep -- --seat 2 --ports 5000,5001,5002 --join-from 0
```

Each process prints `hash 0x...` at the end; all three are equal. `samples/HelloLockstep/TcpMesh.cs` is the whole transport (listener per seat, bounded outbound queue per destination, length-prefixed packets; it also carries the join chunks). `samples/HelloLockstep.Tests/LoopbackLockstepTests.cs` runs the same code in-process on threads and as separate processes and asserts equal hashes at every Tick, including the joiner's.
