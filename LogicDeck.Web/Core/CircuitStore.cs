namespace LogicDeck.Core;

public sealed class CircuitStore
{
    private readonly ConcurrentDictionary<string, Circuit> _circuits = new();
    private readonly ConcurrentDictionary<string, byte> _dirty = new();
    private readonly CircuitPersistence? _persistence;

    public CircuitStore() : this(null)
    {
    }

    public CircuitStore(CircuitPersistence? persistence)
    {
        _persistence = persistence;
        if (persistence is null) return;
        foreach (var circuit in persistence.LoadAll())
        {
            Track(circuit);
            _circuits[circuit.Id] = circuit;
        }
    }

    private void Track(Circuit circuit) => circuit.Touched += () => _dirty[circuit.Id] = 0;

    public Circuit Create(string title, int gridSize, string? ownerSessionId = null)
    {
        while (true)
        {
            var circuit = new Circuit { Id = IdGen.New(), Title = title, GridSize = gridSize, OwnerSessionId = ownerSessionId };
            Track(circuit);
            if (_circuits.TryAdd(circuit.Id, circuit))
            {
                _dirty[circuit.Id] = 0;
                return circuit;
            }
        }
    }

    public Circuit? Find(string id) => _circuits.TryGetValue(id, out var c) ? c : null;

    public int Count => _circuits.Count;

    public IReadOnlyList<RoomSummary> Summaries()
    {
        return _circuits.Values
            .OrderByDescending(c => c.LastActivityUtc)
            .Select(c =>
            {
                lock (c.Lock) return ToSummary(c);
            })
            .ToList();
    }

    public static RoomSummary ToSummary(Circuit c) => new(
        c.Id,
        c.Title,
        c.GridSize,
        c.CreatedUtc,
        c.Running,
        c.Nodes.Count,
        c.Members.Values.ToList(),
        c.OwnerSessionId);

    public Circuit? Delete(string id)
    {
        if (!_circuits.TryRemove(id, out var circuit)) return null;
        _dirty.TryRemove(id, out _);
        TryDeleteFile(id);
        return circuit;
    }

    /// <summary>Writes every circuit that changed since the previous flush (and removes files
    /// for rooms that are gone). Safe to call from a background timer and on shutdown.</summary>
    public void FlushDirty()
    {
        if (_persistence is null) return;
        foreach (var id in _dirty.Keys.ToList())
        {
            _dirty.TryRemove(id, out _);
            var circuit = Find(id);
            if (circuit is null)
            {
                TryDeleteFile(id);
                continue;
            }
            string json;
            lock (circuit.Lock) json = _persistence.Serialize(circuit);
            TryWriteFile(id, json);
        }
    }

    /// <summary>Marks every live circuit dirty and persists immediately.</summary>
    public void FlushAll()
    {
        if (_persistence is null) return;
        foreach (var id in _circuits.Keys) _dirty[id] = 0;
        FlushDirty();
    }

    private void TryWriteFile(string id, string json)
    {
        try
        {
            _persistence?.Write(id, json);
        }
        catch (Exception)
        {
            // A failed save must never take down a live editing session.
        }
    }

    private void TryDeleteFile(string id)
    {
        try
        {
            _persistence?.Delete(id);
        }
        catch (Exception)
        {
        }
    }

    public List<(Circuit Circuit, List<MemberInfo> Removed)> DropMembers(Func<string, bool> sessionIsGone)
    {
        var results = new List<(Circuit Circuit, List<MemberInfo> Removed)>();
        foreach (var circuit in _circuits.Values)
        {
            List<MemberInfo> removed;
            lock (circuit.Lock)
            {
                var goneIds = circuit.Members.Keys.Where(sessionIsGone).ToList();
                if (goneIds.Count == 0) continue;
                removed = new List<MemberInfo>();
                foreach (var id in goneIds)
                    if (circuit.Members.Remove(id, out var m)) removed.Add(m);
                circuit.Touch();
            }
            results.Add((circuit, removed));
        }
        return results;
    }

    public List<string> Sweep(DateTime utcNow, TimeSpan idleAfter)
    {
        var removed = new List<string>();
        foreach (var (id, circuit) in _circuits)
        {
            bool remove;
            lock (circuit.Lock)
                remove = circuit.Members.Count == 0 &&
                         utcNow - circuit.LastActivityUtc > idleAfter;
            if (remove && _circuits.TryRemove(id, out _))
            {
                _dirty.TryRemove(id, out _);
                TryDeleteFile(id);
                removed.Add(id);
            }
        }
        return removed;
    }

    public List<(Circuit Circuit, SignalFrame Frame)> TickClocks()
    {
        var results = new List<(Circuit, SignalFrame)>();
        foreach (var circuit in _circuits.Values)
        {
            lock (circuit.Lock)
            {
                if (!circuit.Running) continue;
                var clocks = circuit.Nodes.Values.Where(n => n.Type == GateType.CLK).ToList();
                if (clocks.Count == 0) continue;
                foreach (var clock in clocks)
                    clock.State = !clock.State;
                circuit.Touch();
                results.Add((circuit, circuit.Evaluate()));
            }
        }
        return results;
    }
}
