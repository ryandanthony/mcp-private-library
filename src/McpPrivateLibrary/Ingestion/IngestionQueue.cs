using System.Threading.Channels;
using McpPrivateLibrary.Data;
using McpPrivateLibrary.Models;
using McpPrivateLibrary.Services;

namespace McpPrivateLibrary.Ingestion;

public sealed record JobSubmission(long JobId, JobStatus Status, string Message);

public interface IJobSubmitter
{
    /// <summary>Validates the URL, upserts the repo, creates a queued job, and enqueues it for processing.</summary>
    Task<JobSubmission> SubmitAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Accepts job submissions and hands them to the background worker via an in-process channel.
/// The job row is the source of truth for progress; the channel is just the wakeup signal.
/// </summary>
public sealed class IngestionQueue : IJobSubmitter
{
    private readonly LibraryStore _store;
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public IngestionQueue(LibraryStore store) => _store = store;

    public ChannelReader<long> Reader => _channel.Reader;

    public async Task<JobSubmission> SubmitAsync(string url, CancellationToken ct = default)
    {
        if (!GitHubUrlParser.TryParse(url, out var repoRef, out var error))
            return new JobSubmission(0, JobStatus.Failed, error ?? "Invalid URL.");

        var repo = await _store.UpsertRepositoryAsync(repoRef.CloneUrl, repoRef.Slug, ct);
        var job = await _store.CreateJobAsync(repo.Id, repoRef.CloneUrl, ct);
        await _channel.Writer.WriteAsync(job.Id, ct);
        return new JobSubmission(job.Id, JobStatus.Queued, $"Queued ingestion for {repoRef.Slug}.");
    }

    /// <summary>Re-signals any jobs already sitting in Queued (e.g. reloaded after a restart).</summary>
    public async Task EnqueueAsync(long jobId, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(jobId, ct);
}
