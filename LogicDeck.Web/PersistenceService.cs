using LogicDeck.Core;

namespace LogicDeck.Web;

/// <summary>
/// Periodically flushes circuits that changed since the last write, and does a final
/// flush on shutdown. Dirty tracking lives in <see cref="CircuitStore"/>; this service
/// only owns the cadence. Failing to save is logged, never fatal.
/// </summary>
public sealed class PersistenceService : BackgroundService
{
    private readonly CircuitStore _store;
    private readonly ILogger<PersistenceService> _logger;
    private readonly TimeSpan _interval;

    public PersistenceService(CircuitStore store, ILogger<PersistenceService> logger, TimeSpan interval)
    {
        _store = store;
        _logger = logger;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Flush();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            Flush();
        }
    }

    private void Flush()
    {
        try
        {
            _store.FlushDirty();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Circuit persistence flush failed");
        }
    }
}
