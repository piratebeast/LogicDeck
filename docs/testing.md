# LogicDeck — Testing

## 1. Commands

```bash
dotnet test                                   # whole suite from repo root
dotnet test LogicDeck.Tests                   # same, scoped
dotnet test --filter "FullyQualifiedName~SimulatorTests"
dotnet run --project LogicDeck.Web            # manual testing on http://localhost:5050
```

The suite needs no database, no browser, no network beyond loopback. The
end-to-end tests boot the real app in-process on `http://127.0.0.1:0`. Persistence
tests write to a per-test temp directory and clean it up, so the suite leaves no
files behind.

## 2. Stack

xUnit 2.9 + `Microsoft.NET.Test.Sdk` 17.12, project `LogicDeck.Tests`
(net9.0, nullable enabled) referencing `LogicDeck.Web` and
`Microsoft.AspNetCore.App`. `Xunit` is a global using in the csproj.
`Microsoft.AspNetCore.SignalR.Client` 10.0.11 drives the E2E tests;
`coverlet.collector` is available for coverage.

## 3. Test map

### 3.1 `SimulatorTests` (simulation engine)

Pure functions of nodes/wires — the highest-value red zone.

- Truth tables for every gate: AND, OR, XOR, XNOR, NAND, NOR (`[Theory]` × 4
  input combinations), NOT inversion.
- Composite circuits: half-adder sum/carry across all four input pairs.
- Sequential edge cases:
  - cross-coupled NOR SR-latch **settles** and is *not* flagged unstable;
  - a self-feeding NOT gate *is* reported in `SignalFrame.Unstable`.
- Defaults and sources: unconnected lamp reads low; CLK/Button drive wires from
  their state and appear in `Sources`.
- Custom IC: an internal NOT-block blueprint evaluates correctly through
  boundary pins (`out0` port mapping).
- Seven-segment display: `[Theory]` over nibbles 0, 1, 2, 5, 8, A, F asserting
  the segment mask in `SignalFrame.Displays`; pin-shape invariant
  (`Seven_segment_has_four_inputs_and_no_output_pin`).
- D flip-flop: latches D on the rising edge only, holds through both clock
  levels and D changes (`D_flip_flop_captures_d_only_on_a_rising_clock_edge`);
  divide-by-2 toggle with the Q̄→D feedback loop
  (`D_flip_flop_toggles_as_a_divide_by_two_when_q_bar_feeds_back`); pin-shape
  invariant (`D_flip_flop_pins_follow_the_spec`).
- Truth table: `ComputeTruthTable` enumerates inputs in position order with the
  right AND rows (`Truth_table_enumerates_inputs_in_position_order`) and restores
  live input/flop state afterwards
  (`Truth_table_restores_input_and_flop_state_after_computing`).
- Pin-shape invariants: `Clock_has_no_input_pins_and_has_output`.

### 3.2 `LobbyAndStoreTests` (identity + storage)

- Name registry: numeric suffixes for duplicates (`John`, `John 2`, `JOHN 3`
  — case-insensitive), rejoin keeps name+color, renaming frees the old name,
  name is **held** during the 15 s grace and released after sweep, messy names
  are cleaned (` Anonymous` fallback, control chars stripped, whitespace
  collapsed, 24-char truncation), colors pick the least-used palette entry.
- Store GC: `Sweep` removes only circuits that are **empty AND idle > 10 min**
  (fresh or occupied circuits survive); summaries ordered by activity; node
  removal cascades attached wires.

### 3.3 `EndToEndTests` (live two-client SignalR)

`IAsyncLifetime` fixture boots `Program.BuildApp(["--urls","http://127.0.0.1:0"])`
once per test and connects real `HubConnection`s (WebSockets + SSE +
LongPolling). Waits are `TaskCompletionSource` + `WaitAsync` (5–10 s) — no sleeps.

Coverage (14 tests):

