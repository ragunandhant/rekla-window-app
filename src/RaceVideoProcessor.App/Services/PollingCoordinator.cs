using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.App.Services;

public sealed class PollingCoordinator : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly EntrySyncService _syncService;
    private readonly IEntryProviderRouter _router;
    private readonly ILocalStateRepository _repository;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset? _lastSuccessUtc;

    public event EventHandler<SyncSnapshot>? SnapshotUpdated;
    public event EventHandler<PollStatus>? StatusUpdated;

    public PollingCoordinator(
        AppSettings settings,
        EntrySyncService syncService,
        IEntryProviderRouter router,
        ILocalStateRepository repository,
        IAppLog log)
    {
        _settings = settings;
        _syncService = syncService;
        _router = router;
        _repository = repository;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loopTask is not null)
            return;
        var persisted = await _repository.LoadSyncStateAsync(cancellationToken).ConfigureAwait(false);
        _lastSuccessUtc = persisted.LastSuccessUtc;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
    }

    public Task PollNowAsync(CancellationToken cancellationToken = default)
        => PollOnceAsync(cancellationToken);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            var delay = TimeSpan.FromSeconds(_settings.PollingIntervalSeconds);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _pollGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        var attempt = DateTimeOffset.UtcNow;
        var providerName = _router.Current.Name;
        try
        {
            _log.Info($"{providerName} polling started.");
            var snapshot = await _syncService.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
            _lastSuccessUtc = snapshot.SynchronizedAtUtc;
            await _repository.SaveSyncStateAsync(
                new SyncState(attempt, _lastSuccessUtc, null), cancellationToken).ConfigureAwait(false);
            SnapshotUpdated?.Invoke(this, snapshot);
            StatusUpdated?.Invoke(this, new PollStatus(
                true, _lastSuccessUtc,
                DateTimeOffset.UtcNow.AddSeconds(_settings.PollingIntervalSeconds),
                null, providerName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"{providerName} polling failed: {ex.Message}");
            await _repository.SaveSyncStateAsync(
                new SyncState(attempt, _lastSuccessUtc, ex.Message), CancellationToken.None).ConfigureAwait(false);
            StatusUpdated?.Invoke(this, new PollStatus(
                false, _lastSuccessUtc,
                DateTimeOffset.UtcNow.AddSeconds(_settings.PollingIntervalSeconds),
                ex.Message, providerName));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }
}
