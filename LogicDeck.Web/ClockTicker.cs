using LogicDeck.Core;
using LogicDeck.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LogicDeck.Web;

public sealed class ClockTicker : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(500);

    private readonly CircuitStore _store;
    private readonly IHubContext<CollabHub, ICollabClient> _hub;
    private readonly ILogger<ClockTicker> _logger;

    public ClockTicker(CircuitStore store, IHubContext<CollabHub, ICollabClient> hub, ILogger<ClockTicker> logger)
    {
        _store = store;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var (circuit, frame) in _store.TickClocks())
                    await _hub.Clients.Group(HubGroups.Room(circuit.Id)).Signals(frame);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Clock tick failed");
            }
        }
    }
}
