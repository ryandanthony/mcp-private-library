using McpPrivateLibrary.Data;

namespace McpPrivateLibrary.Ingestion;

/// <summary>
/// Background service that drains the ingestion queue and processes jobs one at a time.
/// On startup it re-queues any jobs left mid-flight by a previous run.
/// </summary>
public sealed class IngestionWorker : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LibraryStore _store;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        IngestionQueue queue,
        IServiceScopeFactory scopeFactory,
        LibraryStore store,
        ILogger<IngestionWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverStaleJobsAsync(stoppingToken);

        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var job = await _store.GetJobAsync(jobId, stoppingToken);
                if (job is null)
                {
                    _logger.LogWarning("Queued job {JobId} no longer exists; skipping.", jobId);
                    continue;
                }

                // Each job gets its own DI scope so scoped services (e.g. Npgsql) are fresh.
                using var scope = _scopeFactory.CreateScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();
                await ingestion.ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing job {JobId}.", jobId);
            }
        }
    }

    private async Task RecoverStaleJobsAsync(CancellationToken ct)
    {
        try
        {
            var requeued = await _store.RequeueStaleJobsAsync(ct);
            if (requeued > 0)
                _logger.LogInformation("Re-queued {Count} stale job(s) from a previous run.", requeued);

            // Re-signal every queued job so the worker picks them up.
            var jobs = await _store.ListJobsAsync(200, ct);
            foreach (var job in jobs.Where(j => j.Status == Models.JobStatus.Queued))
                await _queue.EnqueueAsync(job.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover stale jobs on startup.");
        }
    }
}
