# LogicDeck — Product Requirements

## 1. Vision

LogicDeck is a **realtime collaborative logic-circuit workbench** for small
local teams: a shared whiteboard for digital logic that anyone can open with
just a name — no accounts, no setup, no database. Two people on two machines see
the same gates, wires, and live signal propagation as it happens.

## 2. Target users

| Persona | Need | Job to be done |
|---|---|---|
| Student / learner | Sketches truth tables and gates to understand logic design | Flip switches and immediately see how a half-adder or SR-latch behaves |
| Hobbyist / maker | Iterates on small circuits rapidly | Prototype with switches, clocks, buttons; reuse sub-circuits as ICs |
| Teacher / demo | Shows a circuit to a room | Share one link; everyone watches the simulation live |
| Small team (LAN) | Collaborates on a design in the same room/network | Join a shared bench with zero onboarding friction |

## 3. Goals / non-goals

**Goals**

- Zero-friction identity: display name only; duplicate names auto-dedupe.
- Realtime multi-client editing with server-enforced consistency.
- Live simulation: RUN propagates signals to every client; clocks tick at 2 Hz.
- Combine-and-reuse: package gate selections into custom ICs with labelled pins.
- Durability without setup: circuits auto-save to plain JSON files on the server
  and reload on restart — no database, no accounts.
- Offline-capable deployment: one `dotnet run`, no Node, no DB, vendored assets.

**Non-goals (explicit)**

- User accounts, auth, fine-grained permissions (beyond owner-delete).
- A managed circuit library (browse/name/rename collections, cross-server sync):
  circuits are auto-saved locally per server and shared via export/import JSON.
- VLSI-scale simulation: no timing analysis, no hazards, no analog — boolean only.
- Scale-out / multi-server clustering.
- Verilog/VHDL import/export, schematic capture standards (KiCad et al.).

## 4. Functional requirements

### 4.1 Identity & lobby
- F1 Join with a display name; cleaned (≤ 24 chars, control chars stripped,
  whitespace collapsed; blank → `Anonymous`) and deduplicated case-insensitively
  (`John` → `John 2` → `John 3`).
- F2 Each session gets a distinct color used for avatars and presence cursors.
- F3 Lobby lists all open circuits (title, grid, member avatars, part count,
  running state) and updates live without reload.
- F4 Create a circuit with title (≤ 48 chars) and grid size (8/16/24/32).
- F5 Only the creator can delete a circuit; deletion kicks occupants to the lobby.

### 4.2 Circuit editing (all collaborative)
- F6 Place components by click after arming from palette or hotkey; Shift-click
  keeps placing; grid-snapped drops; ghost preview follows the cursor.
- F7 Components: input switch, output lamp, free-running CLK, momentary push
  button, AND/OR/NOT/XOR/XNOR/NAND/NOR gates, custom ICs, a **7-segment
  hex display** (4 BCD input pins, decodes 0–F), and a **D flip-flop** (D +
  clock-edge inputs, Q/Q̄ outputs — the first sequential part, enabling counters
  and shift registers).
- F8 Wire gates by dragging between ports; auto-routed orthogonal wires; one
  wire per input pin; no self-loops; wires re-validated on the server.
- F9 Move, duplicate (Ctrl+D), marquee-select, delete parts (cascades wires),
  tidy selection into columns, clear board.
- F10 Rename the circuit inline; change grid size for everyone.
- F11 Presence: colored cursors with name tags glide across the canvas; member
  list shows who is in the room.

### 4.3 Simulation
- F12 RUN toggles a server-side fixed-point boolean evaluation; signals flow to
  all clients instantly.
- F13 CLK nodes free-run at 2 Hz while RUN is on.
- F14 Input switches toggle; push buttons are momentary (high while held).
- F15 Oscillating loops are detected and shown amber, not frozen or crashing.
- F16 Visual contract: high wires glow mint, low wires dim, lamps light only
  while running, 7-segment displays decode their BCD input into a lit digit.

