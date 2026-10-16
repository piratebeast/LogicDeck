namespace LogicDeck.Core;

public sealed class LobbySession
{
    public required string SessionId { get; init; }
    public string BaseName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public DateTime GraceUntilUtc { get; set; } = DateTime.MaxValue;
}

public sealed class LobbyService
{
    private static readonly string[] Palette =
    [
        "#45d6a4", "#5b9cf6", "#e8b64c", "#e06a8c",
        "#9d8cff", "#4fc3e8", "#b5d65c", "#ef8f5f"
    ];

    private readonly object _lock = new();
    private readonly Dictionary<string, LobbySession> _sessions = new();
    private readonly Dictionary<string, string> _ownerByName = new(StringComparer.OrdinalIgnoreCase);

    public LobbySession Join(string sessionId, string? requested)
    {
        var clean = GateSpec.CleanText(requested, GateSpec.MaxNameLength, "Anonymous");
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                existing.GraceUntilUtc = DateTime.MaxValue;
                if (!string.Equals(existing.BaseName, clean, StringComparison.OrdinalIgnoreCase))
                {
                    ReleaseNameLocked(existing);
                    existing.BaseName = clean;
                    existing.Name = AllocateLocked(clean);
                }
                return existing;
            }

            var session = new LobbySession
            {
                SessionId = sessionId,
                BaseName = clean,
                Name = AllocateLocked(clean),
                Color = PickColorLocked()
            };
            _sessions[sessionId] = session;
            return session;
        }
    }

    public LobbySession? Find(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
    }

    public void MarkDisconnected(string sessionId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var s) && s.GraceUntilUtc == DateTime.MaxValue)
                s.GraceUntilUtc = DateTime.UtcNow.AddSeconds(GateSpec.DisconnectGraceSeconds);
        }
    }

    public List<LobbySession> Sweep(DateTime utcNow)
    {
        lock (_lock)
        {
            var expired = _sessions.Values.Where(s => s.GraceUntilUtc < utcNow).ToList();
            foreach (var s in expired)
            {
                ReleaseNameLocked(s);
                _sessions.Remove(s.SessionId);
            }
            return expired;
        }
    }

    private string AllocateLocked(string baseName)
    {
        var candidate = baseName;
        for (var n = 2; _ownerByName.ContainsKey(candidate); n++)
            candidate = $"{baseName} {n}";
        _ownerByName[candidate] = string.Empty;
        return candidate;
    }

    private void ReleaseNameLocked(LobbySession session)
    {
        if (_ownerByName.TryGetValue(session.Name, out _))
            _ownerByName.Remove(session.Name);
    }

    private string PickColorLocked()
    {
        var usage = new int[Palette.Length];
        foreach (var s in _sessions.Values)
        {
            var idx = Array.IndexOf(Palette, s.Color);
            if (idx >= 0) usage[idx]++;
        }
        var min = usage.Min();
        return Palette[Array.IndexOf(usage, min)];
    }
}
