namespace LogicDeck.Core;

public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<string, string> _connectionToSession = new();

    public void Bind(string connectionId, string sessionId) =>
        _connectionToSession[connectionId] = sessionId;

    public string? SessionOf(string connectionId) =>
        _connectionToSession.TryGetValue(connectionId, out var sid) ? sid : null;

    public string? Unbind(string connectionId) =>
        _connectionToSession.TryRemove(connectionId, out var sid) ? sid : null;
}
