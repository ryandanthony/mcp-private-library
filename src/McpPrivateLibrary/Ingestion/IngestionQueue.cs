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

    public IngestionQueue(LibraryStore store, IOptions<LibraryOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public ChannelReader<long> Reader => _channel.Reader;

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