| Test | What it proves |
|---|---|
| `Same_name_joins_get_suffixes_and_rooms_share_live` | name dedup + colors over the wire; node added by A arrives on B; room summary shows 2 members |
| `Simulation_propagates_signals_and_wire_rules_are_enforced` | wire from a lamp rejected ("no output pin"); second wire into a taken input rejected ("already has a wire"); after `SetRunning` + `ToggleInput`, B receives `Signals` with the wire and lamp high |
| `Deleting_a_node_cascades_its_wires_to_other_clients` | `NodesDeleted` carries both node and wire ids to the other client |
| `Clock_ticks_while_running_and_drives_the_lamp` | `ClockTicker` flips the CLK and pushes both phases within 5 s |
| `Duplicating_a_selection_copies_nodes_and_internal_wires_to_all_clients` | clones offset by 32 px (grid 16 × 2), new ids, internal wire re-created, both clients see the copies |
| `Push_button_sets_a_high_signal_while_held` | `SetButton(true)` lights the lamp |
| `Packaging_gates_creates_a_reusable_ic_that_drives_its_output` | `PackageIc` yields labelled pins A/B/Y; placing the IC and toggling both inputs drives the lamp |
| `Only_the_owner_can_delete_a_circuit` | non-owner gets "owner" `HubException`; owner's delete removes it from `FetchRooms` |
| `Seven_segment_display_decodes_four_switches_into_a_digit` | 4 switches wired to a Seg7's pins; toggling bits 0+2 (nibble 5) broadcasts `Displays` mask `0x6D` |
| `D_flip_flop_latches_a_level_on_each_button_pulse` | switch + button drive a FlopD; each press latches the current D into Q (lamps follows Q, frame `Flops`) |
| `Truth_table_reports_every_input_combination` | two switches + AND + lamp; `ComputeTruthTable` returns 4 rows with A/B/OUT1 and the AND column |
| `Truth_table_requires_at_least_one_input_switch` | empty bench rejects `ComputeTruthTable` with an "input switch" `HubException` |
| `Importing_a_circuit_file_replaces_the_room_snapshot` | import replaces title, grid (8), 2 nodes, 1 wire |
| `Tidy_layout_arranges_connected_nodes_into_columns` | x order follows the graph (input < gate < output) |

### 3.4 `CircuitPersistenceTests` (durable circuits)

`CircuitPersistenceTests` builds a `CircuitStore` over a temp directory (a fresh
store over an already-written directory is exactly what a restart does) and
asserts the round-trip and the safety rails:

- circuits survive a store restart: title/grid/nodes/wires come back, the input
  switch's ON position is remembered, `Running` is false and members are empty;
- only input positions are runtime-restored — CLK/Button/FlopD `State` resets;
- deleting a circuit or sweeping an idle room deletes its file;
- an untouched circuit is **not** rewritten on a later flush (no idle disk I/O);
- a corrupt JSON file is skipped without stopping the store;
- a file with dangling wires fails re-validation and is not loaded.

`PersistenceRestartEndToEndTests.Restarting_the_server_restores_persisted_rooms`
boots the app with `BuildApp(args, enablePersistence: true)` and a temp
`--Persistence:Directory`, creates + wires + toggles a room through a real
SignalR client, stops the app (final flush), then **boots a second app** over the
same directory and asserts the room, its nodes/wires and the switch position are
back. It manages its own app lifecycle (not the shared fixture).

## 4. What is NOT covered (yet)

- **UI layer** — `editor.js`/`lobby.js` have no automated tests; the substitute
  is a browser-level check over raw CDP (headless Edge): the Seg7 part was
  verified by arming hotkey `d`, placing parts, drawing wires with real mouse
  drags, toggling inputs and asserting the decoded digit/segments; the truth
  table was verified by wiring two switches to an AND/lamp and asserting the
  rendered dialog rows. Repeat that pattern for other canvas features until a
  proper harness exists.
- **Background sweeps** (`Sweeper`, disconnect grace end-to-end) — covered only
  as unit tests of the underlying methods, not via the hosted services.
- **`CollabHub` details not asserted by E2E**: import rejection paths, IC
  misconfiguration errors, room capacity (200 circuits / 8 members), duplicate
  node cap, grid-size fallback.
- **`UnitTest1.cs`** is an empty scaffold placeholder (see tasks.md).

## 5. Writing new tests — conventions

- **Pure logic → unit test.** Simulator/store changes must land with
  truth-table or invariant coverage in the existing style (`N()`/`W()`
  factories, `[Theory]` over input combinations).
- **Hub behavior → E2E.** Spin up the app once per test via
  `StartServerAndConnect(n)`; use `TaskCompletionSource` for pushes, never
  `Thread.Sleep` in the assertion path; always dispose (the fixture does).
- Name tests after observable outcomes
  (`And_gate_matches_truth_table`, not `TestAndGate`).
- Error-path tests assert with `Assert.ThrowsAnyAsync<HubException>` and check
  the message substring (`StringComparison.OrdinalIgnoreCase`).

## 6. UI audit script (`audit-ui.cjs`)

Not an xUnit test but part of the QA workflow: a Puppeteer script (Node,
`require`d via `PUPPETEER_PATH`) that drives Microsoft Edge headless through
the lobby gate → dashboard → create room → editor, placing parts with hotkeys
and exercising pan/zoom/fit, then writes screenshots to
`docs/screenshots/audit-*.png` (the sources for the docs). It reads internals
through the `window.__ld` hook exposed by `editor.js`
(`board`, `selection()`, `exportData()`). Run it manually when changing layout
or canvas interaction; there is no npm script for it.
