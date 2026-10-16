using System.Text.Json;

namespace LogicDeck.Core;

/// <summary>
/// On-disk shape of a saved circuit. Deliberately a versioned DTO (not the live
/// <see cref="Circuit"/>) so the storage format can evolve independently.
/// </summary>
public sealed record PersistedCircuit(
    int Version,
    string Id,
    string Title,
    int GridSize,
    DateTime CreatedUtc,
    DateTime LastActivityUtc,
    string? OwnerSessionId,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<WireDto> Wires,
    IReadOnlyList<IcDefinitionDto> CustomIcs);

/// <summary>
/// Reads and writes circuits as one JSON file each. The server is the only writer
/// and every save goes through a temp file + atomic move, so a crash mid-write can
/// never corrupt a previously saved circuit. Every file is re-validated on load and
/// silently skipped if it does not pass, so a corrupt file can never break startup.
/// </summary>
public sealed class CircuitPersistence
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public CircuitPersistence(string directory) => _directory = Path.GetFullPath(directory);

    public string Directory => _directory;

    public IReadOnlyList<Circuit> LoadAll()
    {
        if (!System.IO.Directory.Exists(_directory)) return [];

        var restored = new List<Circuit>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<PersistedCircuit>(System.IO.File.ReadAllText(file), Json);
                if (dto is not null && TryBuild(dto, out var circuit))
                    restored.Add(circuit);
            }
            catch (Exception)
            {
                // One unreadable or malformed file must never stop the server starting.
            }
        }
        return restored;
    }

    public string Serialize(Circuit circuit) => JsonSerializer.Serialize(ToPersisted(circuit), Json);

    public void Write(string id, string json)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var path = PathFor(id);
        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, json);
        System.IO.File.Move(temp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
    }

    private string PathFor(string id) => Path.Combine(_directory, id + ".json");

    private static PersistedCircuit ToPersisted(Circuit c) => new(
        CurrentVersion,
        c.Id,
        c.Title,
        c.GridSize,
        c.CreatedUtc,
        c.LastActivityUtc,
        c.OwnerSessionId,
        // Only Input stays are persisted; clock/button/flop state is runtime-only.
        c.Nodes.Values
            .Select(n => new NodeDto(n.Id, n.Type, n.X, n.Y, n.Type == GateType.Input && n.State, n.DefinitionId))
            .ToList(),
        c.Wires.Values.Select(ToWireDto).ToList(),
        c.CustomIcs.Values.Select(ToIcDto).ToList());

    private static WireDto ToWireDto(Wire w) => new(w.Id, w.FromNodeId, w.ToNodeId, w.ToPin, w.FromPort);

    private static IcDefinitionDto ToIcDto(IcDefinition d) => new(
        d.Id,
        d.Name,
        d.Inputs.Select(p => new IcPinDto(p.Id, p.Label)).ToList(),
        d.Outputs.Select(p => new IcPinDto(p.Id, p.Label)).ToList(),
        d.Nodes.Select(n => new IcNodeDto(n.Id, n.Type, n.State)).ToList(),
        d.Wires.Select(ToWireDto).ToList());

    private static bool TryBuild(PersistedCircuit dto, out Circuit circuit)
    {
        circuit = null!;
        if (dto.Version != CurrentVersion || !IdGen.IsValid(dto.Id)) return false;

        var built = new Circuit
        {
            Id = dto.Id,
            Title = GateSpec.CleanText(dto.Title, GateSpec.MaxTitleLength, "Untitled circuit"),
            GridSize = GateSpec.IsValidGridSize(dto.GridSize) ? dto.GridSize : GateSpec.DefaultGridSize,
            CreatedUtc = dto.CreatedUtc == default ? DateTime.UtcNow : dto.CreatedUtc,
            LastActivityUtc = dto.LastActivityUtc == default ? DateTime.UtcNow : dto.LastActivityUtc,
            OwnerSessionId = string.IsNullOrEmpty(dto.OwnerSessionId) ? null : dto.OwnerSessionId
        };

        var definitions = new Dictionary<string, IcDefinition>();
        foreach (var ic in dto.CustomIcs ?? [])
        {
            if (!TryBuildIc(ic, out var definition) || !definitions.TryAdd(definition.Id, definition))
                return false;
        }

        var nodes = dto.Nodes ?? [];
        if (nodes.Count > GateSpec.MaxNodesPerCircuit) return false;

        var seenNodes = new HashSet<string>();
        foreach (var n in nodes)
        {
            if (!IdGen.IsValid(n.Id) || !seenNodes.Add(n.Id) || !GateSpec.All.Contains(n.Type)) return false;
            if (n.Type == GateType.Ic && !definitions.ContainsKey(n.DefinitionId ?? "")) return false;
            built.Nodes[n.Id] = new NodeItem
            {
                Id = n.Id,
                Type = n.Type,
                X = n.X,
                Y = n.Y,
                State = n.Type == GateType.Input && n.State,
                DefinitionId = n.DefinitionId
            };
        }

        foreach (var ic in definitions.Values)
            built.CustomIcs[ic.Id] = ic;

        var usedWires = new HashSet<string>();
        var usedInputs = new HashSet<(string, int)>();
        foreach (var w in dto.Wires ?? [])
        {
            if (!ValidWire(w, built.Nodes, built, usedWires, usedInputs)) return false;
            built.Wires[w.Id] = new Wire
            {
                Id = w.Id,
                FromNodeId = w.FromNodeId,
                ToNodeId = w.ToNodeId,
                ToPin = w.ToPin,
                FromPort = string.IsNullOrEmpty(w.FromPort) ? "out" : w.FromPort
            };
        }

        circuit = built;
        return true;
    }

    private static bool TryBuildIc(IcDefinitionDto dto, out IcDefinition definition)
    {
        definition = null!;
        if (!IdGen.IsValid(dto.Id)) return false;

        var ic = new IcDefinition { Id = dto.Id, Name = GateSpec.CleanText(dto.Name, GateSpec.MaxNameLength, "Custom IC") };
        var nodes = new Dictionary<string, NodeItem>();
        foreach (var n in dto.Nodes ?? [])
        {
            if (!IdGen.IsValid(n.Id) || !GateSpec.All.Contains(n.Type) || nodes.ContainsKey(n.Id)) return false;
            // Nested ICs, clocks, buttons, displays and flops cannot be packed, so they
            // must never appear inside a restored package either.
            if (n.Type is GateType.Ic or GateType.CLK or GateType.Button or GateType.Seg7 or GateType.FlopD) return false;
            ic.Nodes.Add(new IcNode { Id = n.Id, Type = n.Type, State = n.State });
            nodes[n.Id] = new NodeItem { Id = n.Id, Type = n.Type, State = n.State };
        }

        foreach (var p in dto.Inputs ?? [])
        {
            if (!IdGen.IsValid(p.Id) || nodes.GetValueOrDefault(p.Id)?.Type != GateType.Input) return false;
            ic.Inputs.Add(new IcPin(p.Id, GateSpec.CleanText(p.Label, GateSpec.MaxNameLength, $"IN{ic.Inputs.Count + 1}")));
        }
        foreach (var p in dto.Outputs ?? [])
        {
            if (!IdGen.IsValid(p.Id) || nodes.GetValueOrDefault(p.Id)?.Type != GateType.Output) return false;
            ic.Outputs.Add(new IcPin(p.Id, GateSpec.CleanText(p.Label, GateSpec.MaxNameLength, $"OUT{ic.Outputs.Count + 1}")));
        }
        if (ic.Inputs.Count == 0 || ic.Outputs.Count == 0) return false;

        // Every boundary node must be exposed as a pin, exactly once.
        var pinned = ic.Inputs.Concat(ic.Outputs).Select(p => p.Id).ToHashSet();
        if (ic.Nodes.Any(n => n.Type is GateType.Input or GateType.Output && !pinned.Contains(n.Id))) return false;

        // Internal wires are validated against the package's own node set.
        var internalCircuit = new Circuit { Id = IdGen.New() };
        var usedWires = new HashSet<string>();
        var usedInputs = new HashSet<(string, int)>();
        foreach (var w in dto.Wires ?? [])
        {
            if (!ValidWire(w, nodes, internalCircuit, usedWires, usedInputs)) return false;
            ic.Wires.Add(new Wire
            {
                Id = w.Id,
                FromNodeId = w.FromNodeId,
                ToNodeId = w.ToNodeId,
                ToPin = w.ToPin,
                FromPort = string.IsNullOrEmpty(w.FromPort) ? "out" : w.FromPort
            });
        }

        definition = ic;
        return true;
    }

    private static bool ValidWire(
        WireDto w,
        IReadOnlyDictionary<string, NodeItem> nodes,
        Circuit circuit,
        HashSet<string> usedWires,
        HashSet<(string, int)> usedInputs)
    {
        if (!IdGen.IsValid(w.Id) || !usedWires.Add(w.Id)) return false;
        if (w.FromNodeId == w.ToNodeId) return false;
        if (!nodes.TryGetValue(w.FromNodeId, out var from) || !nodes.TryGetValue(w.ToNodeId, out var to)) return false;
        if (!GateSpec.HasOutputPin(from.Type)) return false;

        var pins = GateSpec.InputPins(circuit, to);
        if (pins == 0 || w.ToPin < 0 || w.ToPin >= pins) return false;
        return usedInputs.Add((w.ToNodeId, w.ToPin));
    }
}
