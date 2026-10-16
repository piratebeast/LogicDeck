using LogicDeck.Core;
using LogicDeck.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LogicDeck.Web;

public sealed class Sweeper : BackgroundService
{
    private readonly LobbyService _lobby;
    private readonly CircuitStore _store;
    private readonly IHubContext<CollabHub, ICollabClient> _hub;
    private readonly ILogger<Sweeper> _logger;

    public Sweeper(LobbyService lobby, CircuitStore store, IHubContext<CollabHub, ICollabClient> hub, ILogger<Sweeper> logger)
    {
        _lobby = lobby;
        _store = store;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepOnce(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background sweep failed");
            }
        }
    }

    private async Task SweepOnce(DateTime utcNow, CancellationToken ct)
    {
        _lobby.Sweep(utcNow);

        var dropped = _store.DropMembers(sid => _lobby.Find(sid) == null);
        foreach (var (circuit, removed) in dropped)
            foreach (var member in removed)
                await _hub.Clients.Group(HubGroups.Room(circuit.Id)).MemberLeft(member);

        var closed = _store.Sweep(utcNow, TimeSpan.FromMinutes(GateSpec.IdleCircuitMinutes));
        foreach (var id in closed)
            await _hub.Clients.Group(HubGroups.Lobby).RoomClosed(id);

        if (dropped.Count > 0 || closed.Count > 0)
            await _hub.Clients.Group(HubGroups.Lobby).RoomsChanged(_store.Summaries());
    }
}
