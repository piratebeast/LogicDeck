# LogicDeck — Hub API Reference

The entire client/server contract lives on `CollabHub`
(`LogicDeck.Web/Hubs/CollabHub.cs`), a **typed** SignalR hub
(`Hub<ICollabClient>`) mounted at `/hubs/collab`. `GateType` is serialized as a
string (`JsonStringEnumConverter`); all other payloads are the records in
`Core/Dtos.cs`.

Conventions:

- **C → S** = client-invoked hub method. **S → C** = server push via
  `ICollabClient`.
- Room-scoped pushes go to group `room:{circuitId}`; lobby-wide pushes go to
  group `lobby`. Cursor/op events carry the actor's `sessionId` so originators
  can ignore their own echo.
- Validation failures throw `HubException("<message>")`; the message is shown to
  the user verbatim in a toast. Client-side errors strip SignalR wrapper text.
- Ids must match `^[a-z0-9]{6,40}$` (`IdGen.IsValid`); server-generated ids are
  12-char base36.
- **Persistence is transparent**: there is no save/load method. The server
  auto-saves changed circuits to `App_Data/circuits/` in the background and
  restores them at startup (see `docs/architecture.md` §5.2.1). The only
  client-visible effect is that rooms created before a restart still appear in
  `FetchRooms` afterwards; runtime state (members/RUN/live levels) resets.

## 1. Identity & lobby (C → S)

### `JoinLobby(sessionId, name) → SessionInfo`
Creates or resumes a session, allocates a deduplicated name
(`John` → `John 2` → `John 3`, case-insensitive) and a least-used palette color.
Stores the session in `Context.Items["sid"]` and joins group `lobby`.
Invalid `sessionId` is replaced server-side.

### `FetchRooms() → LobbyState`
`{ Rooms: RoomSummary[] }`, most-recently-active first.

### `CreateRoom(title, int gridSize) → RoomSummary`
Fails with `HubException` at `MaxCircuits` (200). Invalid grid falls back to 16;
title is cleaned and truncated to 48. Caller becomes `OwnerSessionId`.
Broadcasts `RoomsChanged` to the lobby.

### `DeleteRoom(roomId)`
Owner only (`circuit.OwnerSessionId == caller`). Broadcasts `RoomClosed` to the
room and `RoomsChanged` to the lobby.

### `ImportCircuit(CircuitImportDto) → CircuitSnapshot`
Replaces the room's contents wholesale; others get `CircuitReplaced`.
Validated: version must be 1; valid grid; ≤ 400 nodes, ≤ 1600 wires; every node
id valid/unique; IC nodes must reference an included definition; IC definitions
must be non-empty, pin ids valid and unique, no `Ic`/`Button`/`CLK` inside, and
all internal wires pass `ValidateWireTarget`; ≥ 1 input and ≥ 1 output pin.

## 2. Room membership (C → S)

### `EnterRoom(roomId) → CircuitSnapshot`
Adds the caller to the room (fails at `MaxMembersPerCircuit` = 8), joins group
`room:{id}`, returns a **full snapshot** (title, grid, running flag, all nodes,
wires, members, custom ICs, plus a `Signals` frame when running). Late joiners
and reconnects therefore need no delta replay. First join broadcasts
`MemberJoined` to others and `RoomsChanged` to the lobby.

### `LeaveRoom()`
Detaches from the current room and broadcasts `MemberLeft` + `RoomsChanged`.
Also called on `beforeunload` and back-navigation.

## 3. Circuit editing (C → S)

All require room membership. Multi-user ops push `…Added/Moved/Deleted` with
`bySessionId` to `OthersInGroup`, and re-broadcast `Signals` when running.

### `AddNode(GateType type, double x, double y) → NodeDto`
Snaps to grid. Fails when the type is unknown or the room is at 400 nodes.

### `AddIcNode(GateType type, double x, double y, string definitionId) → NodeDto`
Same, but the definition must exist in this circuit.

### `DuplicateNodes(string[] nodeIds) → DuplicationDto`
Copies selected nodes offset by `2 × gridSize` (snapped) and re-creates wires
that connect two copied nodes. Fails if the result would exceed 400 nodes.

### `MoveNode(string nodeId, double x, double y)`
Snaps; returns nothing (broadcast carries the snapped coordinates).

### `DeleteNodes(string[] nodeIds)`
Cascade-deletes attached wires; broadcast `NodesDeleted(nodeIds, wireIds, by)`.
`NodesDeleted` covers wires too — deleting nodes cascades their wires, and the
event carries both id lists. Deleting individual wires uses `RemoveWire`.

