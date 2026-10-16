# LogicDeck — Architecture

## 1. Overview

LogicDeck is a realtime collaborative logic-circuit workbench. Multiple browsers
share a circuit canvas: every edit is applied to a server-authoritative,
in-memory model and broadcast to the room, and a live boolean simulation runs on
the server and streams signal frames back to all clients.

Design pillars:

- **Zero setup** — single .NET 9 process, no database, no Node build step.
- **Server-authoritative** — the hub validates every operation (pin rules,
  one-wire-per-input, no self-loops, capacities) before mutating state;
  clients render optimistically and reconcile against the server's response.
- **Ephemeral rooms, durable circuits** — rooms GC when idle, but every circuit
  is auto-saved to a JSON file and reloaded on restart; runtime state (members,
  RUN, clock/flop levels) is never durable. Export/import JSON is still the
  user-facing portability story.

```
Browser ──┐                                    lobby:{all}
Browser ──┼── WebSocket ──► CollabHub ──► CircuitStore (in-memory)
Browser ──┘    SignalR                       room:{id} groups
                       ▲                              │ dirty rooms
                       ├── Sweeper (5 s tick)          ▼
                       ├── ClockTicker (500 ms tick)   CircuitPersistence ──► App_Data/circuits/*.json
                       └── PersistenceService (2 s tick + shutdown)
```

## 2. Solution layout

```
LogicDeck.sln
├─ LogicDeck.Web/                     ASP.NET Core 9 app (server + static client)
│  ├─ Program.cs                      app builder: DI, SignalR, static files, routes
│  ├─ Hubs/CollabHub.cs               the entire collaborative protocol
│  ├─ Core/                           domain + services (no web dependencies)
│  │  ├─ Domain.cs                    GateType, GateSpec, NodeItem, Wire, Circuit, IcDefinition, IdGen
│  │  ├─ Dtos.cs                      wire-format records (NodeDto, SignalFrame, CircuitSnapshot, …)
│  │  ├─ CircuitStore.cs              room registry, snapshots, sweeps, clock ticks, dirty-flush
│  │  ├─ CircuitPersistence.cs        versioned on-disk circuit format (read/validate/atomic write)
│  │  ├─ CircuitHistory.cs            bounded undo/redo stack + CircuitState snapshot
│  │  ├─ CircuitMapping.cs            shared domain ⇄ DTO conversions
│  │  ├─ LobbyService.cs              session/name registry, palette colors, disconnect grace
│  │  ├─ Simulator.cs                 fixed-point boolean evaluation + oscillation detection
│  │  ├─ PresenceTracker.cs           connectionId → sessionId map
│  │  ├─ HubGroups.cs                 group-name helpers ("lobby", "room:{id}")
│  │  └─ ICollabClient.cs             strongly-typed client callback interface
│  ├─ Sweeper.cs                      BackgroundService: GC of sessions and idle rooms
│  ├─ ClockTicker.cs                  BackgroundService: drives CLK nodes at 2 Hz
│  ├─ PersistenceService.cs          BackgroundService: flushes dirty circuits + shutdown save
│  └─ wwwroot/                        zero-build ES-module frontend
│     ├─ index.html                   lobby gate + dashboard page
│     ├─ circuit.html                 editor page (served for /circuit/{id})
│     ├─ css/app.css                  design tokens + all styling
│     ├─ js/pages/lobby.js            lobby page controller
│     ├─ js/pages/editor.js           editor page controller (largest client file, ~1200 lines)
│     ├─ js/canvas/board.js           JointJS paper: grid, zoom/pan, link validation
│     ├─ js/canvas/shapes.js          JointJS element/link definitions + palette icons
│     ├─ js/core/hub.js               SignalR wrapper, reconnect policy, identity storage
│     ├─ js/core/dom.js               tiny DOM/toast helpers
│     └─ lib/                         vendored offline deps (joint, jquery, lodash, backbone,
│                                     signalr, bootstrap-icons, fonts)
├─ LogicDeck.Tests/                   xUnit unit + end-to-end tests
├─ docs/                              this documentation + screenshots
└─ audit-ui.cjs                       Puppeteer UI smoke script (not part of the app)
```

