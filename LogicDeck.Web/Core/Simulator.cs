namespace LogicDeck.Core;

public static class Simulator
{
    private const int MaxIterations = 96;

    public const int MaxTruthTableInputs = 8;

    public static SignalFrame Evaluate(
        IReadOnlyCollection<NodeItem> nodes,
        IReadOnlyCollection<Wire> wires,
        IReadOnlyDictionary<string, IcDefinition>? icDefinitions = null,
        IReadOnlyDictionary<string, bool>? initialWireValues = null)
    {
        var wireValue = wires.ToDictionary(w => w.Id, w => initialWireValues?.GetValueOrDefault(w.Id) ?? false);

        bool ReadPin(NodeItem node, int pin)
        {
            foreach (var w in wires)
                if (w.ToNodeId == node.Id && w.ToPin == pin)
                    return wireValue[w.Id];
            return false;
        }

        int InputCount(NodeItem node)
        {
            if (node.Type == GateType.Ic && icDefinitions is not null &&
                icDefinitions.TryGetValue(node.DefinitionId ?? "", out var definition))
                return definition.Inputs.Count;
            return GateSpec.InputPins(node.Type);
        }

        bool DrivePrimitive(NodeItem node) => node.Type switch
        {
            GateType.Input or GateType.CLK or GateType.Button => node.State,
            GateType.And => ReadPin(node, 0) & ReadPin(node, 1),
            GateType.Or => ReadPin(node, 0) | ReadPin(node, 1),
            GateType.Xor => ReadPin(node, 0) ^ ReadPin(node, 1),
            GateType.Xnor => !(ReadPin(node, 0) ^ ReadPin(node, 1)),
            GateType.Nand => !(ReadPin(node, 0) & ReadPin(node, 1)),
            GateType.Nor => !(ReadPin(node, 0) | ReadPin(node, 1)),
            GateType.Not => !ReadPin(node, 0),
            _ => false
        };

        Dictionary<string, bool> OutputValues(NodeItem node)
        {
            if (node.Type == GateType.FlopD)
                return new Dictionary<string, bool> { ["out0"] = node.State, ["out1"] = !node.State };
            if (node.Type != GateType.Ic)
                return new Dictionary<string, bool> { ["out"] = DrivePrimitive(node) };
            if (icDefinitions is null || !icDefinitions.TryGetValue(node.DefinitionId ?? "", out var definition))
                return new Dictionary<string, bool>();

            var seed = definition.Wires.ToDictionary(w => w.Id, _ => false);
            for (var i = 0; i < definition.Inputs.Count; i++)
            {
                var feed = definition.Wires.FirstOrDefault(w => w.FromNodeId == definition.Inputs[i].Id);
                if (feed != null) seed[feed.Id] = ReadPin(node, i);
            }

            var internalNodes = definition.Nodes
                .Select(n => new NodeItem { Id = n.Id, Type = n.Type, State = n.State })
                .ToList();
            for (var i = 0; i < definition.Inputs.Count; i++)
            {
                var inputNode = internalNodes.First(n => n.Id == definition.Inputs[i].Id);
                inputNode.State = ReadPin(node, i);
            }
            var frame = Evaluate(internalNodes, definition.Wires, null, seed);
            return definition.Outputs
                .Select((pin, index) => new KeyValuePair<string, bool>($"out{index}", frame.Outputs.GetValueOrDefault(pin.Id)))
                .ToDictionary(p => p.Key, p => p.Value);
        }

        var driven = nodes.Where(n => GateSpec.HasOutputPin(n.Type)).ToList();

        void Settle()
        {
            for (var i = 0; i < MaxIterations; i++)
            {
                var changed = false;
                foreach (var node in driven)
                {
                    var values = OutputValues(node);
                    foreach (var w in wires)
                    {
                        if (w.FromNodeId != node.Id) continue;
                        var value = values.GetValueOrDefault(w.FromPort);
                        if (wireValue[w.Id] == value) continue;
                        wireValue[w.Id] = value;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
        }

        Settle();

        // Flop update pass: edges are detected on the settled clock levels and the
        // nets are re-settled after each commit round, so a chain of flops ripples
        // through delta iterations just like combinational logic. Flops that share
        // a clock edge capture from the same snapshot (nonblocking semantics); the
        // loop is bounded by the flop count to keep every evaluation terminating.
        var flops = nodes.Where(n => n.Type == GateType.FlopD).ToList();
        for (var iteration = 0; iteration <= flops.Count; iteration++)
        {
            var committed = false;
            foreach (var flop in flops)
            {
                var clk = ReadPin(flop, 1);
                if (clk && !flop.ClkPrev)
                {
                    var d = ReadPin(flop, 0);
                    if (flop.State != d)
                    {
                        flop.State = d;
                        committed = true;
                    }
                }
                flop.ClkPrev = clk;
            }
            if (!committed) break;
            Settle();
        }

        var unstableNodes = new HashSet<string>();
        foreach (var node in driven)
        {
            var values = OutputValues(node);
            foreach (var w in wires)
                if (w.FromNodeId == node.Id && wireValue[w.Id] != values.GetValueOrDefault(w.FromPort))
                    unstableNodes.Add(node.Id);
        }

        bool Grew()
        {
            var grew = false;
            foreach (var node in driven)
            {
                if (unstableNodes.Contains(node.Id)) continue;
                for (var pin = 0; pin < InputCount(node); pin++)
                {
                    var feeder = wires.FirstOrDefault(w =>
                        w.ToNodeId == node.Id && w.ToPin == pin &&
                        unstableNodes.Contains(w.FromNodeId));
                    if (feeder == null) continue;
                    unstableNodes.Add(node.Id);
                    grew = true;
                    break;
                }
            }
            return grew;
        }

        while (Grew()) { }

        var unstableWires = wires
            .Where(w => unstableNodes.Contains(w.FromNodeId))
            .Select(w => w.Id)
            .ToList();

        var outputs = nodes
            .Where(n => n.Type == GateType.Output)
            .ToDictionary(
                n => n.Id,
                n => wires.FirstOrDefault(w => w.ToNodeId == n.Id && w.ToPin == 0) is { } feed
                    ? wireValue[feed.Id]
                    : false);

        var sources = nodes
            .Where(n => n.Type is GateType.Input or GateType.CLK or GateType.Button)
            .ToDictionary(n => n.Id, n => n.State);

        var displays = nodes
            .Where(n => n.Type == GateType.Seg7)
            .ToDictionary(n => n.Id, n =>
            {
                var nibble = (ReadPin(n, 0) ? 1 : 0) | (ReadPin(n, 1) ? 2 : 0)
                           | (ReadPin(n, 2) ? 4 : 0) | (ReadPin(n, 3) ? 8 : 0);
                return (int)GateSpec.Seg7Masks[nibble];
            });

        var flopStates = nodes
            .Where(n => n.Type == GateType.FlopD)
            .ToDictionary(n => n.Id, n => n.State);

        return new SignalFrame(wireValue, unstableWires, outputs, sources, displays, flopStates);
    }

    // Enumerates every combination of Input switch states (A = most significant)
    // and evaluates the circuit for each, so a client can render a truth table.
    // The temporary Input/Flop state is restored before returning, leaving the
    // live circuit untouched. Flops restart at Q=0 each row (combinational view).
    public static TruthTableDto ComputeTruthTable(
        IReadOnlyCollection<NodeItem> nodes,
        IReadOnlyCollection<Wire> wires,
        IReadOnlyDictionary<string, IcDefinition>? icDefinitions = null)
    {
        var inputs = nodes
            .Where(n => n.Type == GateType.Input)
            .OrderBy(n => n.Y).ThenBy(n => n.X)
            .ToList();
        var outputs = nodes
            .Where(n => n.Type == GateType.Output)
            .OrderBy(n => n.Y).ThenBy(n => n.X)
            .ToList();
        var flops = nodes.Where(n => n.Type == GateType.FlopD).ToList();

        var savedInputs = inputs.ToDictionary(n => n.Id, n => n.State);
        var savedFlops = flops.ToDictionary(n => n.Id, n => (n.State, n.ClkPrev));

        var rows = new List<TruthTableRow>();
        var combinations = 1 << inputs.Count;
        for (var mask = 0; mask < combinations; mask++)
        {
            var rowInputs = new List<bool>(inputs.Count);
            for (var i = 0; i < inputs.Count; i++)
            {
                var value = (mask & (1 << (inputs.Count - 1 - i))) != 0;
                inputs[i].State = value;
                rowInputs.Add(value);
            }
            foreach (var flop in flops)
            {
                flop.State = false;
                flop.ClkPrev = false;
            }
            var frame = Evaluate(nodes, wires, icDefinitions);
            rows.Add(new TruthTableRow(
                rowInputs,
                outputs.Select(o => frame.Outputs.GetValueOrDefault(o.Id)).ToList()));
        }

        foreach (var input in inputs) input.State = savedInputs[input.Id];
        foreach (var flop in flops) (flop.State, flop.ClkPrev) = savedFlops[flop.Id];

        return new TruthTableDto(
            inputs.Select((n, i) => new TruthTableVar(n.Id, VariableName(i))).ToList(),
            outputs.Select((o, i) => new TruthTableVar(o.Id, $"OUT{i + 1}")).ToList(),
            rows);
    }

    private static string VariableName(int index) =>
        index < 26 ? ((char)('A' + index)).ToString() : $"X{index + 1}";
}
