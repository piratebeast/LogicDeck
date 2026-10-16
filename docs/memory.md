# LogicDeck — Working Memory

Cheat sheet for anyone (human or agent) resuming work on this repo. Facts here
are drawn from the code as-is — verify before relying on them after big changes.

## 30-second orientation

- **What**: realtime collaborative logic-circuit workbench. ASP.NET Core 9 +
  SignalR server, JointJS canvas client; live rooms are in-memory and circuits
  auto-save to JSON files (no database).
- **Run it**: `dotnet run --project LogicDeck.Web` → http://localhost:5050.
- **Test it**: `dotnet test` (unit + two-client SignalR e2e; runs offline).
- **Talk to it**: SignalR hub `/hubs/collab` (contract: `docs/api-reference.md`).
- **Read order for a newcomer**: `README.md` → `docs/architecture.md` →
  `Core/Domain.cs` → `Hubs/CollabHub.cs` → `js/pages/editor.js`.

## Where things live

| Need | File |
|---|---|
| Hub protocol (all ops) | `LogicDeck.Web/Hubs/CollabHub.cs` |
| Domain constants (caps, grids, pin counts) | `LogicDeck.Web/Core/Domain.cs` (`GateSpec`) |
| Boolean engine | `LogicDeck.Web/Core/Simulator.cs` |
| Rooms/gc/clock flip | `LogicDeck.Web/Core/CircuitStore.cs` |
| Circuit save/load (JSON files) | `LogicDeck.Web/Core/CircuitPersistence.cs` |
| Undo/redo history | `LogicDeck.Web/Core/CircuitHistory.cs` |
| Auto-save loop | `PersistenceService.cs` |
| Names/colors/sessions | `LogicDeck.Web/Core/LobbyService.cs` |
| GC + clock loops | `Sweeper.cs`, `ClockTicker.cs` |
| Editor behavior (tools, hotkeys, presence) | `wwwroot/js/pages/editor.js` |
| JointJS paper + link validation | `wwwroot/js/canvas/board.js` |
| Gate/IC shapes, hotkey map | `wwwroot/js/canvas/shapes.js` |
| SignalR wrapper, identity storage | `wwwroot/js/core/hub.js` |
| Design tokens | `wwwroot/css/app.css` (`:root`) |

## Facts that are easy to get wrong

- **Constants** (all in `GateSpec`): 400 nodes/circuit · 8 members/circuit ·
  200 circuits · names 24 · titles 48 · grids 8/16/24/32 (default 16) ·
  disconnect grace **15 s** · idle circuit **10 min** · simulator cap 96
  iterations · sweeper period 5 s · clock period **500 ms (2 Hz)**.