## 3. Runtime topology

`Program.cs` wires a minimal host:

- `LobbyService` and `CircuitStore` as **singletons** (shared mutable state).
- `Sweeper` and `ClockTicker` as **hosted services**; `PersistenceService` too
  when persistence is enabled.
- Persistence is opt-in at build time: `Main` calls
  `BuildApp(args, enablePersistence: true)`, tests call the default
  (`false`), so the test host never touches the disk. When enabled, the
  directory comes from config `Persistence:Directory` (default
  `App_Data/circuits`, resolved against the content root) and the flush period
  from `Persistence:IntervalSeconds` (default 2). `CircuitPersistence` is
  constructed first, passed into the `CircuitStore` singleton, and handed to
  `PersistenceService`.
- SignalR with `EnableDetailedErrors` and the `JsonStringEnumConverter` so
  `GateType` travels as a string (`"And"`, `"Clk"`-style casing as serialized by
  System.Text.Json).
- Pipeline: `UseDefaultFiles` → `UseStaticFiles` → `MapHub<CollabHub>("/hubs/collab")`
  and a `GET /circuit/{id}` route that streams `circuit.html` for any room id
  (the client validates the id by trying to enter).

There is no auth, no database, no distributed cache: **one process holds the
whole world**. See §8 for the consequences.

## 4. Domain model (`Core/Domain.cs`)

| Entity | Role |
|---|---|
| `GateType` enum | `Input`, `Output`, `And`, `Or`, `Not`, `Xor`, `Xnor`, `Nand`, `Nor`, `CLK`, `Button`, `Ic`, `Seg7`, `FlopD` |
| `GateSpec` | all tunables: `MaxNodesPerCircuit=400`, `MaxMembersPerCircuit=8`, `MaxCircuits=200`, `MaxNameLength=24`, `MaxTitleLength=48`, `GridSizes={8,16,24,32}` (default 16), `DisconnectGraceSeconds=15`, `IdleCircuitMinutes=10`; pin counts per type; `CleanText` sanitiser |
| `NodeItem` | a placed component: id, type, x/y, `State` (driven for Input/CLK/Button), `DefinitionId` for ICs |
| `Wire` | `FromNodeId`/`FromPort` (`out` or `outN`) → `ToNodeId`/`ToPin` (`inN` index) |
| `Circuit` | aggregate root: `Nodes`, `Wires`, `CustomIcs`, `Members` dictionaries + `Lock` object + `LastActivityUtc`; `Touch()` stamps activity and raises a `Touched` event the store subscribes to for dirty tracking; `Capture()`/`Restore()` snapshot the editable content |
| `CircuitHistory` | bounded (50) undo/redo stack of `CircuitState` snapshots, room-global and linear |
| `CircuitState` | immutable snapshot of title, grid, nodes, wires and ICs (what an edit can change) |
| `IcDefinition` | a packaged sub-circuit: boundary `Inputs`/`Outputs` pins with labels, internal `Nodes`/`Wires` |
| `MemberInfo` | sessionId, display name, palette color |
| `IdGen` | 12-char base36 ids from `RandomNumberGenerator`; validity regex `^[a-z0-9]{6,40}$` |

Pin conventions live in `GateSpec`: 2-input gates take pins 0/1; NOT and Output
take pin 0; Input/CLK/Button/Ic take 0 pins (ICs take their definition's pin
count). Every type except `Output` has an output pin (ICs expose `out0..outN`).

`Circuit` owns its synchronization primitives — callers always do
`lock (circuit.Lock) { … }` around mutation + snapshot; the store itself is a
`ConcurrentDictionary`.

## 5. Server-side request flow

