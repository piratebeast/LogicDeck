using LogicDeck.Core;
using Xunit;

namespace LogicDeck.Tests;

public class CircuitHistoryTests
{
    private static CircuitState State(string title) => new(title, 16, [], [], []);

    [Fact]
    public void Undo_and_redo_walk_the_edit_stack()
    {
        var history = new CircuitHistory();
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        var before = State("A");
        var after = State("B");
        history.Record(before);

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        var undone = history.Undo(after);
        Assert.Same(before, undone);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);

        var redone = history.Redo(before);
        Assert.Same(after, redone);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void A_new_edit_clears_the_redo_stack()
    {
        var history = new CircuitHistory();
        history.Record(State("A"));
        history.Undo(State("B"));
        Assert.True(history.CanRedo);

        history.Record(State("A2"));

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void History_depth_is_bounded()
    {
        var history = new CircuitHistory(depth: 3);
        for (var i = 0; i < 5; i++) history.Record(State($"s{i}"));

        Assert.NotNull(history.Undo(State("cur")));
        Assert.NotNull(history.Undo(State("cur")));
        Assert.NotNull(history.Undo(State("cur")));
        Assert.Null(history.Undo(State("cur")));
    }

    [Fact]
    public void Undo_without_history_returns_null()
    {
        var history = new CircuitHistory();
        Assert.Null(history.Undo(State("cur")));
        Assert.Null(history.Redo(State("cur")));
    }

    [Fact]
    public void Capturing_and_restoring_a_circuit_round_trips_editable_content()
    {
        var circuit = new Circuit { Id = "capturerest" };
        circuit.Title = "Original";
        circuit.GridSize = 24;
        circuit.Nodes["switch"] = new NodeItem { Id = "switch", Type = GateType.Input, X = 16, Y = 32, State = true };
        circuit.Nodes["andgat"] = new NodeItem { Id = "andgat", Type = GateType.And, X = 160, Y = 32 };
        circuit.Wires["wire001"] = new Wire { Id = "wire001", FromNodeId = "switch", ToNodeId = "andgat", ToPin = 0 };

        var state = circuit.Capture();

        circuit.Title = "Changed";
        circuit.GridSize = 8;
        circuit.Nodes.Remove("switch");
        circuit.Wires.Clear();

        circuit.Restore(state);

        Assert.Equal("Original", circuit.Title);
        Assert.Equal(24, circuit.GridSize);
        Assert.True(circuit.Nodes["switch"].State);
        Assert.Single(circuit.Wires);
    }
}
