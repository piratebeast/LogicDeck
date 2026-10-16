# LogicDeck — Tasks

Backlog derived from the code as it stands. Priority: **P0** = hygiene/bug-risk,
**P1** = high value, **P2** = nice-to-have. Each item names the evidence in the
code that motivates it.

## P0 — Hygiene (do first)

- [ ] **Remove or fill `LogicDeck.Tests/UnitTest1.cs`** — it is an empty
      `[Fact]` scaffold from the xUnit template. Either delete it or replace it
      with a real smoke test. (`docs/testing.md` §4 notes this.)
- [ ] **Sync README hub-method list with reality** — README lists
      `JoinLobby…SendCursor` but omits `DeleteRoom`, `ImportCircuit`,
      `DuplicateNodes`, `PackageIc`, `TidyLayout`, `SetButton`, `AddIcNode`,
      `AddIcWire`, `LeaveRoom`. Link `docs/api-reference.md` as the contract of
      record.
- [ ] **Decide the fate of root `package.json`** — it exists only so
      `audit-ui.cjs` can `require` puppeteer. Either document that clearly (done
      in `docs/memory.md` + `rules.md`) or move the audit script under
      `scripts/` to make the boundary obvious.

## P1 — Test gaps (see `docs/testing.md` §4)

- [ ] **E2E: import rejection paths** — bad `version`, invalid grid, oversize
      node/wire counts, dangling IC reference, IC containing `Ic`/`Button`/
      `CLK`. All throw `HubException` server-side; none are asserted today.
- [ ] **E2E: capacity limits** — 8-member room block, 400-node cap, 200-circuit
      cap (the last one is expensive; consider seeding a store-level test
      instead).
- [ ] **Unit: `Sweeper`/`ClockTicker` hosted-service behavior** — the loops are
      currently only implied by `EndToEndTests.Clock_ticks_while_running…`;
      a direct unit test of `SweepOnce` (member drop → `MemberLeft`, idle →
      `RoomClosed`) would pin the 5 s cadence contract without timing flake.
- [ ] **Unit: `BuildImportedCircuit` boundary cases** — duplicate ids,
      valid-but-unreferenced IC definitions, label cleaning defaults
      (`IN1`/`OUT1`).

## P2 — Features (from `docs/prd.md` §8 backlog)

- [ ] **Sequential ICs** — allow CLK/Button/latches inside packaged blocks;
      requires defining tick semantics for IC interiors (currently packaging
      rejects anything non-combinational).
- [ ] **Named circuit library** — a browse/rename UI over the auto-saved circuits
      now that they persist (`CircuitPersistence`); the storage layer exists,
      the lobby surface does not.
- [ ] **Undo/redo** — needs server-side op history or inverse ops; currently
      impossible because ops are fire-and-broadcast.
- [ ] **Spectator links** — read-only room role; `EnterRoom` would branch on a
      capability token carried in the URL.
- [ ] **Scale-out spike** — SignalR backplane + shared store proof-of-concept;
      the single-process `Circuit.Lock` model is the blocker to analyze first.

## Polish / small

- [ ] **Multi-selection nudging with arrow keys** — natural companion to the
      marquee/duplicate features (client-only, but needs a batched `MoveNode`
      path like drag).
- [ ] **Reduced-motion support** for cursor lerp and signal glow animations
      (`docs/design.md` §7).
- [ ] **Package-IC dialog** with its own scroll area for long pin lists.
- [ ] **Palette collapse** toggle for small screens.
- [ ] **Per-client error retry hint** — reconnecting currently only shows a
      banner; surface "your last action failed" when an in-flight invoke rejects
      during reconnect.
- [ ] **Palette recents** (client-only; pin recently used parts to the top).

## Done (reference)

- Core editing + collaboration, live simulation, custom ICs,
  export/import, presence, snapshot reconnect, responsive editor,
  full test suite (unit + e2e), UI audit script + screenshots.
- **Circuit persistence** — `Core/CircuitPersistence.cs` versioned per-circuit
  JSON + `PersistenceService` (2 s flush + shutdown), dirty tracking via
  `Circuit.Touched`, restore-on-startup with re-validation; opt-in via
  `BuildApp(args, enablePersistence: true)` so tests stay disk-free; unit +
  restart-e2e covered. Updated the "ephemeral" invariant in `rules.md`.
- **7-segment hex display part** — BCD nibble → lit segments + hex digit
  (`GateType.Seg7`, `SignalFrame.Displays`, browser-verified via CDP).
- **D flip-flop part** — first sequential element: edge-clocked D input,
  Q/Q̄ outputs, Q in the node state, `SignalFrame.Flops`, delta-cycle simulator
  pass; also fixed the generic drag-from-`outN` wire bug; browser-verified via CDP.
- **Truth table** — `CollabHub.ComputeTruthTable` enumerates every input-switch
  combination (≤ 8) through `Simulator.ComputeTruthTable`, rendered in a toolbar
  dialog with CSV copy/download; unit + e2e covered, browser-verified via CDP.