`CollabHub` (`Hubs/CollabHub.cs`) is the whole protocol, ~770 lines. Every mutating
method follows the same shape:

1. `RequireMembership()` / `RequireCircuit()` — resolve the caller's session
   (`Context.Items["sid"]`, set by `JoinLobby`) and the room
   (`Context.Items["roomId"]`, set by `EnterRoom`).
2. **Validate** under `lock (circuit.Lock)`; violations throw `HubException`
   with a user-facing message (these surface as toasts on the client).
3. **Mutate** the `Circuit`, `Touch()` it, and build a snapshot/DTO inside the lock.
4. **Broadcast** outside the lock:
   - `Clients.OthersInGroup(Room)` for positional events (`NodeAdded`, `WireAdded`,
     `InputToggled`, …) — each carries `bySessionId` so the originator can dedupe.
   - `Clients.Group(Room)` for `Signals` (the simulation frame, same for everyone).
   - `Clients.Group(Lobby)` for `RoomsChanged` whenever a room summary changes.

Group membership: every connection joins `lobby` at `JoinLobby`; `EnterRoom`
adds it to `room:{id}`. So lobby-wide events reach everyone, room events only
the occupants.

### 5.1 Signal distribution

Two paths push `SignalFrame`s:

- **Op path** — `BroadcastSignalsIfRunningAsync(circuit)` after any op that can
  change logic (add/move/delete node, add/remove wire, toggle, IC package).
  It re-runs `circuit.Evaluate()` on the server and broadcasts to the whole room
  if `Running`.
- **Clock path** — `ClockTicker` every 500 ms flips every `CLK` node in running
  circuits (a 2 Hz square wave), re-evaluates, and broadcasts to each room.

Clients never simulate; they classify wires/lamp visuals from the frame
(`sig-high` / `sig-low` / `sig-unstable`, lamp glow).

### 5.2 Lifecycle services

**Sweeper** (every 5 s):
1. `LobbyService.Sweep` — drop sessions whose 15 s disconnect grace expired.
2. `CircuitStore.DropMembers` — remove members whose session is gone, broadcast
   `MemberLeft` to their rooms.
3. `CircuitStore.Sweep` — delete circuits with **zero members** and
   `LastActivityUtc` older than 10 min, broadcast `RoomClosed` to the lobby.
4. Broadcast a fresh `RoomsChanged` if anything changed.

**ClockTicker** (every 500 ms): `CircuitStore.TickClocks()` → `Signals` per room.

**PersistenceService** (every 2 s, plus a final flush on shutdown):
`CircuitStore.FlushDirty()` writes each circuit that was `Touch()`ed since the
last flush to its `<id>.json` file (temp file + atomic move), and deletes files
for rooms that vanished. Untouched circuits are never re-serialized, so an idle
server does no disk I/O. A failed save is logged and skipped; startup re-validates
every file and skips the bad ones.

All three are `PeriodicTimer` loops with try/catch so a single failure is logged
and the loop continues.

### 5.2.1 Persistence format (`Core/CircuitPersistence.cs`)

One file per circuit at `Persistence:Directory/<circuitId>.json`, holding a
versioned `PersistedCircuit` record (schema `Version` = 1) with title, grid,
timestamps, owner, and the same `NodeDto`/`WireDto`/`IcDefinitionDto` shapes the
import path uses. Only the input-switch `State` is stored as runtime data; clock
level, button press, flop Q and RUN are deliberately dropped. On load each file
is re-validated (ids, grid, node caps, pin legality, no self-loops,
one-wire-per-input, IC boundary pinning) and skipped silently if it fails, so a
corrupt file can never stop the server from starting.

### 5.3 Disconnect / reconnect

- `OnDisconnectedAsync`: if the connection had a room, detach it **without**
  broadcasting (the member may reconnect), then `LobbyService.MarkDisconnected`
  starts the 15 s grace.