### 4.4 Custom ICs
- F17 Any selection of combinational gates can be packaged into a reusable IC
  with user-labelled boundary pins (external inputs become `IN*`, externally
  consumed outputs become `OUT*`).
- F18 ICs appear in the palette, can be placed like parts, and evaluate their
  internal blueprint correctly driven by upstream signals.
- F19 ICs are per-circuit (not global) and ship with export/import.

### 4.5 Persistence & sharing
- F20 Export the circuit (nodes, wires, ICs) as a versioned JSON file; import
  replaces the bench and is fully validated server-side.
- F21 Invite link copies the room URL; opening it joins the room live.
- F26 Circuits auto-save server-side and are restored after a restart: every
  circuit is written to its own JSON file within ~2 s of an edit (and on
  shutdown) and reloaded at startup; a curve that cannot be re-validated is
  skipped. Only input-switch positions survive — RUN, members and live
  clock/button/flop levels reset. Deleting a room (owner or idle GC) deletes its
  file. This is invisible on the client; export/import stays for sharing.

### 4.6 Resilience
- F22 Late joiners and reconnecting clients receive a full snapshot.
- F23 A disconnecting member is held for 15 s; a real leave removes them
  immediately. Idle (empty + 10 min) circuits close and disappear from the lobby.
- F24 Connection state is always visible (LINK OK / RECONNECTING / OFFLINE).

### 4.7 Simulation tooling
- F25 Truth table: with at least one input switch and one output lamp, a
  **Truth table** action enumerates every switch combination and shows each
  lamp's value in a modal, copyable/downloadable as CSV. Supports up to 8
  switches; flops are treated as starting at Q=0 per row.

## 5. Non-functional requirements

- **Setup**: `dotnet run --project LogicDeck.Web`, open the URL; .NET 9 SDK is
  the only prerequisite. Works fully offline (all client libs vendored).
- **Capacity**: 200 circuits, 400 nodes/circuit, 8 members/circuit — enforced
  server-side with friendly errors.
- **Consistency**: server-authoritative; every client converges to the same
  state from the same op stream.
- **Portability**: runs on any OS; LAN mode via `--urls http://0.0.0.0:5050`.
- **Usability**: responsive down to ~360 px; keyboard-operable editor; no mouse
  required for the core loop (hotkeys + Enter/Esc).

## 6. Acceptance criteria (release bar)

1. Two browsers on two machines see each other's edits within a second.
2. A half-adder (A/B switches → SUM/CARRY lamps) is correct for all four input
   combinations, on both clients.
3. An oscillator circuit (NOT loop) is flagged amber on every client without
   freezing the UI or burning CPU.
4. A packaged IC placed and driven by two switches reproduces its blueprint's
   truth table.
5. Refresh on either client restores identical state (snapshot path); rooms and
   their saved circuits survive a server restart (runtime state resets).
6. Killing and restarting the server starts cleanly; saved circuits reappear,
   and any unreadable/corrupt save file is skipped rather than crashing.
7. All unit + e2e tests green; UI audit screenshots regenerate without console
   errors.

## 7. Release history

- **v1.1 (current)** — adds server-side circuit persistence (auto-save/restore),
  a 7-segment hex display, a D flip-flop, and truth-table tooling on top of v1.0.
- **v1.0** — identity, lobby, collaborative editing, live simulation,
  custom ICs, export/import, presence, snapshot-based reconnect (see `README.md`).

## 8. Backlog / ideas

Candidates (owners/decisions tracked in `tasks.md`):

- Named circuit library / folders (browse saved circuits in the lobby).
- Sequential ICs: allow CLK/Button/latches inside packaged blocks with defined
  tick semantics.
- Simulation tooling: per-wire probe readout, step mode.
- Undo/redo per client (needs server-side op history or CRDT-style inverse ops).
- Rooms as shareable read-only links (spectator role).
- Multi-server scale-out via SignalR backplane + shared store.
- Gate count per type in the palette, recently-used parts pinning.
