using Microsoft.AspNetCore.SignalR;

namespace LogicDeck.Core;

public static class HubGroups
{
    public const string Lobby = "lobby";
    public const string RoomKey = "roomId";

    public static string Room(string circuitId) => $"room:{circuitId}";
}
