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

                // Cancelled while still sitting in the channel (IngestionQueue.TryCancelAsync
                // marks a Queued job Cancelled directly since there's no running pipeline yet to
                // signal). Don't start it.
                if (job.Status == Models.JobStatus.Cancelled)
                {
                    _logger.LogInformation("Job {JobId}: skipping, cancelled before it started.", jobId);
                    continue;
                }

                // Own token source for this job, linked to the app's shutdown token so a normal
                // shutdown still stops the pipeline promptly. Registered before processing starts
                // so a concurrent cancel request can always find it; always unregistered and
                // disposed afterwards regardless of outcome.
                using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _queue.RegisterRunning(jobId, jobCts);
                try
                {
                    // Each job gets its own DI scope so scoped services (e.g. Npgsql) are fresh.
                    using var scope = _scopeFactory.CreateScope();
                    var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();
                    await ingestion.ProcessAsync(job, jobCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Cancelled via the API (jobCts, not the app shutting down): IngestionService
                    // already unwound its try/finally (cleaned up the abandoned generation etc.)
                    // and rethrew. Record the terminal status here since ProcessAsync's own catch
                    // block only handles ordinary failures, not user-requested cancellation.
                    job.Status = Models.JobStatus.Cancelled;
                    await _store.UpdateJobProgressAsync(job, CancellationToken.None);
                    _logger.LogInformation("Job {JobId}: cancelled by request.", jobId);
                }
                finally
                {
                    _queue.UnregisterRunning(jobId);
                }
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
