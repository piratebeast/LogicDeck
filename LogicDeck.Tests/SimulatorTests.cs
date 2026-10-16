using LogicDeck.Core;
using Xunit;

namespace LogicDeck.Tests;

public class SimulatorTests
{
    private static NodeItem N(string id, GateType type, bool state = false) =>
        new() { Id = id, Type = type, State = state };

    private static Wire W(string id, string from, string to, int pin = 0) =>
        new() { Id = id, FromNodeId = from, ToNodeId = to, ToPin = pin };

    private static (bool Wire, bool Output) Run(GateType gate, params bool[] inputs)
    {
        var nodes = new List<NodeItem> { N("in0", GateType.Input, inputs[0]) };
        var wires = new List<Wire>();
        if (inputs.Length > 1)
        {
            nodes.Add(N("in1", GateType.Input, inputs[1]));
            wires.Add(W("w1", "in1", "gate", 1));
        }
        wires.Add(W("w0", "in0", "gate", 0));
        wires.Add(W("wo", "gate", "lamp"));
        nodes.Add(N("gate", gate));
        nodes.Add(N("lamp", GateType.Output));

        var frame = Simulator.Evaluate(nodes, wires);
        return (frame.Wires["wo"], frame.Outputs["lamp"]);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void And_gate_matches_truth_table(bool a, bool b, bool expected)
    {
        var (wire, output) = Run(GateType.And, a, b);
        Assert.Equal(expected, wire);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Or_gate_matches_truth_table(bool a, bool b, bool expected)
    {
        var (wire, output) = Run(GateType.Or, a, b);
        Assert.Equal(expected, wire);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void Xor_gate_matches_truth_table(bool a, bool b, bool expected)
    {
        var (wire, output) = Run(GateType.Xor, a, b);
        Assert.Equal(expected, wire);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Xnor_gate_matches_truth_table(bool a, bool b, bool expected)
    {
        var (wire, output) = Run(GateType.Xnor, a, b);
        Assert.Equal(expected, wire);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Not_gate_inverts()
    {
        var (low, lowOut) = Run(GateType.Not, false);
        var (high, highOut) = Run(GateType.Not, true);
        Assert.True(low && lowOut);
        Assert.False(high || highOut);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void Nand_gate_matches_truth_table(bool a, bool b, bool expected)
    {
        var (_, output) = Run(GateType.Nand, a, b);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Nor_gate_matches_truth_table(bool a, bool b, bool expected)
    {
        var (_, output) = Run(GateType.Nor, a, b);
        Assert.Equal(expected, output);
    }

    private static SignalFrame HalfAdder(bool a, bool b)
    {
        var nodes = new List<NodeItem>
        {
            N("a", GateType.Input, a),
            N("b", GateType.Input, b),
            N("xor", GateType.Xor),
            N("and", GateType.And),
            N("sum", GateType.Output),
            N("carry", GateType.Output)
        };
        var wires = new List<Wire>
        {
            W("wa0", "a", "xor", 0),
            W("wa1", "a", "and", 0),
            W("wb0", "b", "xor", 1),
            W("wb1", "b", "and", 1),
            W("ws", "xor", "sum"),
            W("wc", "and", "carry")
        };
        return Simulator.Evaluate(nodes, wires);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, true)]
    public void Half_adder_sums_and_carries(bool a, bool b, bool sum, bool carry)
    {
        var frame = HalfAdder(a, b);
        Assert.Equal(sum, frame.Outputs["sum"]);
        Assert.Equal(carry, frame.Outputs["carry"]);
        Assert.Empty(frame.Unstable);
    }

    [Fact]
    public void Cross_coupled_nors_settle_without_being_flagged_unstable()
    {
        var nodes = new List<NodeItem>
        {
            N("s", GateType.Input),
            N("r", GateType.Input),
            N("q", GateType.Nor),
            N("qb", GateType.Nor),
            N("outQ", GateType.Output)
        };
        var wires = new List<Wire>
        {
            W("ws_q", "s", "q", 0),
            W("wqb_q", "qb", "q", 1),
            W("wr_qb", "r", "qb", 0),
            W("wq_qb", "q", "qb", 1),
            W("wout", "q", "outQ")
        };
        var frame = Simulator.Evaluate(nodes, wires);
        Assert.Empty(frame.Unstable);

        var q = frame.Wires["wout"];
        var qb = frame.Wires["wqb_q"];
        Assert.NotEqual(q, qb);
    }

    [Fact]
    public void Not_gate_loop_is_reported_unstable()
    {
        var nodes = new List<NodeItem> { N("not", GateType.Not) };
        var wires = new List<Wire> { W("loop", "not", "not", 0) };

        var frame = Simulator.Evaluate(nodes, wires);

        Assert.Contains("loop", frame.Unstable);
    }

    [Fact]
    public void Unconnected_output_reads_low()
    {
        var frame = Simulator.Evaluate([N("lamp", GateType.Output)], []);
        Assert.False(frame.Outputs["lamp"]);
    }

    [Fact]
    public void Clock_drives_outgoing_wires_from_its_state()
    {
        var nodes = new List<NodeItem> { N("clk", GateType.CLK, true), N("lamp", GateType.Output) };
        var wires = new List<Wire> { W("w", "clk", "lamp") };

        var frame = Simulator.Evaluate(nodes, wires);

        Assert.True(frame.Wires["w"]);
        Assert.True(frame.Outputs["lamp"]);
        Assert.True(frame.Sources["clk"]);
    }

    [Fact]
    public void Sources_include_input_and_clock_phases()
    {
        var nodes = new List<NodeItem>
        {
            N("in", GateType.Input, true),
            N("clk", GateType.CLK, false),
            N("and", GateType.And),
            N("lamp", GateType.Output)
        };
        var wires = new List<Wire>
        {
            W("w0", "in", "and", 0),
            W("w1", "clk", "and", 1),
            W("wo", "and", "lamp")
        };

        var frame = Simulator.Evaluate(nodes, wires);

        Assert.True(frame.Sources["in"]);
        Assert.False(frame.Sources["clk"]);
        Assert.False(frame.Outputs["lamp"]);
    }

    [Fact]
    public void Button_drive_is_momentary_state()
    {
        var nodes = new List<NodeItem> { N("button", GateType.Button, true), N("lamp", GateType.Output) };
        var wires = new List<Wire> { W("w", "button", "lamp") };

        var frame = Simulator.Evaluate(nodes, wires);

        Assert.True(frame.Wires["w"]);
        Assert.True(frame.Outputs["lamp"]);
        Assert.True(frame.Sources["button"]);
    }

    [Fact]
    public void Custom_ic_evaluates_its_internal_blueprint()
    {
        var definition = new IcDefinition { Id = "not-ic", Name = "NOT IC" };
        definition.Nodes.Add(new IcNode { Id = "boundary-in", Type = GateType.Input });
        definition.Nodes.Add(new IcNode { Id = "inner-not", Type = GateType.Not });
        definition.Nodes.Add(new IcNode { Id = "boundary-out", Type = GateType.Output });
        definition.Inputs.Add(new IcPin("boundary-in", "IN1"));
        definition.Outputs.Add(new IcPin("boundary-out", "OUT1"));
        definition.Wires.Add(new Wire { Id = "inner-in", FromNodeId = "boundary-in", ToNodeId = "inner-not", ToPin = 0 });
        definition.Wires.Add(new Wire { Id = "inner-out", FromNodeId = "inner-not", ToNodeId = "boundary-out", ToPin = 0 });
        var definitions = new Dictionary<string, IcDefinition> { [definition.Id] = definition };
        var nodes = new List<NodeItem>
        {
            N("source", GateType.Input, true),
            new NodeItem { Id = "ic", Type = GateType.Ic, DefinitionId = definition.Id },
            N("lamp", GateType.Output)
        };
        var wires = new List<Wire>
        {
            W("source-to-ic", "source", "ic"),
            new Wire { Id = "ic-to-lamp", FromNodeId = "ic", ToNodeId = "lamp", ToPin = 0, FromPort = "out0" }
        };

        var frame = Simulator.Evaluate(nodes, wires, definitions);

        Assert.False(frame.Wires["ic-to-lamp"]);
        Assert.False(frame.Outputs["lamp"]);
    }

    [Theory]
    [InlineData(0, 0x3F)]
    [InlineData(1, 0x06)]
    [InlineData(2, 0x5B)]
    [InlineData(5, 0x6D)]
    [InlineData(8, 0x7F)]
    [InlineData(10, 0x77)]
    [InlineData(15, 0x71)]
    public void Seven_segment_decodes_its_bcd_nibble(int nibble, int expectedMask)
    {
        var nodes = new List<NodeItem>();
        var wires = new List<Wire>();
        for (var bit = 0; bit < 4; bit++)
        {
            nodes.Add(N($"in{bit}", GateType.Input, (nibble & (1 << bit)) != 0));
            wires.Add(W($"w{bit}", $"in{bit}", "seg", bit));
        }
        nodes.Add(N("seg", GateType.Seg7));

        var frame = Simulator.Evaluate(nodes, wires);

        Assert.Equal(expectedMask, frame.Displays["seg"]);
        Assert.Empty(frame.Outputs);
    }

    [Fact]
    public void Seven_segment_has_four_inputs_and_no_output_pin()
    {
        Assert.Equal(4, GateSpec.InputPins(GateType.Seg7));
        Assert.False(GateSpec.HasOutputPin(GateType.Seg7));
        Assert.Contains(GateType.Seg7, GateSpec.All);
    }

    [Fact]
    public void Clock_has_no_input_pins_and_has_output()
    {
        Assert.Equal(0, GateSpec.InputPins(GateType.CLK));
        Assert.True(GateSpec.HasOutputPin(GateType.CLK));
        Assert.Contains(GateType.CLK, GateSpec.All);
    }

    [Fact]
    public void D_flip_flop_pins_follow_the_spec()
    {
        Assert.Equal(2, GateSpec.InputPins(GateType.FlopD));
        Assert.True(GateSpec.HasOutputPin(GateType.FlopD));
        Assert.Contains(GateType.FlopD, GateSpec.All);
    }

    [Fact]
    public void D_flip_flop_captures_d_only_on_a_rising_clock_edge()
    {
        var flop = N("flop", GateType.FlopD);
        var d = N("d", GateType.Input, true);
        var clk = N("clk", GateType.Button, false);
        var lamp = N("lamp", GateType.Output);
        var lampBar = N("lampBar", GateType.Output);
        var nodes = new List<NodeItem> { flop, d, clk, lamp, lampBar };
        var wires = new List<Wire>
        {
            W("wd", "d", "flop", 0),
            W("wc", "clk", "flop", 1),
            new Wire { Id = "wq", FromNodeId = "flop", ToNodeId = "lamp", ToPin = 0, FromPort = "out0" },
            new Wire { Id = "wqb", FromNodeId = "flop", ToNodeId = "lampBar", ToPin = 0, FromPort = "out1" }
        };

        var frame = Simulator.Evaluate(nodes, wires);
        Assert.False(frame.Wires["wq"]);
        Assert.True(frame.Wires["wqb"]);
        Assert.False(frame.Flops["flop"]);

        // Rising edge captures D.
        clk.State = true;
        frame = Simulator.Evaluate(nodes, wires);
        Assert.True(frame.Wires["wq"]);
        Assert.False(frame.Wires["wqb"]);
        Assert.True(frame.Flops["flop"]);
        Assert.True(frame.Outputs["lamp"]);
        Assert.False(frame.Outputs["lampBar"]);

        // Holding the clock high and changing D does nothing.
        d.State = false;
        frame = Simulator.Evaluate(nodes, wires);
        Assert.True(frame.Flops["flop"]);

        // A falling edge also does nothing.
        clk.State = false;
        frame = Simulator.Evaluate(nodes, wires);
        Assert.True(frame.Flops["flop"]);

        // The next rising edge latches the new level.
        clk.State = true;
        frame = Simulator.Evaluate(nodes, wires);
        Assert.False(frame.Wires["wq"]);
        Assert.True(frame.Wires["wqb"]);
        Assert.False(frame.Flops["flop"]);
        Assert.False(frame.Outputs["lamp"]);
        Assert.True(frame.Outputs["lampBar"]);
    }

    [Fact]
    public void D_flip_flop_toggles_as_a_divide_by_two_when_q_bar_feeds_back()
    {
        var flop = N("flop", GateType.FlopD);
        var clk = N("clk", GateType.Button, false);
        var nodes = new List<NodeItem> { flop, clk };
        var wires = new List<Wire>
        {
            W("wc", "clk", "flop", 1),
            new Wire { Id = "feedback", FromNodeId = "flop", ToNodeId = "flop", ToPin = 0, FromPort = "out1" }
        };

        var frame = Simulator.Evaluate(nodes, wires);
        Assert.False(frame.Flops["flop"]);
        Assert.True(frame.Wires["feedback"]);

        clk.State = true;
        frame = Simulator.Evaluate(nodes, wires);
        Assert.True(frame.Flops["flop"]);

        clk.State = false;
        frame = Simulator.Evaluate(nodes, wires);
        Assert.True(frame.Flops["flop"]);

        clk.State = true;
        frame = Simulator.Evaluate(nodes, wires);
        Assert.False(frame.Flops["flop"]);
    }

    private static bool[] FlattenRow(TruthTableRow row) => [.. row.Inputs, .. row.Outputs];

    [Fact]
    public void Truth_table_enumerates_inputs_in_position_order()
    {
        var nodes = new List<NodeItem>
        {
            new() { Id = "a", Type = GateType.Input, X = 0, Y = 0 },
            new() { Id = "b", Type = GateType.Input, X = 0, Y = 64 },
            new() { Id = "and", Type = GateType.And },
            new() { Id = "lamp", Type = GateType.Output }
        };
        var wires = new List<Wire>
        {
            W("wa", "a", "and", 0),
            W("wb", "b", "and", 1),
            W("wo", "and", "lamp")
        };

        var table = Simulator.ComputeTruthTable(nodes, wires);

        Assert.Equal(new[] { "A", "B" }, table.Inputs.Select(v => v.Label));
        Assert.Equal("a", table.Inputs[0].Id);
        Assert.Equal("lamp", Assert.Single(table.Outputs).Id);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(new[] { false, false, false }, FlattenRow(table.Rows[0]));
        Assert.Equal(new[] { false, true, false }, FlattenRow(table.Rows[1]));
        Assert.Equal(new[] { true, false, false }, FlattenRow(table.Rows[2]));
        Assert.Equal(new[] { true, true, true }, FlattenRow(table.Rows[3]));
    }

    [Fact]
    public void Truth_table_restores_input_and_flop_state_after_computing()
    {
        var a = N("a", GateType.Input, true);
        var flop = N("flop", GateType.FlopD, true);
        var nodes = new List<NodeItem> { a, flop };
        var wires = new List<Wire> { W("wa", "a", "flop", 0) };

        var table = Simulator.ComputeTruthTable(nodes, wires);

        Assert.Equal(2, table.Rows.Count);
        Assert.True(a.State);
        Assert.True(flop.State);
        Assert.False(flop.ClkPrev);
    }
}