- The client (`js/core/hub.js`) auto-reconnects with backoff
  `0,1s,2s,4s,8s,15s,30s`; `editor.js` on reconnect re-joins the lobby and calls
  `EnterRoom` again, then `applySnapshot()` — full state resync, so recovery is
  snapshot-based, not delta-based.
- A "real" leave (`LeaveRoom`, back button, `beforeunload`) detaches and
  broadcasts `MemberLeft` immediately.

### 5.4 Undo / redo

`Circuit.History` (a `CircuitHistory`) records the pre-edit `CircuitState` of every
structural op — add/move/delete node, add/remove wire, duplicate, package IC,
tidy, rename, grid size, import — just before it mutates, while still under
`lock (circuit.Lock)`. Simulation actions (RUN, switch toggles, button presses)
are deliberately **not** recorded, so undo never fights the live signals.

Because a restore replaces the whole circuit, undo is **room-global and linear**:
`Undo` pops the most recent pre-edit state and `Restore`s it (redo is the mirror
image), so it can never clobber a more recent edit by someone else. The actor
gets the new snapshot as the return value; everyone else gets `CircuitReplaced`
(the same path import uses, so client `applySnapshot` needs no new code). The
stack is bounded to 50 states and any fresh edit clears the redo branch.

Every recorded op also broadcasts `HistoryChanged(canUndo, canRedo)` to the room
(and `EnterRoom` sends it to the caller), which drives the toolbar buttons. The
`CircuitSnapshot`/`SignalFrame` wire shapes are unchanged.

## 6. The Simulator (`Core/Simulator.cs`)

A deterministic fixed-point evaluator, no event queue:

1. `wireValue[id]` starts from `initialWireValues` (used for IC interiors) or `false`.
2. Repeat up to `MaxIterations = 96`: recompute each source node's outputs and
   write its outgoing wires; stop early when a full pass changes nothing.
3. Anything still mismatching after the loop is **unstable**; instability then
   propagates forward (`Grew()` loop) so downstream nodes fed by oscillators are
   flagged too. This is what paints amber wires — e.g. a NOT gate feeding itself.
4. Build `SignalFrame { Wires, Unstable, Outputs, Sources }`:
   - `Outputs` — value at each lamp's pin 0 (false if unconnected).
   - `Sources` — raw state of Input/CLK/Button nodes (so the UI can paint
     switches/clocks authoritatively from the frame).

**Custom ICs** evaluate by expansion: the IC node's incoming pin values seed the
definition's internal boundary `Input` nodes, the internal graph is evaluated
recursively (`Evaluate(internalNodes, definition.Wires, null, seed)`), and the
definition's `Outputs` map to ports `out0..outN`. ICs cannot contain ICs (enforced
at packaging and import), so recursion depth is bounded at one level.

Notable: a cross-coupled NOR SR-latch **settles** and is *not* flagged unstable
(regression-tested); a pure NOT loop **is** flagged (regression-tested).
**Sinks** are evaluated separately from drivers: lamps read `Outputs`; 7-segment
displays read four input pins as a BCD nibble and land in `Displays` as a 7-bit
segment mask (regression-tested). After the combinational settle, flops run a
delta-cycle update pass (edge detect → capture → commit → re-settle, bounded by
the flop count) and report their Q bit in `Flops`.

`Simulator.ComputeTruthTable` reuses `Evaluate` to enumerate every `Input` switch
combination (A = most significant) and collect each lamp's value for the truth
table. It temporarily drives the Input/Flop state and restores it before
returning, so the request is read-only from the circuit's perspective (invoked by
`CollabHub.ComputeTruthTable`, capped at 8 variables).

## 7. Frontend architecture (`wwwroot/js`, no build step)

Native ES modules, no framework, no bundler — the browser loads them directly.

- **`core/hub.js`** — one `HubConnection` per page: event fan-out (`hub.on`),
  error normalisation (strips SignalR wrapper text from `HubException`),
  identity in `sessionStorage` (`ld.sid`, `ld.name`, `ld.color`; sid is a
  24-char hex from `crypto.randomUUID`), auto-reconnect.
