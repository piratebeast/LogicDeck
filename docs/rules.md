# LogicDeck — Engineering Rules

These are the invariants of the codebase. Breaking them breaks the product.

## 1. Product invariants

1. **Server-authoritative, always.** The hub validates and mutates; clients
   render optimistically and reconcile against the server's response
   (return values, then broadcast events). Never trust client geometry, ids, or
   pin indices.
2. **No database, no external services.** Live state is in-memory singletons;
   durability is plain versioned JSON files on the local disk
   (`App_Data/circuits/`, written by `Core/CircuitPersistence`) with no new
   dependency. Anything that looks like persistence must reuse the
   export/import JSON shapes rather than invent a new format.
3. **Zero-build frontend.** `wwwroot/js` ships as native ES modules. No npm
   packages, no bundler, no transpile step — add runtime deps only by vendoring
   files under `wwwroot/lib` and keeping the app offline-capable.
4. **Zero-setup run.** `dotnet run --project LogicDeck.Web` with .NET 9 only.
   No migrations, seed scripts, or config beyond `appsettings.json`.
5. **Ephemerality is a feature — for rooms and sessions.** Empty rooms GC after
   10 idle minutes (their saved file is deleted too) and sessions after 15 s of
   disconnect grace. Circuits survive a server restart, but **runtime** state
   (members, RUN, clock/button/flop levels) is never durable. New features must
   not assume runtime state persists.

## 2. Backend rules (C# / ASP.NET)

### 2.1 Project shape
- `LogicDeck.Web` (`net9.0`, `Nullable: enable`, `ImplicitUsings: enable`) —
  server + static client in one project. Keep it that way.
- `Core/` holds pure domain + services and must stay free of ASP.NET types
  (except `HubGroups`, which references SignalR groups constants — acceptable).
  It may use `System.IO` for the file-backed persistence; put new logic in
  `Core/`, not in the hub, when it isn't protocol.
- `GlobalUsings.cs` owns the common usings; add there, not per-file.

### 2.2 Hub discipline (`CollabHub`)
- Every mutating method: resolve caller (`RequireMembership` /
  `RequireCircuit`) → validate → mutate **inside `lock (circuit.Lock)` →
  build DTOs → broadcast outside the lock. Do not await inside the lock.
- User-facing failures throw `HubException` with a short, message-quality
  sentence; these strings are shown verbatim in toasts — no stack traces, no
  keys/ids in the text.
- Every state-changing op calls `circuit.Touch()`.
- Anything that can change logic must end with
  `BroadcastSignalsIfRunningAsync(circuit)` (or equivalent `Signals` broadcast).
- Structural edits record history (`Record(circuit)`) **before** mutating, under
  the same lock; simulation actions (RUN, switch toggles, buttons) must **not**
  record. Undo/redo restore a whole snapshot: send the restored snapshot to the
  actor, `CircuitReplaced` to `OthersInGroup`, and `HistoryChanged` to the group.
- New gates/capacities/constants go in `GateSpec` — never inline magic numbers
  in the hub.

### 2.3 Concurrency
- `CircuitStore` is a `ConcurrentDictionary`; each `Circuit` owns its `Lock`.
  Never mutate a circuit's dictionaries outside `lock (circuit.Lock)`.
- Background services (`Sweeper`, `ClockTicker`, `PersistenceService`) use
  `PeriodicTimer`, catch non-cancellation exceptions, log, and keep looping.
- Persistence touches a circuit only under `lock (circuit.Lock)`; a failed save
  is swallowed (never fatal) and a corrupt file is skipped on load.

### 2.4 Simulator
- `Simulator` must stay a pure static function of `(nodes, wires, definitions,
  seed)` — no I/O, no clock reads, no mutation of inputs.
- Any change to evaluation must keep the SR-latch-settles / NOT-loop-unstable
  semantics (regression tests in `SimulatorTests`).

### 2.5 Wire model
- Pins: `toPin` indexes the target's inputs (`in0..inN`); `fromPort` is `out` or
  `out{n}` for IC outputs. `ValidateWireTarget` is the single gate for wire
  legality — route every wire-creation path through it (including imports and
  IC internals).
- Self-loops and multiple wires into one input are **always** illegal.

## 3. Frontend rules (`wwwroot/js`)

- ES modules, no globals beyond vendored libs (`signalR`, `joint`).
- Remote events are the only source of cross-client truth: handlers must set
  `state.applyingRemote` around graph mutations so echoes don't re-broadcast.
- Position updates batch through the 90 ms `pendingMoves` debounce; cursors
  through the 70 ms throttle. Don't fire per-mouse-move hub calls.
- New part types need: a shape + ports in `shapes.js`, palette/hotkey entries in
  `editor.js`, and the server-side `GateType`/`GateSpec`/`Simulator` changes —
  in the same commit. Pin naming must match the server convention.
- Hub errors are surfaced via `toast(err.message)`; never swallow them silently
  (debug hooks excepted).

## 4. Testing rules

- Simulator/domain changes require unit tests in the existing style
  (truth tables, invariants) — see `docs/testing.md`.
- Hub-protocol changes require an E2E test driving real clients (no sleeps;
  `TaskCompletionSource` + `WaitAsync`).
- `dotnet test` must be green before merging; the suite runs offline.
- UI-affecting changes: run `audit-ui.cjs` mentally against the breakpoints
  (720 px / 560 px) and keep the auditor console-error-free.

## 5. Style

- C#: file-scoped namespaces, `sealed` by default, records for DTOs, expression
  members, collection expressions (`[]`), primary constructors where used.
  No comments unless the "why" is non-obvious.
- JS: 2-space indent, single quotes, semicolons, `const`/arrow style as in the
  existing files; helper factories (`el`, `$`) over innerHTML for user-derived
  strings (names are injected into cursor tags, keep it escaped).
- CSS: use the `--acc/--bg-*/--ink-*/--amber/--red` tokens; no new hex literals
  outside `:root`. Class names stay kebab-case; state classes are
  `.on/.armed/.hidden/.sig-*`.
- IDs are opaque `IdGen` output — never encode type or position in them.

## 6. Forbidden changes (without a design decision first)

- Adding a database, ORM, or external cache. (File-backed JSON via
  `CircuitPersistence` is the sanctioned durability mechanism — not a DB.)
- Adding an npm/build step or replacing vendored libs with CDN links.
- Adding auth/permissions beyond the owner-delete rule.
- Multi-instance/scale-out hints (sticky sessions, backplanes) — the domain
  model is single-process.
- Changing `SignalFrame`/`CircuitSnapshot` shapes or the `logicdeck-circuit`
  export version without an import-migration path (bump `Version`).

## 7. Workflow

- Run `dotnet test` (and lint/typecheck if configured) before handing work back.
- Don't commit unless asked. Keep changes scoped: one concern per commit.
- Docs in `docs/` describe current behavior — update them in the same change
  when behavior changes (`api-reference.md` is the contract of record).
- Note: root `package.json` is a stub for `audit-ui.cjs` only; do not treat it
  as a JS project manifest.
