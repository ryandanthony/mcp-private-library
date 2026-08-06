using McpPrivateLibrary.Configuration;
using McpPrivateLibrary.Data;
using McpPrivateLibrary.Models;
using McpPrivateLibrary.Services;
using Microsoft.Extensions.Options;

namespace McpPrivateLibrary.Ingestion;

/// <summary>
/// Runs the full ingestion pipeline for a single job:
/// clone -> discover markdown -> chunk -> embed -> persist, updating job progress as it goes.
/// </summary>
public sealed class IngestionService
{
    private readonly LibraryStore _store;
    private readonly GitCloneService _git;
    private readonly MarkdownProcessor _markdown;
    private readonly IEmbeddingService _embeddings;
    private readonly LibraryOptions _options;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        LibraryStore store,
        GitCloneService git,
        MarkdownProcessor markdown,
        IEmbeddingService embeddings,
        IOptions<LibraryOptions> options,
        ILogger<IngestionService> logger)
    {
        _store = store;
        _git = git;
        _markdown = markdown;
        _embeddings = embeddings;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(Job job, CancellationToken ct)
    {
        var workRoot = string.IsNullOrWhiteSpace(_options.WorkDirectory)
            ? Path.Combine(Path.GetTempPath(), "mcp-private-library")
            : _options.WorkDirectory;
        var dest = Path.Combine(workRoot, job.RepositoryId.ToString(), "repo");

        try
        {
            if (!GitHubUrlParser.TryParse(job.Url, out var repoRef, out var parseError))
                throw new InvalidOperationException(parseError ?? "Invalid URL.");

            // 1. Clone
            job.Status = JobStatus.Cloning;
            await _store.UpdateJobProgressAsync(job, ct);
            var clone = await _git.CloneAsync(repoRef.CloneUrl, dest, ct);
            await _store.UpdateRepositoryCommitAsync(job.RepositoryId, clone.Branch, clone.CommitSha, ct);

            // Re-ingest cleanly: drop prior documents/chunks for this repo.
            await _store.ClearRepositoryContentAsync(job.RepositoryId, ct);

            // 2. Discover
            job.Status = JobStatus.Discovering;
            await _store.UpdateJobProgressAsync(job, ct);
            var files = _markdown.Discover(clone.LocalPath);
            job.FilesTotal = files.Count;
            job.FilesProcessed = 0;
            job.ChunksTotal = 0;
            job.ChunksEmbedded = 0;
            await _store.UpdateJobProgressAsync(job, ct);
            _logger.LogInformation("Job {JobId}: found {Count} markdown files.", job.Id, files.Count);

            if (files.Count == 0)
            {
                job.Status = JobStatus.Completed;
                await _store.UpdateJobProgressAsync(job, ct);
                return;
            }

            // 3. Chunk + persist documents/chunks
            job.Status = JobStatus.Chunking;
            await _store.UpdateJobProgressAsync(job, ct);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                string content;
                try
                {
                    content = await File.ReadAllTextAsync(file.AbsolutePath, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Job {JobId}: skipping unreadable file {Path}", job.Id, file.RelativePath);
                    job.FilesProcessed++;
                    await _store.UpdateJobProgressAsync(job, ct);
                    continue;
                }

                var doc = new Document
                {
                    RepositoryId = job.RepositoryId,
                    Path = file.RelativePath,
                    Title = MarkdownProcessor.ExtractTitle(content),
                    ContentHash = MarkdownProcessor.ComputeHash(content)
                };
                var docId = await _store.InsertDocumentAsync(doc, ct);

                var chunks = _markdown.Chunk(content);
                foreach (var chunk in chunks)
                {
                    await _store.InsertChunkAsync(new Chunk
                    {
                        DocumentId = docId,
                        RepositoryId = job.RepositoryId,
                        Ordinal = chunk.Ordinal,
                        HeadingPath = chunk.HeadingPath,
                        Content = chunk.Content,
                        TokenEstimate = chunk.TokenEstimate
                    }, ct);
                }

                job.FilesProcessed++;
                job.ChunksTotal += chunks.Count;
                await _store.UpdateJobProgressAsync(job, ct);
            }

            // 4. Embed all chunks that still need vectors, in batches.
            job.Status = JobStatus.Embedding;
            await _store.UpdateJobProgressAsync(job, ct);
            await EmbedPendingAsync(job, ct);

            // 5. Embed the root README as a repo-level vector so the repo itself is searchable.
            await EmbedRepositoryReadmeAsync(job, clone.LocalPath, files, ct);

            job.Status = JobStatus.Completed;
            await _store.UpdateJobProgressAsync(job, ct);
            _logger.LogInformation("Job {JobId}: completed ({Chunks} chunks).", job.Id, job.ChunksEmbedded);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId}: cancelled.", job.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: failed.", job.Id);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            await _store.UpdateJobProgressAsync(job, CancellationToken.None);
        }
        finally
        {
            if (_options.CleanupClones)
            {
                try { GitCloneService.DeleteDirectory(dest); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up clone at {Path}", dest); }
            }
        }
    }

    private async Task EmbedPendingAsync(Job job, CancellationToken ct)
    {
        var batchSize = _options.Embedding.BatchSize;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await _store.GetChunksMissingEmbeddingsAsync(job.RepositoryId, batchSize, ct);
            if (batch.Count == 0) break;

            var vectors = await _embeddings.EmbedAsync(batch.Select(c => c.Content).ToList(), ct);
            for (var i = 0; i < batch.Count; i++)
            {
                await _store.SetChunkEmbeddingAsync(batch[i].Id, vectors[i], ct);
            }
            job.ChunksEmbedded += batch.Count;
            await _store.UpdateJobProgressAsync(job, ct);
        }
    }

    /// <summary>
    /// Finds the repository's root README, stores a short summary, and embeds it as the repo-level
    /// vector used by repository search. Falls back gracefully if no README is present.
    /// </summary>
    private async Task EmbedRepositoryReadmeAsync(Job job, string repoRoot, IReadOnlyList<MarkdownFile> files, CancellationToken ct)
    {
        try
        {
            // Prefer a root-level README (README.md / readme.markdown etc.), case-insensitive.
            var readme = files
                .Where(f => !f.RelativePath.Contains('/'))
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.RelativePath)
                    .Equals("README", StringComparison.OrdinalIgnoreCase));

            string summarySource;
            if (readme is not null)
            {
                summarySource = await File.ReadAllTextAsync(readme.AbsolutePath, ct);
            }
            else
            {
                // No README: synthesize a description from the slug so the repo is still searchable.
                _logger.LogInformation("Job {JobId}: no root README; using slug for repo embedding.", job.Id);
                summarySource = job.Url;
            }

            var summary = BuildSummary(summarySource);
            // Embed the summary text (bounded) as the repo-level vector.
            var embedding = await _embeddings.EmbedOneAsync(summary, ct);
            await _store.UpdateRepositoryReadmeAsync(job.RepositoryId, summary, embedding, ct);
            _logger.LogInformation("Job {JobId}: stored repo-level README embedding.", job.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Repo-level embedding is best-effort; don't fail the whole ingestion over it.
            _logger.LogWarning(ex, "Job {JobId}: failed to embed root README.", job.Id);
        }
    }

    /// <summary>Produces a compact, embedding-friendly summary from README text.</summary>
    private static string BuildSummary(string content)
    {
        content = content.Replace("\r\n", "\n");
        // Drop YAML front-matter if present.
        if (content.StartsWith("---", StringComparison.Ordinal))
        {
            var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end >= 0)
            {
                var after = content.IndexOf('\n', end + 1);
                if (after >= 0) content = content[(after + 1)..];
            }
        }
        var trimmed = content.Trim();
        const int max = 2000;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
