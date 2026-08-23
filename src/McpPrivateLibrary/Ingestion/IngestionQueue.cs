using System.Collections.Concurrent;
using System.Threading.Channels;
using McpPrivateLibrary.Configuration;
using McpPrivateLibrary.Data;
using McpPrivateLibrary.Models;
using McpPrivateLibrary.Services;
using Microsoft.Extensions.Options;

namespace McpPrivateLibrary.Ingestion;

public sealed record JobSubmission(long JobId, JobStatus Status, string Message, JobCreationOutcome Outcome);

public interface IJobSubmitter
{
    /// <summary>
    /// Validates the URL, upserts the repo, and creates + enqueues a job for it -- unless the
    /// repo already has a job in flight, or (when <paramref name="force"/> is false) it was
    /// indexed more recently than <see cref="LibraryOptions.MinReindexInterval"/> ago, in which
    /// case no new job is created and the existing/skip reason is reported back instead.
    /// </summary>
    /// <param name="force">
    /// True to bypass the recent-index cooldown (the explicit Reindex action). Submissions from
    /// the "Index a repo" form / plain POST /api/jobs should pass false.
    /// </param>
    Task<JobSubmission> SubmitAsync(string url, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Same contract as <see cref="SubmitAsync"/>, but for a website source: <paramref name="url"/>
    /// is the start page and <paramref name="crawlSameDomain"/> selects a same-host crawl instead
    /// of a single-page scrape.
    /// </summary>
    Task<JobSubmission> SubmitWebAsync(
        string url, bool crawlSameDomain, int? maxPages, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Forces a reindex of an already-known repository by id, routing to <see cref="SubmitAsync"/>
    /// or <see cref="SubmitWebAsync"/> based on the repository's stored <see cref="RepositorySourceType"/>
    /// rather than assuming git. Centralizing the branch here means every caller (the REST endpoint,
    /// MCP tools, ...) gets the right pipeline for web sources instead of hitting the GitHub URL
    /// parser with a non-GitHub URL. Returns null if no repository with that id exists.
    /// </summary>
    Task<JobSubmission?> ReindexAsync(string repositoryId, CancellationToken ct = default);

    /// <summary>
    /// Requests cancellation of a job that isn't already terminal (Completed/Failed/Cancelled).
    /// A job currently running has its pipeline's <see cref="CancellationToken"/> signalled, so it
    /// stops at the next checkpoint (typically within one HTTP request / DB write) and the worker
    /// marks it Cancelled. A job still sitting in Queued (enqueued but not yet dequeued by the
    /// worker) is marked Cancelled directly; the worker skips it without ever starting it. Returns
    /// false if the job id doesn't exist or is already terminal.
    /// </summary>
    Task<bool> TryCancelAsync(long jobId, CancellationToken ct = default);
}

/// <summary>
/// Accepts job submissions and hands them to the background worker via an in-process channel.
/// The job row is the source of truth for progress; the channel is just the wakeup signal.
/// </summary>
public sealed class IngestionQueue : IJobSubmitter
{
    private readonly LibraryStore _store;
    private readonly LibraryOptions _options;
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    // Tracks the CancellationTokenSource actively driving each in-flight job's pipeline, keyed
    // by job id. Registered by IngestionWorker just before it starts processing a job and removed
    // once processing finishes (success, failure, or cancellation). A job id with no entry here is
    // either not yet picked up by the worker, or already terminal -- TryCancel reports false for
    // either case, which is surfaced to the caller as "nothing to cancel."
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    public IngestionQueue(LibraryStore store, IOptions<LibraryOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public ChannelReader<long> Reader => _channel.Reader;

    /// <summary>
    /// Registers the token source that will drive <paramref name="jobId"/>'s pipeline, so a later
    /// <see cref="TryCancel"/> call can find and signal it. Called by the worker right before
    /// <see cref="IngestionService.ProcessAsync"/> starts.
    /// </summary>
    public void RegisterRunning(long jobId, CancellationTokenSource cts) => _running[jobId] = cts;

    /// <summary>Removes the tracked token source once a job finishes, regardless of outcome.</summary>
    public void UnregisterRunning(long jobId) => _running.TryRemove(jobId, out _);

    public async Task<bool> TryCancelAsync(long jobId, CancellationToken ct = default)
    {
        // Running (or about to run): signal the pipeline's token. The worker's finally block
        // sets the DB status to Cancelled once ProcessAsync unwinds, so we don't touch it here --
        // doing so would race the worker's own terminal-status write for the same row.
        if (_running.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Fell through: the job just finished and got unregistered/disposed between the
                // lookup and the cancel. Fall back to the DB check below.
            }
        }

        // Not running: only a job still sitting in Queued can be meaningfully cancelled here.
        // Anything else (already Completed/Failed/Cancelled, or unknown id) is a no-op.
        var job = await _store.GetJobAsync(jobId, ct);
        if (job is null || job.Status != JobStatus.Queued) return false;

        job.Status = JobStatus.Cancelled;
        await _store.UpdateJobProgressAsync(job, ct);
        return true;
    }

    public async Task<JobSubmission?> ReindexAsync(string repositoryId, CancellationToken ct = default)
    {
        var repo = await _store.GetRepositoryAsync(repositoryId, ct);
        if (repo is null) return null;

        // Route by the repo's actual source type rather than assuming git: SubmitAsync runs the
        // GitHub-only URL parser, which rejects a web repo's non-GitHub URL outright (that's the
        // "Only GitHub HTTPS or SSH clone URLs are supported" error for a web-sourced reindex).
        return repo.SourceType == RepositorySourceType.Web
            ? await SubmitWebAsync(repo.Url, repo.CrawlSameDomain, repo.MaxPages, force: true, ct)
            : await SubmitAsync(repo.Url, force: true, ct);
    }

    public async Task<JobSubmission> SubmitAsync(string url, bool force = false, CancellationToken ct = default)
    {
        if (!GitHubUrlParser.TryParse(url, out var repoRef, out var error))
            return new JobSubmission(0, JobStatus.Failed, error ?? "Invalid URL.", JobCreationOutcome.InvalidUrl);

        var repo = await _store.UpsertRepositoryAsync(
            repoRef.Id, repoRef.CloneUrl, repoRef.Slug, repoRef.CanonicalName,
            RepositorySourceType.Git, crawlSameDomain: false, maxPages: null, ct);

        var result = await _store.TryCreateJobAsync(repo.Id, repoRef.CloneUrl, force, _options.MinReindexInterval, ct);

        return await FinishSubmissionAsync(repo, repoRef.CloneUrl, repoRef.Slug, result, force, ct);
    }

    public async Task<JobSubmission> SubmitWebAsync(
        string url, bool crawlSameDomain, int? maxPages, bool force = false, CancellationToken ct = default)
    {
        if (!WebUrlParser.TryParse(url, crawlSameDomain, out var webRef, out var error, maxPages))
            return new JobSubmission(0, JobStatus.Failed, error ?? "Invalid URL.", JobCreationOutcome.InvalidUrl);

        var repo = await _store.UpsertRepositoryAsync(
            webRef.Id, webRef.StartUrl, webRef.Slug, webRef.CanonicalName,
            RepositorySourceType.Web, webRef.CrawlSameDomain, maxPages, ct);

        var result = await _store.TryCreateJobAsync(repo.Id, webRef.StartUrl, force, _options.MinReindexInterval, ct);

        return await FinishSubmissionAsync(repo, webRef.StartUrl, webRef.Slug, result, force, ct);
    }

    private async Task<JobSubmission> FinishSubmissionAsync(
        Repository repo, string url, string slug, JobCreationResult result, bool force, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case JobCreationOutcome.AlreadyInFlight:
                return new JobSubmission(
                    result.Job!.Id, result.Job.Status,
                    $"{slug} (id {repo.Id}) is already indexing (job #{result.Job.Id}, {result.Job.Status}). Not queuing a duplicate.",
                    JobCreationOutcome.AlreadyInFlight);

            case JobCreationOutcome.TooRecent:
                var since = DateTimeOffset.UtcNow - result.LastIndexedAt!.Value;
                return new JobSubmission(
                    0, JobStatus.Failed,
                    $"{slug} (id {repo.Id}) was already indexed {FormatSince(since)} ago (within the {FormatInterval(_options.MinReindexInterval)} cooldown). Use Reindex to force a refresh.",
                    JobCreationOutcome.TooRecent);

            default: // Created
                await _channel.Writer.WriteAsync(result.Job!.Id, ct);
                return new JobSubmission(
                    result.Job.Id, JobStatus.Queued, $"Queued ingestion for {slug} (id {repo.Id}).",
                    JobCreationOutcome.Created);
        }
    }

    /// <summary>Re-signals any jobs already sitting in Queued (e.g. reloaded after a restart).</summary>
    public async Task EnqueueAsync(long jobId, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(jobId, ct);

    private static string FormatSince(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{span.TotalDays:F1} day(s)";
        if (span.TotalHours >= 1) return $"{span.TotalHours:F1} hour(s)";
        return $"{Math.Max(1, (int)span.TotalMinutes)} minute(s)";
    }

    private static string FormatInterval(TimeSpan span)
    {
        if (span.TotalDays >= 1 && span.TotalDays % 1 == 0) return $"{(int)span.TotalDays}-day";
        if (span.TotalHours >= 1) return $"{span.TotalHours:F0}-hour";
        return $"{(int)span.TotalMinutes}-minute";
    }
}