### `AddWire(fromNodeId, toNodeId, toPin) → WireDto`
`fromPort` is always `"out"`. Validation (`ValidateWireTarget`):
- source must have an output pin (lamps and 7-segment displays don't, so wires can't start there);
- valid source ports: `out` for single-output parts; `out{0..n}` for custom ICs and the FlopD's Q (`out0`) / Q̄ (`out1`);
- `toPin` must be a real input pin of the target (ICs use the definition's count);
- no self-loops (`from.Id == to.Id`);
- one wire per input pin (`That input already has a wire.`);
- no exact duplicate (`These pins are already wired.`).

### `AddIcWire(fromNodeId, toNodeId, toPin, fromPin) → WireDto`
Same, for a specific `out{fromPin}` of an IC source; rejects `out` or an
out-of-range `fromPin`.

### `RemoveWire(wireId)`
No-op if missing; otherwise broadcast `WireRemoved`.

### `ToggleInput(nodeId) → bool`
Input switches only; flips state, returns the new value.

### `SetButton(nodeId, bool pressed) → bool`
Push buttons only; momentary state.

### `SetRunning(bool)`
Flips the whole-room simulation; when starting, an initial `Signals` frame goes
to the entire room.

### `ComputeTruthTable() → TruthTableDto`
Read-only simulation tool: enumerates every combination of `Input` switch states
(A = most significant, switches ordered top-to-bottom then left-to-right) and
evaluates the circuit for each, returning one row per combination with every
lamp's value. Fails with a `HubException` when the room has no input switches, no
output lamps, or more than `Simulator.MaxTruthTableInputs` (8) input switches.
Flops restart at Q=0 for each row (a combinational view) and all live
Input/Flop state is restored before returning — nothing is broadcast.

### `RenameRoom(title) → RoomSummary`
Cleaned/truncated to 48 chars; pushes `TitleChanged` to others.

### `SetGridSize(int gridSize)`
Must be one of 8/16/24/32 (else resets to 16); pushes `GridSizeChanged`.

### `SendCursor(double x, double y)`
Throttled presence; server rounds to 1 decimal. Silently ignored if the caller
isn't a room member.

## 4. Custom ICs (C → S)

### `PackageIc(name, nodeIds, inputLabels, outputLabels) → PackageIcResult`
Turns a selection of **combinational gates only** (AND/OR/NOT/XOR/XNOR/NAND/NOR)
into an `IcDefinition` + a single IC node placed at the selection's min corner:

- Boundary **inputs** are created for every unconnected input pin (and pins fed
  from outside the selection), labelled from `inputLabels` (`"{nodeId}:in{pin}"`
  keys) or `IN{n}`.
- Boundary **outputs** are created for every gate output with no internal
  consumer, labelled `OUT{n}` or the supplied label; at least one is required.
- The original nodes and all attached wires are removed; surviving external
  wires are re-routed to the IC's numbered pins.
- Others receive `IcPackaged(definition, node, removedNodeIds, removedWireIds, addedWires, bySessionId)`.

### `TidyLayout(nodeIds) → TidyLayoutDto`
Layered layout: node level = longest path from a source along wires between the
selected nodes; column `x = 32 + level × 144`, row `y = 32 + index × 112`,
grid-snapped. Empty selection = whole circuit. Broadcasts one `NodeMoved` per
node.

## 5. Server pushes (S → C, `ICollabClient`)

| Event | Payload | Meaning |
|---|---|---|
| `RoomsChanged` | `RoomSummary[]` | lobby list changed |
| `RoomClosed` | `circuitId` | room deleted (owner or idle GC) |
| `MemberJoined` / `MemberLeft` | `MemberInfo` | presence |
| `NodeAdded` | `NodeDto, bySessionId` | part placed (incl. by you — clients dedupe via `bySessionId`) |
| `NodeMoved` | `nodeId, x, y, bySessionId` | snapped position |
| `NodesDeleted` | `nodeIds[], wireIds[], bySessionId` | cascade delete |
| `WireAdded` / `WireRemoved` | `WireDto` / `wireId, bySessionId` | connections |
| `InputToggled` / `ButtonChanged` | `nodeId, state, bySessionId` | source state changed |
| `RunningChanged` | `bool` | sim on/off |
| `TitleChanged` / `GridSizeChanged` | `string` / `int` | room metadata |
| `IcPackaged` | `definition, node, removed…, added…, bySessionId` | someone packaged an IC |
| `CircuitReplaced` | `CircuitSnapshot` | someone imported a file |
| `Signals` | `SignalFrame` | new evaluation (after ops and on 2 Hz clock ticks); `displays` carries 7-segment masks, `flops` carries flop Q bits |
| `CursorMoved` | `sessionId, name, color, x, y` | presence cursor |

## 6. Wire formats (selected records, `Core/Dtos.cs`)

```csharp
NodeDto(id, type, x, y, state, definitionId?)
WireDto(id, fromNodeId, toNodeId, toPin, fromPort? = "out")
IcPinDto(id, label)                       // boundary pins
IcDefinitionDto(id, name, inputs[], outputs[], nodes[], wires[])
CircuitSnapshot(circuitId, title, gridSize, running, nodes[], wires[],
                members[], signals?, customIcs?)
SignalFrame(wires: {wireId → bool}, unstable: wireId[],
            outputs: {lampId → bool}, sources: {sourceId → bool},
            displays: {seg7Id → 7-bit mask (bit0=a … bit6=g)},
            flops: {flopId → Q})
RoomSummary(id, title, gridSize, createdUtc, running, nodeCount,
            members[], ownerSessionId?)
TruthTableVar(id, label)
TruthTableRow(inputs: bool[], outputs: bool[])
TruthTableDto(inputs: {id, label}[], outputs: {id, label}[], rows: TruthTableRow[])
```

Client export file format (`buildExportData` in `editor.js`), version 1:

```json
{
  "format": "logicdeck-circuit",
  "version": 1,
  "title": "Half-adder",
  "gridSize": 16,
  "nodes":  [{ "id": "…", "type": "And", "x": 32, "y": 48, "state": false, "definitionId": null }],
  "wires":  [{ "id": "…", "fromNodeId": "…", "toNodeId": "…", "toPin": 0, "fromPort": "out" }],
  "customIcs": []
}
```

The client rejects files > 2 MB and any payload not matching
`format === "logicdeck-circuit" && version === 1` before uploading; the server
re-validates everything server-side regardless.

## 7. Transport notes

- Client uses `withAutomaticReconnect([0, 1s, 2s, 4s, 8s, 15s, 30s])`.
- Identity (`ld.sid`, `ld.name`, `ld.color`) persists in `sessionStorage`, so a
  reconnect resumes the same session and name.
- On reconnect the editor re-joins the lobby and calls `EnterRoom` to force a
  fresh snapshot — pushes are not buffered across the disconnect.
