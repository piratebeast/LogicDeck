namespace LogicDeck.Core;

public enum GateType
{
    Input,
    Output,
    And,
    Or,
    Not,
    Xor,
    Xnor,
    Nand,
    Nor,
    CLK,
    Button,
    Ic,
    Seg7,
    FlopD
}

public static class GateSpec
{
    public static readonly GateType[] All =
    [
        GateType.Input, GateType.Output, GateType.And, GateType.Or,
        GateType.Not, GateType.Xor, GateType.Xnor, GateType.Nand, GateType.Nor, GateType.CLK, GateType.Button, GateType.Ic, GateType.Seg7, GateType.FlopD
    ];

    public const int MaxNodesPerCircuit = 400;
    public const int MaxMembersPerCircuit = 8;
    public const int MaxCircuits = 200;
    public const int MaxNameLength = 24;
    public const int MaxTitleLength = 48;

    public static readonly int[] GridSizes = [8, 16, 24, 32];
    public const int DefaultGridSize = 16;

    public const int DisconnectGraceSeconds = 15;
    public const int IdleCircuitMinutes = 10;

    // bit0..bit6 = segments a..g; indexed by the BCD nibble read from the Seg7 pins.
    public static readonly byte[] Seg7Masks =
    [
        0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07,
        0x7F, 0x6F, 0x77, 0x7C, 0x39, 0x5E, 0x79, 0x71
    ];

    public static int InputPins(GateType type) => type switch
    {
        GateType.And or GateType.Or or GateType.Xor or GateType.Xnor or GateType.Nand or GateType.Nor => 2,
        GateType.Not or GateType.Output => 1,
        GateType.Seg7 => 4,
        GateType.FlopD => 2, // in0 = D, in1 = CLK
        GateType.CLK or GateType.Button or GateType.Ic => 0,
        _ => 0
    };

    public static int InputPins(Circuit circuit, NodeItem node) =>
        node.Type == GateType.Ic && circuit.CustomIcs.TryGetValue(node.DefinitionId ?? "", out var definition)
            ? definition.Inputs.Count
            : InputPins(node.Type);

    public static bool HasOutputPin(GateType type) => type is not (GateType.Output or GateType.Seg7);

    public static bool IsValidGridSize(int size) => GridSizes.Contains(size);

    public static string CleanText(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value.Where(c => !char.IsControl(c)).ToArray();
        var cleaned = new string(chars);
        cleaned = Regex.Replace(cleaned.Trim(), @"\s+", " ");
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength].TrimEnd();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}

public sealed class NodeItem
{
    public required string Id { get; init; }
    public required GateType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool State { get; set; }
    public string? DefinitionId { get; set; }

    // FlopD only: stored Q bit (State) and the previous settled clock level,
    // kept on the node so flop memory survives across evaluations and reloads.
    [JsonIgnore]
    public bool ClkPrev { get; set; }
}

public sealed class IcNode
{
    public required string Id { get; init; }
    public required GateType Type { get; init; }
    public bool State { get; set; }
}

public sealed record IcPin(string Id, string Label);

public sealed class IcDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<IcPin> Inputs { get; } = [];
    public List<IcPin> Outputs { get; } = [];
    public List<IcNode> Nodes { get; } = [];
    public List<Wire> Wires { get; } = [];
}

public sealed class Wire
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public required int ToPin { get; init; }
    public string FromPort { get; init; } = "out";
}

public sealed record MemberInfo(string SessionId, string Name, string Color);

public sealed class Circuit
{
    public required string Id { get; init; }
    public string Title { get; set; } = "Untitled circuit";
    public int GridSize { get; set; } = GateSpec.DefaultGridSize;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    public bool Running { get; set; }
    public string? OwnerSessionId { get; init; }

    public Dictionary<string, NodeItem> Nodes { get; } = new();
    public Dictionary<string, Wire> Wires { get; } = new();
    public Dictionary<string, IcDefinition> CustomIcs { get; } = new();
    public Dictionary<string, MemberInfo> Members { get; } = new();

    [JsonIgnore]
    public object Lock { get; } = new();

    [JsonIgnore]
    public CircuitHistory History { get; } = new();

    // Raised after any mutation so the store can auto-save changed circuits.
    // Not serialized (events are invisible to System.Text.Json).
    public event Action? Touched;

    public void Touch()
    {
        LastActivityUtc = DateTime.UtcNow;
        Touched?.Invoke();
    }

    /// <summary>Snapshots the editable content (title, grid, nodes, wires, ICs) for undo.</summary>
    public CircuitState Capture() => new(
        Title,
        GridSize,
        Nodes.Values.Select(CircuitMapping.ToNodeDto).ToList(),
        Wires.Values.Select(CircuitMapping.ToWireDto).ToList(),
        CustomIcs.Values.Select(CircuitMapping.ToIcDto).ToList());

    /// <summary>Replaces the editable content of the circuit with a captured state.</summary>
    public void Restore(CircuitState state)
    {
        Title = state.Title;
        GridSize = state.GridSize;
        Nodes.Clear();
        Wires.Clear();
        CustomIcs.Clear();
        foreach (var node in state.Nodes)
            Nodes[node.Id] = new NodeItem
            {
                Id = node.Id,
                Type = node.Type,
                X = node.X,
                Y = node.Y,
                State = node.State,
                DefinitionId = node.DefinitionId
            };
        foreach (var wire in state.Wires)
            Wires[wire.Id] = new Wire
            {
                Id = wire.Id,
                FromNodeId = wire.FromNodeId,
                ToNodeId = wire.ToNodeId,
                ToPin = wire.ToPin,
                FromPort = string.IsNullOrEmpty(wire.FromPort) ? "out" : wire.FromPort
            };
        foreach (var definition in state.CustomIcs)
            CustomIcs[definition.Id] = CircuitMapping.ToIcDefinition(definition);
        Touch();
    }

    public void Snap(NodeItem node)
    {
        var g = GridSize;
        node.X = Math.Round(node.X / g) * g;
        node.Y = Math.Round(node.Y / g) * g;
    }

    public (List<string> NodesRemoved, List<string> WiresRemoved) RemoveNodesAndAttachedWires(ICollection<string> nodeIds)
    {
        var nodesRemoved = new List<string>();
        var wiresRemoved = new List<string>();
        var idSet = nodeIds.ToHashSet();
        foreach (var id in idSet)
        {
            if (Nodes.Remove(id)) nodesRemoved.Add(id);
        }
        foreach (var key in Wires.Keys.ToList())
        {
            if (Wires[key] is { } w && (idSet.Contains(w.FromNodeId) || idSet.Contains(w.ToNodeId)))
            {
                Wires.Remove(key);
                wiresRemoved.Add(key);
            }
        }
        return (nodesRemoved, wiresRemoved);
    }

    public SignalFrame Evaluate()
    {
        var nodesCopy = Nodes.Values.ToList();
        var wiresCopy = Wires.Values.ToList();
        return Simulator.Evaluate(nodesCopy, wiresCopy, CustomIcs);
    }
}

public static partial class IdGen
{
    [GeneratedRegex("^[a-z0-9]{6,40}$")]
    private static partial Regex IdPattern();

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string New()
    {
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && IdPattern().IsMatch(id);
}
