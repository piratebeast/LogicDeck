namespace LogicDeck.Core;

public record SessionInfo(string SessionId, string Name, string Color);

public record RoomSummary(
    string Id,
    string Title,
    int GridSize,
    DateTime CreatedUtc,
    bool Running,
    int NodeCount,
    IReadOnlyList<MemberInfo> Members,
    string? OwnerSessionId = null);

public record NodeDto(string Id, GateType Type, double X, double Y, bool State, string? DefinitionId = null);

public record WireDto(string Id, string FromNodeId, string ToNodeId, int ToPin, string? FromPort = null);

public record IcPinDto(string Id, string Label);

public record IcNodeDto(string Id, GateType Type, bool State);

public record IcDefinitionDto(
    string Id,
    string Name,
    IReadOnlyList<IcPinDto> Inputs,
    IReadOnlyList<IcPinDto> Outputs,
    IReadOnlyList<IcNodeDto> Nodes,
    IReadOnlyList<WireDto> Wires);

public record CircuitImportDto(
    int Version,
    string Title,
    int GridSize,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<WireDto> Wires,
    IReadOnlyList<IcDefinitionDto> CustomIcs);

public record DuplicationDto(IReadOnlyList<NodeDto> Nodes, IReadOnlyList<WireDto> Wires);

public record TidyLayoutDto(IReadOnlyList<NodeDto> Nodes);

public record PackageIcResult(
    IcDefinitionDto Definition,
    NodeDto Node,
    IReadOnlyList<string> RemovedNodeIds,
    IReadOnlyList<string> RemovedWireIds,
    IReadOnlyList<WireDto> AddedWires);

public record CircuitSnapshot(
    string CircuitId,
    string Title,
    int GridSize,
    bool Running,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<WireDto> Wires,
    IReadOnlyList<MemberInfo> Members,
    SignalFrame? Signals = null,
    IReadOnlyList<IcDefinitionDto>? CustomIcs = null);

public record SignalFrame(
    IReadOnlyDictionary<string, bool> Wires,
    IReadOnlyList<string> Unstable,
    IReadOnlyDictionary<string, bool> Outputs,
    IReadOnlyDictionary<string, bool> Sources,
    IReadOnlyDictionary<string, int> Displays,
    IReadOnlyDictionary<string, bool> Flops);

public record TruthTableVar(string Id, string Label);

public record TruthTableRow(IReadOnlyList<bool> Inputs, IReadOnlyList<bool> Outputs);

public record TruthTableDto(
    IReadOnlyList<TruthTableVar> Inputs,
    IReadOnlyList<TruthTableVar> Outputs,
    IReadOnlyList<TruthTableRow> Rows);

public record LobbyState(IReadOnlyList<RoomSummary> Rooms);
