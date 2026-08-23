using McpPrivateLibrary.Configuration;
using McpPrivateLibrary.Data;
using McpPrivateLibrary.Models;
using McpPrivateLibrary.Services;
using Microsoft.Extensions.Options;

namespace McpPrivateLibrary.Ingestion;

/// <summary>One item to chunk/embed/persist, regardless of where its Markdown came from.</summary>
internal sealed record SourceDocument(string Path, string? Title, string Content);

/// <summary>
/// Runs the full ingestion pipeline for a single job. The fetch step branches on the
/// repository's <see cref="RepositorySourceType"/>: Git clones the repo and discovers Markdown
/// files on disk; Web scrapes one page or a same-host crawl via <see cref="WebScraperService"/>.
/// From there both sources share the same chunk -> embed -> persist pipeline, updating job
/// progress as it goes.
/// </summary>
public sealed class IngestionService
{
    private readonly LibraryStore _store;
    private readonly GitCloneService _git;
    private readonly WebScraperService _webScraper;
    private readonly MarkdownProcessor _markdown;
    private readonly IEmbeddingService _embeddings;
    private readonly LibraryOptions _options;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        LibraryStore store,
        GitCloneService git,
        WebScraperService webScraper,
        MarkdownProcessor markdown,
        IEmbeddingService embeddings,
        IOptions<LibraryOptions> options,
        ILogger<IngestionService> logger)
    {
        _store = store;
        _git = git;
        _webScraper = webScraper;
        _markdown = markdown;
        _embeddings = embeddings;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(Job job, CancellationToken ct)
    {
        var repo = await _store.GetRepositoryAsync(job.RepositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found.");

        // Each job builds its documents/chunks under a brand-new generation (the job's own id,
        // which is unique and monotonic) rather than clearing the repo's current content first.
        // The previous generation stays fully live and searchable for the entire fetch/chunk/embed
        // run; only SwapGenerationAsync (called once everything below succeeds) atomically drops
        // it and activates this one. A failure at any point before that leaves the repo's existing
        // index completely untouched -- no partial/empty-index window, ever.
        var generation = job.Id;
        var swapped = false;
        string? cloneDir = null;

        try
        {
            IReadOnlyList<SourceDocument> docs;
            string summarySource;

            if (repo.SourceType == RepositorySourceType.Web)
            {
                (docs, summarySource) = await FetchWebAsync(job, repo, ct);
            }
            else
            {
                string workRoot = string.IsNullOrWhiteSpace(_options.WorkDirectory)
                    ? Path.Combine(Path.GetTempPath(), "mcp-private-library")
                    : _options.WorkDirectory;
                cloneDir = Path.Combine(workRoot, job.RepositoryId.ToString(), "repo");

                (docs, summarySource) = await FetchGitAsync(job, cloneDir, ct);
            }

            job.ChunksTotal = 0;
            job.ChunksEmbedded = 0;
            await _store.UpdateJobProgressAsync(job, ct);

            if (docs.Count == 0)
            {
                // Nothing to index: still swap so a reindex of a now-empty source actually clears
                // out whatever the previous generation had (e.g. all docs were deleted upstream).
                await _store.SwapGenerationAsync(job.RepositoryId, generation, ct);
                swapped = true;
                job.Status = JobStatus.Completed;
                await _store.UpdateJobProgressAsync(job, ct);
                return;
            }

            // Chunk + persist documents/chunks (into the new generation).
            job.Status = JobStatus.Chunking;
            await _store.UpdateJobProgressAsync(job, ct);

            foreach (var doc in docs)
            {
                ct.ThrowIfCancellationRequested();

                var chunkCount = await ChunkAndPersistAsync(job.RepositoryId, generation, doc, ct);
                job.FilesProcessed++;
                job.ChunksTotal += chunkCount;
                await _store.UpdateJobProgressAsync(job, ct);
            }

            // Embed all chunks in the new generation, in batches.
            job.Status = JobStatus.Embedding;
            await _store.UpdateJobProgressAsync(job, ct);
            await EmbedPendingAsync(job, generation, ct);

            // Embed a repo-level summary vector so the repo itself is searchable.
            await EmbedRepositorySummaryAsync(job, summarySource, ct);

            // Everything for the new generation is fully built: atomically make it live and
            // drop the old one. This is the only write that ever removes previously-live content.
            await _store.SwapGenerationAsync(job.RepositoryId, generation, ct);
            swapped = true;

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
            // Failed (or cancelled) before the swap: clean up the incomplete generation so it
            // doesn't linger as orphaned rows. The previously-live generation (if any) was never
            // touched and keeps serving search/MCP queries throughout.
            if (!swapped)
            {
                try { await _store.AbandonGenerationAsync(job.RepositoryId, generation, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Job {JobId}: failed to clean up abandoned generation {Gen}", job.Id, generation); }
            }

            if (cloneDir is not null && _options.CleanupClones)
            {
                try { GitCloneService.DeleteDirectory(cloneDir); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up clone at {Path}", cloneDir); }
            }
        }
    }

    /// <summary>Clones the repo and reads its Markdown files. Unreadable files are skipped (logged,
    /// still counted as processed) rather than failing the whole job.</summary>
    private async Task<(IReadOnlyList<SourceDocument> Docs, string SummarySource)> FetchGitAsync(
        Job job, string cloneDir, CancellationToken ct)
    {
        if (!GitHubUrlParser.TryParse(job.Url, out var repoRef, out var parseError))
            throw new InvalidOperationException(parseError ?? "Invalid URL.");

        job.Status = JobStatus.Cloning;
        await _store.UpdateJobProgressAsync(job, ct);
        var clone = await _git.CloneAsync(repoRef.CloneUrl, cloneDir, ct);
        await _store.UpdateRepositoryCommitAsync(job.RepositoryId, clone.Branch, clone.CommitSha, ct);

        job.Status = JobStatus.Discovering;
        await _store.UpdateJobProgressAsync(job, ct);
        var files = _markdown.Discover(clone.LocalPath);
        job.FilesTotal = files.Count;
        job.FilesProcessed = 0;
        await _store.UpdateJobProgressAsync(job, ct);
        _logger.LogInformation("Job {JobId}: found {Count} markdown files.", job.Id, files.Count);

        MarkdownFile? readme = files
            .Where(f => !f.RelativePath.Contains('/'))
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.RelativePath)
                .Equals("README", StringComparison.OrdinalIgnoreCase));
        string? readmeContent = null;

        var docs = new List<SourceDocument>();
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

            if (readme is not null && file.RelativePath == readme.RelativePath)
                readmeContent = content;

            docs.Add(new SourceDocument(file.RelativePath, MarkdownProcessor.ExtractTitle(content), content));
        }

        // No README: synthesize a description from the URL so the repo is still searchable.
        if (readmeContent is null)
            _logger.LogInformation("Job {JobId}: no root README; using URL for repo embedding.", job.Id);

        return (docs, readmeContent ?? job.Url);
    }

    /// <summary>Scrapes a single page or a same-host crawl and converts each page to a document.</summary>
    private async Task<(IReadOnlyList<SourceDocument> Docs, string SummarySource)> FetchWebAsync(
        Job job, Repository repo, CancellationToken ct)
    {
        if (!WebUrlParser.TryParse(job.Url, repo.CrawlSameDomain, out var webRef, out var parseError, repo.MaxPages))
            throw new InvalidOperationException(parseError ?? "Invalid URL.");

        job.Status = JobStatus.Scraping;
        await _store.UpdateJobProgressAsync(job, ct);
        var pages = await _webScraper.ScrapeAsync(webRef, ct, async (processed, known, token) =>
        {
            job.FilesProcessed = processed;
            job.FilesTotal = known;
            await _store.UpdateJobProgressAsync(job, token);
        });

        job.FilesTotal = pages.Count;
        job.FilesProcessed = 0;
        await _store.UpdateJobProgressAsync(job, ct);
        _logger.LogInformation("Job {JobId}: scraped {Count} page(s) from {Host}.", job.Id, pages.Count, webRef.Host);

        var docs = pages.Select(p => new SourceDocument(p.Path, p.Title, p.Markdown)).ToList();

        // Prefer the exact start page for the repo-level summary; fall back to the first page
        // scraped (e.g. if the start URL redirected elsewhere).
        var startPage = pages.FirstOrDefault(p => string.Equals(p.Url, webRef.StartUrl, StringComparison.OrdinalIgnoreCase))
            ?? pages[0];

        return (docs, startPage.Markdown);
    }

    private async Task<int> ChunkAndPersistAsync(string repositoryId, long generation, SourceDocument doc, CancellationToken ct)
    {
        var docRow = new Document
        {
            RepositoryId = repositoryId,
            Path = doc.Path,
            Title = doc.Title,
            ContentHash = MarkdownProcessor.ComputeHash(doc.Content),
            Generation = generation
        };
        var docId = await _store.InsertDocumentAsync(docRow, ct);

        var chunks = _markdown.Chunk(doc.Content);
        foreach (var chunk in chunks)
        {
            await _store.InsertChunkAsync(new Chunk
            {
                DocumentId = docId,
                RepositoryId = repositoryId,
                Generation = generation,
                Ordinal = chunk.Ordinal,
                HeadingPath = chunk.HeadingPath,
                Content = chunk.Content,
                TokenEstimate = chunk.TokenEstimate
            }, ct);
        }

        return chunks.Count;
    }

    private async Task EmbedPendingAsync(Job job, long generation, CancellationToken ct)
    {
        var batchSize = _options.Embedding.BatchSize;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await _store.GetChunksMissingEmbeddingsAsync(job.RepositoryId, generation, batchSize, ct);
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
    /// Stores a short summary of <paramref name="summarySource"/> and embeds it as the repo-level
    /// vector used by repository search. Best-effort: doesn't fail the whole ingestion if it fails.
    /// </summary>
    private async Task EmbedRepositorySummaryAsync(Job job, string summarySource, CancellationToken ct)
    {
        try
        {
            var summary = BuildSummary(summarySource);
            var embedding = await _embeddings.EmbedOneAsync(summary, ct);
            await _store.UpdateRepositoryReadmeAsync(job.RepositoryId, summary, embedding, ct);
            _logger.LogInformation("Job {JobId}: stored repo-level summary embedding.", job.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Repo-level embedding is best-effort; don't fail the whole ingestion over it.
            _logger.LogWarning(ex, "Job {JobId}: failed to embed repo summary.", job.Id);
        }
    }

    /// <summary>Produces a compact, embedding-friendly summary from README/page text.</summary>
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
