using LogicDeck.Core;
using Microsoft.AspNetCore.SignalR;

namespace LogicDeck.Web.Hubs;

public sealed class CollabHub : Hub<ICollabClient>
{
    private readonly LobbyService _lobby;
    private readonly CircuitStore _store;

    public CollabHub(LobbyService lobby, CircuitStore store)
    {
        _lobby = lobby;
        _store = store;
    }

    private string SessionId => Context.Items.TryGetValue("sid", out var sid)
        ? (string)sid!
        : throw new HubException("Your session has expired. Reload the page.");

    public async Task<SessionInfo> JoinLobby(string sessionId, string name)
    {
        if (!IdGen.IsValid(sessionId))
            sessionId = IdGen.New();
        var session = _lobby.Join(sessionId, name);
        Context.Items["sid"] = sessionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Lobby);
        return new SessionInfo(session.SessionId, session.Name, session.Color);
    }

    public LobbyState FetchRooms() => new(_store.Summaries());

    public async Task<RoomSummary> CreateRoom(string title, int gridSize)
    {
        var sid = SessionId;
        if (!GateSpec.IsValidGridSize(gridSize)) gridSize = GateSpec.DefaultGridSize;
        if (_store.Count >= GateSpec.MaxCircuits)
            throw new HubException("The server is at capacity. Try again later.");
        var circuit = _store.Create(GateSpec.CleanText(title, GateSpec.MaxTitleLength, "Untitled circuit"), gridSize, sid);
        var summary = RoomSummaryOf(circuit);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
        return summary;
    }

    public async Task DeleteRoom(string roomId)
    {
        var sid = SessionId;
        var circuit = RequireCircuit(roomId);
        lock (circuit.Lock)
        {
            if (circuit.OwnerSessionId != sid)
                throw new HubException("Only the circuit owner can delete it.");
        }
        if (_store.Delete(roomId) == null)
            throw new HubException("That circuit no longer exists.");
        await Clients.Group(HubGroups.Room(roomId)).RoomClosed(roomId);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
    }

    public async Task<CircuitSnapshot> ImportCircuit(CircuitImportDto data)
    {
        var (sid, circuit) = RequireMembership();
        CircuitSnapshot snapshot;
        lock (circuit.Lock)
        {
            var imported = BuildImportedCircuit(data);
            Record(circuit);
            circuit.Title = imported.Title;
            circuit.GridSize = imported.GridSize;
            circuit.Nodes.Clear();
            circuit.Wires.Clear();
            circuit.CustomIcs.Clear();
            foreach (var definition in imported.CustomIcs.Values) circuit.CustomIcs[definition.Id] = definition;
            foreach (var node in imported.Nodes.Values) circuit.Nodes[node.Id] = node;
            foreach (var wire in imported.Wires.Values) circuit.Wires[wire.Id] = wire;
            circuit.Touch();
            snapshot = SnapshotOf(circuit);
            if (circuit.Running) snapshot = snapshot with { Signals = circuit.Evaluate() };
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).CircuitReplaced(snapshot);
        await BroadcastHistoryAsync(circuit);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
        return snapshot;
    }

    public async Task<CircuitSnapshot> EnterRoom(string roomId)
    {
        var sid = SessionId;
        var circuit = RequireCircuit(roomId);
        CircuitSnapshot snapshot;
        MemberInfo member;
        bool newlyJoined;
        bool canUndo, canRedo;
        SignalFrame? frame = null;
        lock (circuit.Lock)
        {
            if (!circuit.Members.TryGetValue(sid, out member!))
            {
                if (circuit.Members.Count >= GateSpec.MaxMembersPerCircuit)
                    throw new HubException("This room is full.");
                var session = _lobby.Find(sid)!;
                member = new MemberInfo(sid, session.Name, session.Color);
                circuit.Members[sid] = member;
                newlyJoined = true;
            }
            else newlyJoined = false;
            circuit.Touch();
            snapshot = SnapshotOf(circuit);
            canUndo = circuit.History.CanUndo;
            canRedo = circuit.History.CanRedo;
            if (circuit.Running) frame = circuit.Evaluate();
        }
        snapshot = snapshot with { Signals = frame };

        Context.Items[HubGroups.RoomKey] = circuit.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Room(circuit.Id));
        await Clients.Caller.HistoryChanged(canUndo, canRedo);
        if (newlyJoined)
        {
            await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).MemberJoined(member);
            await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
        }
        return snapshot;
    }

    public async Task LeaveRoom()
    {
        if (Context.Items.TryGetValue(HubGroups.RoomKey, out var roomIdValue))
            await DetachRoom(SessionId, (string)roomIdValue!, broadcast: true);
    }

    public async Task<RoomSummary> RenameRoom(string title)
    {
        var (sid, circuit) = RequireMembership();
        title = GateSpec.CleanText(title, GateSpec.MaxTitleLength, "Untitled circuit");
        lock (circuit.Lock)
        {
            if (circuit.Title != title)
            {
                Record(circuit);
                circuit.Title = title;
                circuit.Touch();
            }
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).TitleChanged(title);
        await BroadcastHistoryAsync(circuit);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
        return RoomSummaryOf(circuit);
    }

    public async Task SetGridSize(int gridSize)
    {
        var (sid, circuit) = RequireMembership();
        if (!GateSpec.IsValidGridSize(gridSize)) gridSize = GateSpec.DefaultGridSize;
        lock (circuit.Lock)
        {
            if (circuit.GridSize != gridSize)
            {
                Record(circuit);
                circuit.GridSize = gridSize;
                circuit.Touch();
            }
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).GridSizeChanged(gridSize);
        await BroadcastHistoryAsync(circuit);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
    }

    public Task<NodeDto> AddNode(GateType type, double x, double y) => AddNodeCore(type, x, y, null);

    public Task<NodeDto> AddIcNode(GateType type, double x, double y, string definitionId)
    {
        if (type != GateType.Ic) throw new HubException("Only custom IC nodes use a definition id.");
        return AddNodeCore(type, x, y, definitionId);
    }

    private async Task<NodeDto> AddNodeCore(GateType type, double x, double y, string? definitionId)
    {
        var (sid, circuit) = RequireMembership();
        if (!GateSpec.All.Contains(type))
            throw new HubException("Unknown component type.");
        NodeDto dto;
        lock (circuit.Lock)
        {
            if (type == GateType.Ic && (definitionId == null || !circuit.CustomIcs.ContainsKey(definitionId)))
                throw new HubException("That custom IC is not available in this circuit.");
            if (type != GateType.Ic && definitionId != null)
                throw new HubException("Only custom IC nodes use a definition id.");
            if (circuit.Nodes.Count >= GateSpec.MaxNodesPerCircuit)
                throw new HubException($"A circuit can hold at most {GateSpec.MaxNodesPerCircuit} components.");
            var node = new NodeItem { Id = IdGen.New(), Type = type, X = x, Y = y, DefinitionId = definitionId };
            circuit.Snap(node);
            Record(circuit);
            circuit.Nodes[node.Id] = node;
            circuit.Touch();
            dto = NodeDtoOf(node);
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).NodeAdded(dto, sid);
        await BroadcastHistoryAsync(circuit);
        await BroadcastSignalsIfRunningAsync(circuit);
        return dto;
    }

    public async Task<DuplicationDto> DuplicateNodes(IReadOnlyList<string> nodeIds)
    {
        var (sid, circuit) = RequireMembership();
        List<NodeDto> nodes;
        List<WireDto> wires;
        lock (circuit.Lock)
        {
            var sources = nodeIds
                .Where(IdGen.IsValid)
                .Distinct()
                .Select(id => FindNode(circuit, id))
                .ToList();
            if (sources.Count == 0) return new DuplicationDto([], []);
            if (circuit.Nodes.Count + sources.Count > GateSpec.MaxNodesPerCircuit)
                throw new HubException($"A circuit can hold at most {GateSpec.MaxNodesPerCircuit} components.");

            Record(circuit);
            var offset = circuit.GridSize * 2;
            var copies = new Dictionary<string, NodeItem>();
            foreach (var source in sources)
            {
                var copy = new NodeItem
                {
                    Id = IdGen.New(), Type = source.Type,
                    X = source.X + offset, Y = source.Y + offset, State = source.State,
                    DefinitionId = source.DefinitionId
                };
                circuit.Snap(copy);
                circuit.Nodes[copy.Id] = copy;
                copies[source.Id] = copy;
            }

            var duplicatedWires = new List<Wire>();
            foreach (var wire in circuit.Wires.Values.ToList())
            {
                if (!copies.TryGetValue(wire.FromNodeId, out var from) || !copies.TryGetValue(wire.ToNodeId, out var to))
                    continue;
                var copy = new Wire
                {
                    Id = IdGen.New(), FromNodeId = from.Id, ToNodeId = to.Id, ToPin = wire.ToPin,
                    FromPort = wire.FromPort
                };
                circuit.Wires[copy.Id] = copy;
                duplicatedWires.Add(copy);
            }

            circuit.Touch();
            nodes = copies.Values.Select(NodeDtoOf).ToList();
            wires = duplicatedWires.Select(WireDtoOf).ToList();
        }

        var others = Clients.OthersInGroup(HubGroups.Room(circuit.Id));
        foreach (var node in nodes) await others.NodeAdded(node, sid);
        foreach (var wire in wires) await others.WireAdded(wire, sid);
        await BroadcastHistoryAsync(circuit);
        await BroadcastSignalsIfRunningAsync(circuit);
        return new DuplicationDto(nodes, wires);
    }

    public async Task<PackageIcResult> PackageIc(
        string name,
        IReadOnlyList<string> nodeIds,
        IReadOnlyDictionary<string, string> inputLabels,
        IReadOnlyDictionary<string, string> outputLabels)
    {
        var (sid, circuit) = RequireMembership();
        PackageIcResult result;
        lock (circuit.Lock)
        {
            var selected = nodeIds
                .Where(IdGen.IsValid)
                .Distinct()
                .Select(id => FindNode(circuit, id))
                .ToList();
            if (selected.Count == 0)
                throw new HubException("Select at least one component to package.");
            if (selected.Any(n => n.Type is not (GateType.And or GateType.Or or GateType.Not or GateType.Xor or GateType.Xnor or GateType.Nand or GateType.Nor)))
                throw new HubException("Package only combinational gates for now.");

            var selectedIds = selected.Select(n => n.Id).ToHashSet();
            var attached = circuit.Wires.Values
                .Where(w => selectedIds.Contains(w.FromNodeId) || selectedIds.Contains(w.ToNodeId))
                .ToList();
            var definition = new IcDefinition
            {
                Id = IdGen.New(),
                Name = GateSpec.CleanText(name, GateSpec.MaxNameLength, "Custom IC")
            };
            var cloneIds = new Dictionary<string, string>();
            foreach (var source in selected)
            {
                var cloneId = IdGen.New();
                cloneIds[source.Id] = cloneId;
                definition.Nodes.Add(new IcNode { Id = cloneId, Type = source.Type, State = source.State });
            }
            foreach (var wire in attached.Where(w => selectedIds.Contains(w.FromNodeId) && selectedIds.Contains(w.ToNodeId)))
            {
                definition.Wires.Add(new Wire
                {
                    Id = IdGen.New(), FromNodeId = cloneIds[wire.FromNodeId], ToNodeId = cloneIds[wire.ToNodeId],
                    ToPin = wire.ToPin, FromPort = wire.FromPort
                });
            }

            var inputIndexes = new Dictionary<string, int>();
            foreach (var source in selected)
            {
                for (var pin = 0; pin < GateSpec.InputPins(source.Type); pin++)
                {
                    var incoming = attached.FirstOrDefault(w => w.ToNodeId == source.Id && w.ToPin == pin);
                    if (incoming != null && selectedIds.Contains(incoming.FromNodeId)) continue;
                    var boundaryId = IdGen.New();
                    var key = $"{source.Id}:in{pin}";
                    definition.Nodes.Add(new IcNode { Id = boundaryId, Type = GateType.Input });
                    definition.Inputs.Add(new IcPin(boundaryId, CleanIcLabel(inputLabels, key, $"IN{definition.Inputs.Count + 1}")));
                    inputIndexes[key] = definition.Inputs.Count - 1;
                    definition.Wires.Add(new Wire
                    {
                        Id = IdGen.New(), FromNodeId = boundaryId, ToNodeId = cloneIds[source.Id], ToPin = pin
                    });
                }
            }

            var outputIndexes = new Dictionary<string, int>();
            foreach (var source in selected.Where(n => GateSpec.HasOutputPin(n.Type)))
            {
                var internalOutput = attached.FirstOrDefault(w =>
                    w.FromNodeId == source.Id && selectedIds.Contains(w.ToNodeId));
                if (internalOutput != null) continue;
                var boundaryId = IdGen.New();
                definition.Nodes.Add(new IcNode { Id = boundaryId, Type = GateType.Output });
                definition.Outputs.Add(new IcPin(boundaryId, CleanIcLabel(outputLabels, source.Id, $"OUT{definition.Outputs.Count + 1}")));
                outputIndexes[source.Id] = definition.Outputs.Count - 1;
                definition.Wires.Add(new Wire
                {
                    Id = IdGen.New(), FromNodeId = cloneIds[source.Id], ToNodeId = boundaryId, ToPin = 0
                });
            }
            if (definition.Outputs.Count == 0)
                throw new HubException("The selected circuit needs at least one output pin.");

            Record(circuit);
            var minX = selected.Min(n => n.X);
            var minY = selected.Min(n => n.Y);
            var icNode = new NodeItem
            {
                Id = IdGen.New(), Type = GateType.Ic, DefinitionId = definition.Id, X = minX, Y = minY
            };
            circuit.Snap(icNode);
            var (removedNodes, removedWires) = circuit.RemoveNodesAndAttachedWires(selectedIds);
            circuit.Nodes[icNode.Id] = icNode;
            var addedWires = new List<Wire>();
            foreach (var old in attached.Where(w => !selectedIds.Contains(w.FromNodeId) || !selectedIds.Contains(w.ToNodeId)))
            {
                if (selectedIds.Contains(old.ToNodeId))
                {
                    var inputKey = $"{old.ToNodeId}:in{old.ToPin}";
                    if (!inputIndexes.TryGetValue(inputKey, out var inputIndex)) continue;
                    addedWires.Add(new Wire
                    {
                        Id = IdGen.New(), FromNodeId = old.FromNodeId, ToNodeId = icNode.Id,
                        ToPin = inputIndex, FromPort = old.FromPort
                    });
                }
                else
                {
                    if (!outputIndexes.TryGetValue(old.FromNodeId, out var outputIndex)) continue;
                    addedWires.Add(new Wire
                    {
                        Id = IdGen.New(), FromNodeId = icNode.Id, ToNodeId = old.ToNodeId,
                        ToPin = old.ToPin, FromPort = $"out{outputIndex}"
                    });
                }
            }
            foreach (var wire in addedWires) circuit.Wires[wire.Id] = wire;
            circuit.CustomIcs[definition.Id] = definition;
            circuit.Touch();
            result = new PackageIcResult(
                IcDefinitionDtoOf(definition), NodeDtoOf(icNode), removedNodes, removedWires,
                addedWires.Select(WireDtoOf).ToList());
        }

        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).IcPackaged(
            result.Definition, result.Node, result.RemovedNodeIds, result.RemovedWireIds, result.AddedWires, sid);
        await BroadcastHistoryAsync(circuit);
        await BroadcastSignalsIfRunningAsync(circuit);
        return result;
    }

    public async Task<TidyLayoutDto> TidyLayout(IReadOnlyList<string> nodeIds)
    {
        var (sid, circuit) = RequireMembership();
        List<NodeDto> moved;
        lock (circuit.Lock)
        {
            var selected = nodeIds
                .Where(IdGen.IsValid)
                .Distinct()
                .Select(id => circuit.Nodes.GetValueOrDefault(id))
                .Where(n => n != null)
                .Select(n => n!)
                .ToList();
            if (selected.Count == 0) selected = circuit.Nodes.Values.ToList();
            if (selected.Count < 2) return new TidyLayoutDto(selected.Select(NodeDtoOf).ToList());

            Record(circuit);
            var selectedIds = selected.Select(n => n.Id).ToHashSet();
            var levels = selected.ToDictionary(n => n.Id, _ => 0);
            for (var pass = 0; pass < selected.Count; pass++)
            {
                foreach (var wire in circuit.Wires.Values)
                {
                    if (!selectedIds.Contains(wire.FromNodeId) || !selectedIds.Contains(wire.ToNodeId)) continue;
                    levels[wire.ToNodeId] = Math.Min(selected.Count, Math.Max(levels[wire.ToNodeId], levels[wire.FromNodeId] + 1));
                }
            }
            var groups = selected.GroupBy(n => levels[n.Id]).OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                var ordered = group.OrderBy(n => n.Y).ThenBy(n => n.X).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var node = ordered[i];
                    node.X = 32 + levels[node.Id] * 144;
                    node.Y = 32 + i * 112;
                    circuit.Snap(node);
                }
            }
            circuit.Touch();
            moved = selected.Select(NodeDtoOf).ToList();
        }
        foreach (var node in moved)
            await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).NodeMoved(node.Id, node.X, node.Y, sid);
        await BroadcastHistoryAsync(circuit);
        return new TidyLayoutDto(moved);
    }

    public TruthTableDto ComputeTruthTable()
    {
        var (_, circuit) = RequireMembership();
        lock (circuit.Lock)
        {
            var inputCount = circuit.Nodes.Values.Count(n => n.Type == GateType.Input);
            if (inputCount == 0)
                throw new HubException("Add at least one input switch to build a truth table.");
            if (!circuit.Nodes.Values.Any(n => n.Type == GateType.Output))
                throw new HubException("Add at least one output lamp to build a truth table.");
            if (inputCount > Simulator.MaxTruthTableInputs)
                throw new HubException($"A truth table supports up to {Simulator.MaxTruthTableInputs} input switches.");
            return Simulator.ComputeTruthTable(circuit.Nodes.Values.ToList(), circuit.Wires.Values.ToList(), circuit.CustomIcs);
        }
    }

    public async Task<CircuitSnapshot> Undo()
    {
        var (_, circuit) = RequireMembership();
        CircuitSnapshot snapshot;
        lock (circuit.Lock)
        {
            var previous = circuit.History.Undo(circuit.Capture())
                ?? throw new HubException("There is nothing left to undo.");
            circuit.Restore(previous);
            snapshot = SnapshotOf(circuit);
            if (circuit.Running) snapshot = snapshot with { Signals = circuit.Evaluate() };
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).CircuitReplaced(snapshot);
        await BroadcastHistoryAsync(circuit);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
        return snapshot;
    }

    public async Task<CircuitSnapshot> Redo()
    {
        var (_, circuit) = RequireMembership();
        CircuitSnapshot snapshot;
        lock (circuit.Lock)
        {
            var next = circuit.History.Redo(circuit.Capture())
                ?? throw new HubException("There is nothing left to redo.");
            circuit.Restore(next);
            snapshot = SnapshotOf(circuit);
            if (circuit.Running) snapshot = snapshot with { Signals = circuit.Evaluate() };
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).CircuitReplaced(snapshot);
        await BroadcastHistoryAsync(circuit);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
        return snapshot;
    }

    public async Task MoveNode(string nodeId, double x, double y)
    {
        var (sid, circuit) = RequireMembership();
        lock (circuit.Lock)
        {
            var node = FindNode(circuit, nodeId);
            Record(circuit);
            node.X = x; node.Y = y;
            circuit.Snap(node);
            circuit.Touch();
            (x, y) = (node.X, node.Y);
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).NodeMoved(nodeId, x, y, sid);
        await BroadcastHistoryAsync(circuit);
    }

    public async Task DeleteNodes(IReadOnlyList<string> nodeIds)
    {
        var (sid, circuit) = RequireMembership();
        List<string> nodesRemoved;
        List<string> wiresRemoved;
        lock (circuit.Lock)
        {
            var valid = nodeIds.Where(IdGen.IsValid).ToList();
            if (valid.Any(circuit.Nodes.ContainsKey)) Record(circuit);
            (nodesRemoved, wiresRemoved) = circuit.RemoveNodesAndAttachedWires(valid);
            circuit.Touch();
        }
        if (nodesRemoved.Count == 0 && wiresRemoved.Count == 0) return;
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).NodesDeleted(nodesRemoved, wiresRemoved, sid);
        await BroadcastHistoryAsync(circuit);
    }

    public Task<WireDto> AddWire(string fromNodeId, string toNodeId, int toPin) => AddWireCore(fromNodeId, toNodeId, toPin, 0);

    public Task<WireDto> AddIcWire(string fromNodeId, string toNodeId, int toPin, int fromPin) =>
        AddWireCore(fromNodeId, toNodeId, toPin, fromPin);

    private async Task<WireDto> AddWireCore(string fromNodeId, string toNodeId, int toPin, int fromPin)
    {
        var (sid, circuit) = RequireMembership();
        WireDto dto;
        lock (circuit.Lock)
        {
            var from = FindNode(circuit, fromNodeId);
            var to = FindNode(circuit, toNodeId);
            var fromPort = from.Type is GateType.Ic or GateType.FlopD ? $"out{fromPin}" : "out";
            ValidateWireTarget(circuit, from, to, toPin, fromPort);

            Record(circuit);
            var wire = new Wire { Id = IdGen.New(), FromNodeId = fromNodeId, ToNodeId = toNodeId, ToPin = toPin, FromPort = fromPort };
            circuit.Wires[wire.Id] = wire;
            circuit.Touch();
            dto = WireDtoOf(wire);
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).WireAdded(dto, sid);
        await BroadcastHistoryAsync(circuit);
        await BroadcastSignalsIfRunningAsync(circuit);
        return dto;
    }

    private static void ValidateWireTarget(Circuit circuit, NodeItem from, NodeItem to, int toPin, string fromPort)
    {
        if (!GateSpec.HasOutputPin(from.Type))
            throw new HubException($"{from.Type} has no output pin.");
        IcDefinition? definition = null;
        if (from.Type == GateType.Ic)
        {
            if (!circuit.CustomIcs.TryGetValue(from.DefinitionId ?? "", out var found))
                throw new HubException("That custom IC is not available in this circuit.");
            definition = found;
        }
        if (from.Type == GateType.Ic && (fromPort == "out" || !int.TryParse(fromPort[3..], out var outputPin) || outputPin < 0 || outputPin >= definition!.Outputs.Count))
            throw new HubException("That custom IC output does not exist.");
        if (from.Type == GateType.FlopD && fromPort is not ("out0" or "out1"))
            throw new HubException("That flip-flop output does not exist. Q=out0, Q̄=out1.");
        if (from.Type is not (GateType.Ic or GateType.FlopD) && fromPort != "out")
            throw new HubException("This component has no such output pin.");
        if (toPin < 0 || toPin >= GateSpec.InputPins(circuit, to))
            throw new HubException($"{to.Type} has no such input pin.");
        if (from.Id == to.Id)
            throw new HubException("A component cannot feed itself.");
        if (circuit.Wires.Values.Any(w => w.ToNodeId == to.Id && w.ToPin == toPin))
            throw new HubException("That input already has a wire.");
        if (circuit.Wires.Values.Any(w =>
                w.FromNodeId == from.Id && w.FromPort == fromPort && w.ToNodeId == to.Id && w.ToPin == toPin))
            throw new HubException("These pins are already wired.");
    }

    public async Task RemoveWire(string wireId)
    {
        var (sid, circuit) = RequireMembership();
        bool removed = false;
        lock (circuit.Lock)
        {
            if (circuit.Wires.ContainsKey(wireId))
            {
                Record(circuit);
                removed = circuit.Wires.Remove(wireId);
                if (removed) circuit.Touch();
            }
        }
        if (!removed) return;
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).WireRemoved(wireId, sid);
        await BroadcastHistoryAsync(circuit);
        await BroadcastSignalsIfRunningAsync(circuit);
    }

    public async Task<bool> SetButton(string nodeId, bool pressed)
    {
        var (sid, circuit) = RequireMembership();
        bool state;
        lock (circuit.Lock)
        {
            var node = FindNode(circuit, nodeId);
            if (node.Type != GateType.Button)
                throw new HubException("Only push buttons can be pressed.");
            node.State = pressed;
            state = pressed;
            circuit.Touch();
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).ButtonChanged(nodeId, state, sid);
        await BroadcastSignalsIfRunningAsync(circuit);
        return state;
    }

    public async Task<bool> ToggleInput(string nodeId)
    {
        var (sid, circuit) = RequireMembership();
        bool state;
        lock (circuit.Lock)
        {
            var node = FindNode(circuit, nodeId);
            if (node.Type != GateType.Input)
                throw new HubException("Only input switches can be toggled.");
            node.State = !node.State;
            state = node.State;
            circuit.Touch();
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).InputToggled(nodeId, state, sid);
        await BroadcastSignalsIfRunningAsync(circuit);
        return state;
    }

    public async Task SetRunning(bool running)
    {
        var (sid, circuit) = RequireMembership();
        SignalFrame? frame = null;
        lock (circuit.Lock)
        {
            circuit.Running = running;
            circuit.Touch();
            if (running) frame = circuit.Evaluate();
        }
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id)).RunningChanged(running);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
        if (frame != null)
            await Clients.Group(HubGroups.Room(circuit.Id)).Signals(frame);
    }

    public async Task SendCursor(double x, double y)
    {
        var (sid, circuit) = PeekMembership();
        if (sid == null || circuit == null) return;
        MemberInfo? member;
        lock (circuit.Lock) circuit.Members.TryGetValue(sid, out member);
        if (member == null) return;
        await Clients.OthersInGroup(HubGroups.Room(circuit.Id))
            .CursorMoved(sid, member.Name, member.Color, Math.Round(x, 1), Math.Round(y, 1));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("sid", out var sidValue) && sidValue is string sid)
        {
            if (Context.Items.TryGetValue(HubGroups.RoomKey, out var roomValue) && roomValue is string roomId)
                await DetachRoom(sid, roomId, broadcast: false);
            _lobby.MarkDisconnected(sid);
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task DetachRoom(string sid, string roomId, bool broadcast)
    {
        Context.Items.Remove(HubGroups.RoomKey);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Room(roomId));
        if (!broadcast) return;
        var circuit = _store.Find(roomId);
        if (circuit == null) return;
        MemberInfo? removed = null;
        lock (circuit.Lock)
        {
            if (circuit.Members.Remove(sid, out var m))
            {
                removed = m;
                circuit.Touch();
            }
        }
        if (removed == null) return;
        await Clients.OthersInGroup(HubGroups.Room(roomId)).MemberLeft(removed);
        await Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
    }

    private async Task BroadcastSignalsIfRunningAsync(Circuit circuit)
    {
        SignalFrame? frame;
        lock (circuit.Lock) frame = circuit.Running ? circuit.Evaluate() : null;
        if (frame != null)
            await Clients.Group(HubGroups.Room(circuit.Id)).Signals(frame);
    }

    // Call under lock(circuit.Lock) immediately before a history-worthy mutation,
    // so the pre-edit state can be restored by Undo.
    private static void Record(Circuit circuit) => circuit.History.Record(circuit.Capture());

    private async Task BroadcastHistoryAsync(Circuit circuit)
    {
        bool canUndo, canRedo;
        lock (circuit.Lock)
        {
            canUndo = circuit.History.CanUndo;
            canRedo = circuit.History.CanRedo;
        }
        await Clients.Group(HubGroups.Room(circuit.Id)).HistoryChanged(canUndo, canRedo);
    }

    private Circuit RequireCircuit(string roomId)
    {
        if (!IdGen.IsValid(roomId))
            throw new HubException("Invalid circuit id.");
        return _store.Find(roomId) ?? throw new HubException("That circuit no longer exists.");
    }

    private (string? Sid, Circuit? Circuit) PeekMembership()
    {
        if (!Context.Items.TryGetValue("sid", out var sidValue) || sidValue is not string sid)
            return (null, null);
        if (!Context.Items.TryGetValue(HubGroups.RoomKey, out var roomValue) || roomValue is not string roomId)
            return (null, null);
        var circuit = _store.Find(roomId);
        return circuit == null ? (null, null) : ((string? Sid, Circuit? Circuit))(sid, circuit);
    }

    private (string Sid, Circuit Circuit) RequireMembership() =>
        PeekMembership() switch
        {
            ({ } sid, { } circuit) => (sid, circuit),
            _ when Context.Items.ContainsKey("sid") =>
                throw new HubException("This circuit was closed."),
            _ => throw new HubException("Your session has expired. Reload the page.")
        };

    private static NodeItem FindNode(Circuit circuit, string nodeId) =>
        circuit.Nodes.GetValueOrDefault(nodeId) ?? throw new HubException("That component no longer exists.");

    private static Circuit BuildImportedCircuit(CircuitImportDto data)
    {
        if (data.Version != 1) throw new HubException("Unsupported circuit file version.");
        if (!GateSpec.IsValidGridSize(data.GridSize)) throw new HubException("Invalid circuit grid size.");
        if (data.Nodes is null || data.Nodes.Count > GateSpec.MaxNodesPerCircuit)
            throw new HubException("That circuit file has too many components.");
        if (data.Wires is null || data.Wires.Count > GateSpec.MaxNodesPerCircuit * 4)
            throw new HubException("That circuit file has too many wires.");

        var staged = new Circuit
        {
            Id = "import",
            Title = GateSpec.CleanText(data.Title, GateSpec.MaxTitleLength, "Untitled circuit"),
            GridSize = data.GridSize
        };
        var definitions = new Dictionary<string, IcDefinition>();
        foreach (var dto in data.CustomIcs ?? [])
        {
            if (!IdGen.IsValid(dto.Id) || definitions.ContainsKey(dto.Id))
                throw new HubException("That circuit file contains an invalid custom IC id.");
            var definition = new IcDefinition
            {
                Id = dto.Id,
                Name = GateSpec.CleanText(dto.Name, GateSpec.MaxNameLength, "Custom IC")
            };
            var internalIds = new HashSet<string>();
            foreach (var node in dto.Nodes ?? [])
            {
                if (!IdGen.IsValid(node.Id) || !internalIds.Add(node.Id) || node.Type is GateType.Ic or GateType.Button or GateType.CLK or GateType.Seg7 or GateType.FlopD)
                    throw new HubException("That circuit file contains an invalid custom IC node.");
                definition.Nodes.Add(new IcNode { Id = node.Id, Type = node.Type, State = node.State });
            }
            var inputPins = new HashSet<string>();
            foreach (var pin in dto.Inputs ?? [])
            {
                if (!IdGen.IsValid(pin.Id) || !inputPins.Add(pin.Id) ||
                    !definition.Nodes.Any(n => n.Id == pin.Id && n.Type == GateType.Input))
                    throw new HubException("That circuit file contains an invalid custom IC input.");
                definition.Inputs.Add(new IcPin(pin.Id, GateSpec.CleanText(pin.Label, GateSpec.MaxNameLength, $"IN{definition.Inputs.Count + 1}")));
            }
            var outputPins = new HashSet<string>();
            foreach (var pin in dto.Outputs ?? [])
            {
                if (!IdGen.IsValid(pin.Id) || !outputPins.Add(pin.Id) ||
                    !definition.Nodes.Any(n => n.Id == pin.Id && n.Type == GateType.Output))
                    throw new HubException("That circuit file contains an invalid custom IC output.");
                definition.Outputs.Add(new IcPin(pin.Id, GateSpec.CleanText(pin.Label, GateSpec.MaxNameLength, $"OUT{definition.Outputs.Count + 1}")));
            }
            var internalCircuit = new Circuit { Id = $"internal-{dto.Id}", GridSize = data.GridSize };
            foreach (var node in definition.Nodes)
                internalCircuit.Nodes[node.Id] = new NodeItem { Id = node.Id, Type = node.Type, State = node.State };
            foreach (var wire in dto.Wires ?? [])
            {
                if (!IdGen.IsValid(wire.Id) || internalCircuit.Wires.ContainsKey(wire.Id) ||
                    !internalCircuit.Nodes.TryGetValue(wire.FromNodeId, out var from) ||
                    !internalCircuit.Nodes.TryGetValue(wire.ToNodeId, out var to))
                    throw new HubException("That circuit file contains an invalid custom IC wire.");
                ValidateWireTarget(internalCircuit, from, to, wire.ToPin, wire.FromPort ?? "out");
                var copy = new Wire
                {
                    Id = wire.Id, FromNodeId = wire.FromNodeId, ToNodeId = wire.ToNodeId,
                    ToPin = wire.ToPin, FromPort = wire.FromPort ?? "out"
                };
                internalCircuit.Wires[copy.Id] = copy;
                definition.Wires.Add(copy);
            }
            if (definition.Inputs.Count == 0 || definition.Outputs.Count == 0)
                throw new HubException("A custom IC needs at least one input and one output.");
            definitions[definition.Id] = definition;
            staged.CustomIcs[definition.Id] = definition;
        }

        var nodeIds = new HashSet<string>();
        foreach (var dto in data.Nodes)
        {
            if (!IdGen.IsValid(dto.Id) || !nodeIds.Add(dto.Id) || !GateSpec.All.Contains(dto.Type) ||
                !double.IsFinite(dto.X) || !double.IsFinite(dto.Y))
                throw new HubException("That circuit file contains an invalid component.");
            if (dto.Type == GateType.Ic && (dto.DefinitionId == null || !definitions.ContainsKey(dto.DefinitionId)))
                throw new HubException("That circuit file references a missing custom IC.");
            staged.Nodes[dto.Id] = new NodeItem
            {
                Id = dto.Id, Type = dto.Type, X = dto.X, Y = dto.Y, State = dto.State,
                DefinitionId = dto.DefinitionId
            };
            staged.Snap(staged.Nodes[dto.Id]);
        }
        foreach (var wire in data.Wires)
        {
            if (!IdGen.IsValid(wire.Id) || staged.Wires.ContainsKey(wire.Id) ||
                !staged.Nodes.TryGetValue(wire.FromNodeId, out var from) ||
                !staged.Nodes.TryGetValue(wire.ToNodeId, out var to))
                throw new HubException("That circuit file contains an invalid wire.");
            ValidateWireTarget(staged, from, to, wire.ToPin, wire.FromPort ?? "out");
            var copy = new Wire
            {
                Id = wire.Id, FromNodeId = wire.FromNodeId, ToNodeId = wire.ToNodeId,
                ToPin = wire.ToPin, FromPort = wire.FromPort ?? "out"
            };
            staged.Wires[copy.Id] = copy;
        }
        return staged;
    }

    private static string CleanIcLabel(IReadOnlyDictionary<string, string> labels, string key, string fallback) =>
        GateSpec.CleanText(labels.TryGetValue(key, out var value) ? value : null, GateSpec.MaxNameLength, fallback);

    private static IcDefinitionDto IcDefinitionDtoOf(IcDefinition definition) => CircuitMapping.ToIcDto(definition);

    private static NodeDto NodeDtoOf(NodeItem n) => CircuitMapping.ToNodeDto(n);
    private static WireDto WireDtoOf(Wire w) => CircuitMapping.ToWireDto(w);
    private static RoomSummary RoomSummaryOf(Circuit c) { lock (c.Lock) return CircuitStore.ToSummary(c); }
    private static CircuitSnapshot SnapshotOf(Circuit c) => new(
        c.Id,
        c.Title,
        c.GridSize,
        c.Running,
        c.Nodes.Values.Select(NodeDtoOf).ToList(),
        c.Wires.Values.Select(WireDtoOf).ToList(),
        c.Members.Values.ToList(),
        CustomIcs: c.CustomIcs.Values.Select(IcDefinitionDtoOf).ToList());
}