- **Pin mapping**: 2-input gates use pins 0/1; NOT & Output pin 0; Input/CLK/Button
  have no inputs; **Seg7 has pins 0–3 (BCD nibble, bit0 first) and no output pin**;
  **FlopD has in0=D, in1=CLK and two output ports: out0=Q, out1=Q̄** (its Q bit
  lives in the node's `State`, which snapshots/export/duplicate preserve).
  `fromPort` is `out`, or `out{n}` for an IC output pin and FlopD's Q/Q̄ —
  drag-from-flop works through the generalized direction regex. Clients send
  `in{n}` → server `toPin` index.
- **Hotkeys**: Input 1, Output 2, And 3, Or 4, Not 5, Xor 6, Nand 7, Nor 8,
  CLK 9, Xnor 0, Button b, Seg7 d, **FlopD f**.
- **Wire legality** is a single function: `ValidateWireTarget` — no self-loops,
  one wire per input pin, no exact duplicates; imports and IC internals use it too.
- **IC packaging** only accepts combinational gates (And/Or/Not/Xor/Xnor/Nand/
  Nor). ICs never nest (guaranteed at packaging + import; Seg7 is also rejected
  inside ICs), so the recursive simulator is depth-bounded at one level.
- **Flop update pass**: `Simulator.Evaluate` (run under the circuit lock) detects
  CLK 0→1 edges on the **settled** values, captures D, commits, re-settles, and
  repeats for up to flop-count delta iterations — a one-shot nonblocking-update
  cycle like Verilog. Flops sharing a clock edge sample D from the same
  pre-edge snapshot (synchronous semantics); Q̄→own-D toggle (divide-by-2) and
  Q̄→next-CLK ripple chains both settle correctly. Flops are rejected inside ICs
  (combinational-only) at packaging and import, so all flop state is top-level.
- **Signals flow two ways**: after logic-affecting ops (via
  `BroadcastSignalsIfRunningAsync`) and every 500 ms from `ClockTicker` for
  running rooms. STOP powers sinks down (lamps and Seg7 displays go dark via
  `clearSignals`).
- **Truth table**: `CollabHub.ComputeTruthTable` enumerates `Input` combinations
  through `Simulator.ComputeTruthTable` (A/B/… ordered top-to-bottom then
  left-to-right, ≤ **8** switches, needs ≥ 1 switch and ≥ 1 lamp). Flops reset to
  Q=0 each row and all live state is restored — it is read-only, broadcasts
  nothing, and returns `TruthTableDto`.
- **Circuit persistence** (on by default when run normally): every circuit
  auto-saves to `App_Data/circuits/<id>.json` (dir from `Persistence:Directory`,
  flush every `Persistence:IntervalSeconds` = 2 s, plus a shutdown flush) and is
  reloaded on startup. Writes are temp-file + atomic move; loads re-validate and
  skip bad files. It's opt-in in code — `Main` uses
  `BuildApp(args, enablePersistence: true)`, **tests use the `false` default** so
  they never touch the disk (the persistence e2e passes its own temp dir via
  `--Persistence:Directory`). Only input-switch `State` is durable; RUN, member
  lists, clock/button/flop levels reset on restart. Dirty tracking is the
  `Circuit.Touched` event wired up in `CircuitStore.Track`.
- **Undo/redo** is room-global and linear: `Circuit.History` records the pre-edit
  `CircuitState` (title, grid, nodes, wires, ICs) under the circuit lock for every
  structural op — but **not** simulation actions (RUN, switches, buttons). `Undo`/
  `Redo` restore a whole snapshot and return it; others get `CircuitReplaced`
  (reusing import's path), everyone gets `HistoryChanged(canUndo, canRedo)`.
  Bounded to `CircuitHistory.DefaultDepth` = 50; a fresh edit clears redo. A
  per-user undo would clobber teammates' later edits, so it's intentionally global.
- **Late join / reconnect = full snapshot** (`EnterRoom` returns
  `CircuitSnapshot`); no delta replay exists.
- **Names** dedupe case-insensitively with numeric suffixes; the *original*
  casing of the first claimer wins the base name; held for 15 s after disconnect.
- **Optimistic UI**: local action → server confirm → revert+toast on error.
  Moves batch 90 ms; cursors throttle 70 ms + client-side lerp.
- **Ids** must match `^[a-z0-9]{6,40}$`; the client mints a 24-hex sid from
  `crypto.randomUUID()`.
- **sessionStorage keys**: `ld.sid`, `ld.name`, `ld.color` (identity survives
  reload; `switchName()` in lobby clears them).
- **Export format**: `{"format":"logicdeck-circuit","version":1,...}`, 2 MB
  client cap, fully re-validated server-side (Version must be 1).
- **Offline by design**: fonts, joint, jquery, lodash, backbone, signalr,
  bootstrap-icons are all vendored under `wwwroot/lib`. No CDN fallbacks.
- **Breakpoints**: 720 px (icons-only toolbar), 560 px (drop secondary
  buttons, coords readout). `audit-ui.cjs` screenshots at 360 px.
- **Debug hook**: `window.__ld = { board, selection(), exportData(), state }` is
  used by `audit-ui.cjs` and the CDP smoke tests; keep it present when editing
  editor boot code.

## Environment notes

- .NET 9 SDK only. Test project pins `Microsoft.AspNetCore.SignalR.Client`
  10.0.11, xunit 2.9.2, Test.Sdk 17.12.
- LAN mode: `dotnet run --project LogicDeck.Web --urls http://0.0.0.0:5050`
  (launchSettings already uses 0.0.0.0:5050).
- `audit-ui.cjs` runs via Node with `PUPPETEER_PATH` env and a hardcoded Edge
  executable path — it's a manual QA tool, not CI.
- Root `package.json` exists only to support that script; the app has no JS
  toolchain.

## Known quirks / asymmetries (as of this writing)

- `LogicDeck.Tests/UnitTest1.cs` is an empty scaffold.
- README's hub-method list omits newer ops (DeleteRoom, Import, Duplicate,
  PackageIc, TidyLayout, SetButton, Undo/Redo) — full list in `docs/api-reference.md`.
- IC definitions live in a module-level `Map` in `shapes.js` keyed by id and are
  re-registered from every snapshot; they're per-circuit data, not global.
- Circuits now survive a server restart (`App_Data/circuits/`); only **runtime**
  state is lost (members, RUN, live clock/button/flop levels). `App_Data/` is
  gitignored. Deleting an idle/owner-deleted room also deletes its file.
- The editor's `hub.on('RoomsChanged', …)` handler is intentionally a no-op
  (lobby pushes are irrelevant while editing).
