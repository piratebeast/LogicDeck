namespace LogicDeck.Core;

public interface ICollabClient
{
    Task RoomsChanged(IReadOnlyList<RoomSummary> rooms);
    Task RoomClosed(string circuitId);
    Task MemberJoined(MemberInfo member);
    Task MemberLeft(MemberInfo member);
    Task NodeAdded(NodeDto node, string bySessionId);
    Task NodeMoved(string nodeId, double x, double y, string bySessionId);
    Task NodesDeleted(IReadOnlyList<string> nodeIds, IReadOnlyList<string> wireIds, string bySessionId);
    Task IcPackaged(
        IcDefinitionDto definition,
        NodeDto node,
        IReadOnlyList<string> removedNodeIds,
        IReadOnlyList<string> removedWireIds,
        IReadOnlyList<WireDto> addedWires,
        string bySessionId);
    Task CircuitReplaced(CircuitSnapshot snapshot);
    Task WireAdded(WireDto wire, string bySessionId);
    Task WireRemoved(string wireId, string bySessionId);
    Task TitleChanged(string title);
    Task GridSizeChanged(int gridSize);
    Task InputToggled(string nodeId, bool state, string bySessionId);
    Task ButtonChanged(string nodeId, bool state, string bySessionId);
    Task RunningChanged(bool running);
    Task Signals(SignalFrame frame);
    Task CursorMoved(string sessionId, string name, string color, double x, double y);
}