- **`pages/lobby.js`** — name gate → dashboard. Renders room cards from
  `RoomsChanged` pushes (`FetchRooms` on boot/reconnect); creates rooms; shows
  owner-only delete buttons.
- **`pages/editor.js`** — the application proper. Owns the tool state machine
  (armed part + ghost preview), selection (click/shift/marquee), selection ops
  (duplicate, package, tidy, clear), the wire-drawing gesture, keyboard map,
  presence cursors, and all hub event handlers that apply remote ops to the
  JointJS graph. Guards re-entrancy with `state.applyingRemote` so locally-applied
  remote echoes don't re-broadcast.
- **`canvas/board.js`** — wraps `joint.dia.Paper`: dot grid, zoom clamps
  (0.3–2.6), pan (middle-drag or Space+drag), fit-to-content, grid changes, and
  client-side link validation (`validateMagnet`/`validateConnection` enforce
  one-wire-per-input before the server ever sees it).
- **`canvas/shapes.js`** — `joint.dia.Element.define('ld.<Type>')` SVG shapes
  for all gates (plus inversion bubbles for NAND/NOR/NOT/XNOR and the XOR
  arc), `ld.Ic_<definitionId>` shapes generated per packaged IC with pin labels,
  and the `ld.Wire` link (orthogonal router, rounded connector). Ports follow the
  `in0/in1`/`out0..outN` convention that mirrors the server wire model.

### 7.1 Optimistic UI policy

Input toggles, button presses, wire creation and deletes are applied locally
first, then confirmed by the hub return value; on error the UI reverts and
toasts. Position (move) events are batched: 90 ms debounce of `pendingMoves`
then one `MoveNode` per moved node.

## 8. State, capacity, and scaling limits

| Aspect | Behavior |
|---|---|
| Storage | In-memory rooms + file-backed circuits: each circuit auto-saves to `App_Data/circuits/<id>.json` and reloads on restart. Runtime state (members, RUN, clock/flop levels) is lost on restart; export/import remains the portability path |
| Capacity | 200 circuits × 400 nodes × 8 members; names ≤ 24 chars, titles ≤ 48 |
| Concurrency | Fine-grained: `ConcurrentDictionary` of rooms, per-room `lock` |
| Scale-out | **Not supported** — no backplane; multiple instances would diverge. Horizontal scale needs a shared store + SignalR backplane (Redis) and sticky or explicit room affinity |
| Simulation cost | O(nodes × wires) per pass, ≤ 96 passes; room broadcast is O(members). Fine at the 400-node cap; a hostile circuit could still burn CPU per op |
| Security | No auth by design; `HubException` messages are user-facing and sent verbatim to clients |

## 9. Extension points

- **New part type**: follow the worked examples of `Seg7` (7-segment display)
  and `FlopD` (first sequential part): `GateType` + `GateSpec.All`/`InputPins`/`HasOutputPin`,
  evaluation in `Simulator` (extend `SignalFrame` if the part needs new output
  data — `Displays` and `Flops` were added for those two), a shape + ports +
  icon in `shapes.js`, palette/hotkey and paint-from-frame branches in
  `editor.js`, plus tests. Pin naming must match the server convention; multi-output
  parts need the `out{n}` port-name path (hub + drag-direction regex).
- **New hub op**: method on `CollabHub` (validate → mutate under lock → broadcast),
  mirror on `ICollabClient` if it has a push counterpart, invoke from the client.
- **Persistence**: implemented via `CircuitPersistence` — file I/O is isolated from
  the in-memory `CircuitStore` by the `PersistedCircuit` DTO (a close cousin of
  `CircuitImportDto`), so swapping the storage engine would only touch that one
  file. New durable fields belong in `PersistedCircuit` with a `Version` bump and
  an import migration.
- **Realtime scale-out**: add a SignalR backplane and move rooms behind a shared
  store; the per-room lock would become a distributed concern.
