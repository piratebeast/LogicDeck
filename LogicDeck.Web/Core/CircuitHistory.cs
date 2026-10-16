namespace LogicDeck.Core;

/// <summary>
/// An immutable snapshot of everything an edit can change. Runtime state
/// (members, RUN, clock/button/flop levels within a snapshot aside) is either
/// transient or intentionally excluded by the caller.
/// </summary>
public sealed record CircuitState(
    string Title,
    int GridSize,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<WireDto> Wires,
    IReadOnlyList<IcDefinitionDto> CustomIcs);

/// <summary>
/// A bounded, room-global linear edit history. Undo restores a full snapshot, so
/// it always steps back the room's <em>most recent</em> change (whoever made it);
/// per-session undo would clobber other people's later edits.
/// </summary>
public sealed class CircuitHistory
{
    public const int DefaultDepth = 50;

    private readonly int _depth;
    private readonly List<CircuitState> _undo = [];
    private readonly List<CircuitState> _redo = [];

    public CircuitHistory(int depth = DefaultDepth) => _depth = Math.Max(1, depth);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records the pre-edit state. Any redo stack is discarded, because a
    /// fresh edit starts a new branch of history.</summary>
    public void Record(CircuitState before)
    {
        _undo.Add(before);
        if (_undo.Count > _depth) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public CircuitState? Undo(CircuitState current)
    {
        if (_undo.Count == 0) return null;
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return previous;
    }

    public CircuitState? Redo(CircuitState current)
    {
        if (_redo.Count == 0) return null;
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(next);
        return next;
    }
}
