namespace LogicDeck.Core;

/// <summary>
/// Shared conversions between the live domain model and the wire/tool DTOs.
/// Centralised so persistence, history snapshots and the hub all speak the same
/// shapes.
/// </summary>
public static class CircuitMapping
{
    public static NodeDto ToNodeDto(NodeItem n) => new(n.Id, n.Type, n.X, n.Y, n.State, n.DefinitionId);

    public static WireDto ToWireDto(Wire w) => new(w.Id, w.FromNodeId, w.ToNodeId, w.ToPin, w.FromPort);

    public static IcDefinitionDto ToIcDto(IcDefinition d) => new(
        d.Id,
        d.Name,
        d.Inputs.Select(p => new IcPinDto(p.Id, p.Label)).ToList(),
        d.Outputs.Select(p => new IcPinDto(p.Id, p.Label)).ToList(),
        d.Nodes.Select(n => new IcNodeDto(n.Id, n.Type, n.State)).ToList(),
        d.Wires.Select(ToWireDto).ToList());

    public static IcDefinition ToIcDefinition(IcDefinitionDto dto)
    {
        var definition = new IcDefinition { Id = dto.Id, Name = dto.Name };
        foreach (var node in dto.Nodes)
            definition.Nodes.Add(new IcNode { Id = node.Id, Type = node.Type, State = node.State });
        foreach (var pin in dto.Inputs)
            definition.Inputs.Add(new IcPin(pin.Id, pin.Label));
        foreach (var pin in dto.Outputs)
            definition.Outputs.Add(new IcPin(pin.Id, pin.Label));
        foreach (var wire in dto.Wires)
            definition.Wires.Add(new Wire
            {
                Id = wire.Id,
                FromNodeId = wire.FromNodeId,
                ToNodeId = wire.ToNodeId,
                ToPin = wire.ToPin,
                FromPort = string.IsNullOrEmpty(wire.FromPort) ? "out" : wire.FromPort
            });
        return definition;
    }
}
