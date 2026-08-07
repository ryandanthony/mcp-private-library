using Dapper;
using McpPrivateLibrary.Models;

namespace McpPrivateLibrary.Data;

/// <summary>Data access for repositories, jobs, documents and chunks using Dapper.</summary>
public sealed class LibraryStore
{
    private readonly NpgsqlConnectionFactory _factory;

    public LibraryStore(NpgsqlConnectionFactory factory) => _factory = factory;

    // ---- Repositories -----------------------------------------------------

    public async Task<Repository> UpsertRepositoryAsync(
        string id, string url, string slug, string canonicalName, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO repositories (id, url, slug, canonical_name)
VALUES (@Id, @Url, @Slug, @CanonicalName)
ON CONFLICT (id) DO UPDATE SET url = EXCLUDED.url, slug = EXCLUDED.slug,
    canonical_name = EXCLUDED.canonical_name, updated_at = now()
RETURNING id, url, slug, canonical_name AS CanonicalName, summary,
          default_branch AS DefaultBranch, last_commit_sha AS LastCommitSha,
          current_generation AS CurrentGeneration, last_indexed_at AS LastIndexedAt,
          created_at AS CreatedAt, updated_at AS UpdatedAt;";
        await using var conn = _factory.Create();
        return await conn.QuerySingleAsync<Repository>(new CommandDefinition(
            sql, new { Id = id, Url = url, Slug = slug, CanonicalName = canonicalName }, cancellationToken: ct));
    }

    public async Task<Repository?> GetRepositoryAsync(string id, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, url, slug, canonical_name AS CanonicalName, summary,
       default_branch AS DefaultBranch, last_commit_sha AS LastCommitSha,
       current_generation AS CurrentGeneration, last_indexed_at AS LastIndexedAt,
       created_at AS CreatedAt, updated_at AS UpdatedAt
FROM repositories WHERE id = @Id;";
        await using var conn = _factory.Create();
        return await conn.QuerySingleOrDefaultAsync<Repository>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task UpdateRepositoryCommitAsync(string repoId, string? branch, string? sha, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE repositories SET default_branch = @Branch, last_commit_sha = @Sha, updated_at = now()
WHERE id = @Id;";
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = repoId, Branch = branch, Sha = sha }, cancellationToken: ct));
    }

    /// <summary>Stores the repo-level README summary and its embedding (used for repo search).</summary>
    public async Task UpdateRepositoryReadmeAsync(
        string repoId, string? summary, Pgvector.Vector? embedding, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE repositories SET summary = @Summary, readme_embedding = @Embedding, updated_at = now()
WHERE id = @Id;";
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = repoId, Summary = summary, Embedding = embedding }, cancellationToken: ct));
    }

    // ---- Jobs -------------------------------------------------------------

    public async Task<Job> CreateJobAsync(string repoId, string url, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO jobs (repository_id, url, status)
VALUES (@RepoId, @Url, @Status)
RETURNING id, repository_id AS RepositoryId, url, status, files_total AS FilesTotal,
          files_processed AS FilesProcessed, chunks_total AS ChunksTotal,
          chunks_embedded AS ChunksEmbedded, error, created_at AS CreatedAt, updated_at AS UpdatedAt;";
        await using var conn = _factory.Create();
        return await conn.QuerySingleAsync<Job>(new CommandDefinition(
            sql, new { RepoId = repoId, Url = url, Status = JobStatus.Queued.ToString() }, cancellationToken: ct));
    }

    public async Task<Job?> GetJobAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, repository_id AS RepositoryId, url, status, files_total AS FilesTotal,
       files_processed AS FilesProcessed, chunks_total AS ChunksTotal,
       chunks_embedded AS ChunksEmbedded, error, created_at AS CreatedAt, updated_at AS UpdatedAt
FROM jobs WHERE id = @Id;";
        await using var conn = _factory.Create();
        return await conn.QuerySingleOrDefaultAsync<Job>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<Job?> GetLatestJobForUrlAsync(string url, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, repository_id AS RepositoryId, url, status, files_total AS FilesTotal,
       files_processed AS FilesProcessed, chunks_total AS ChunksTotal,
       chunks_embedded AS ChunksEmbedded, error, created_at AS CreatedAt, updated_at AS UpdatedAt
FROM jobs WHERE url = @Url ORDER BY id DESC LIMIT 1;";
        await using var conn = _factory.Create();
        return await conn.QuerySingleOrDefaultAsync<Job>(new CommandDefinition(sql, new { Url = url }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Job>> ListJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, repository_id AS RepositoryId, url, status, files_total AS FilesTotal,
       files_processed AS FilesProcessed, chunks_total AS ChunksTotal,
       chunks_embedded AS ChunksEmbedded, error, created_at AS CreatedAt, updated_at AS UpdatedAt
FROM jobs ORDER BY id DESC LIMIT @Limit;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<Job>(new CommandDefinition(sql, new { Limit = limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task UpdateJobStatusAsync(long id, JobStatus status, string? error = null, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE jobs SET status = @Status, error = @Error, updated_at = now() WHERE id = @Id;";
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = id, Status = status.ToString(), Error = error }, cancellationToken: ct));
    }

    public async Task UpdateJobProgressAsync(Job job, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE jobs SET status = @Status, files_total = @FilesTotal, files_processed = @FilesProcessed,
    chunks_total = @ChunksTotal, chunks_embedded = @ChunksEmbedded, error = @Error, updated_at = now()
WHERE id = @Id;";
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            job.Id,
            Status = job.Status.ToString(),
            job.FilesTotal,
            job.FilesProcessed,
            job.ChunksTotal,
            job.ChunksEmbedded,
            job.Error
        }, cancellationToken: ct));
    }

    /// <summary>Resets any jobs stuck mid-flight (e.g. after a crash) back to Queued on startup.</summary>
    public async Task<int> RequeueStaleJobsAsync(CancellationToken ct = default)
    {
        const string sql = @"
UPDATE jobs SET status = @Queued, updated_at = now()
WHERE status NOT IN (@Completed, @Failed);";
        await using var conn = _factory.Create();
        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Queued = JobStatus.Queued.ToString(),
            Completed = JobStatus.Completed.ToString(),
            Failed = JobStatus.Failed.ToString()
        }, cancellationToken: ct));
    }

    // ---- Documents & Chunks ----------------------------------------------
    //
    // Ingestion writes into a fresh "generation" (the job id) rather than deleting the current
    // live content first. The live generation (repositories.current_generation) keeps serving
    // reads/search the entire time a (re)index runs. Only once the new generation is fully built
    // does SwapGenerationAsync flip the pointer and drop the old rows, in a single transaction:
    // there is never a moment where the repo's index is empty or half-written. If ingestion fails
    // partway, AbandonGenerationAsync deletes just the incomplete new generation, leaving the
    // still-live old one untouched.

    public async Task<long> InsertDocumentAsync(Document doc, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO documents (repository_id, path, title, content_hash, generation)
VALUES (@RepositoryId, @Path, @Title, @ContentHash, @Generation)
ON CONFLICT (repository_id, generation, path)
DO UPDATE SET title = EXCLUDED.title, content_hash = EXCLUDED.content_hash
RETURNING id;";
        await using var conn = _factory.Create();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            doc.RepositoryId, doc.Path, doc.Title, doc.ContentHash, doc.Generation
        }, cancellationToken: ct));
    }

    public async Task<long> InsertChunkAsync(Chunk chunk, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO chunks (document_id, repository_id, generation, ordinal, heading_path, content, token_estimate)
VALUES (@DocumentId, @RepositoryId, @Generation, @Ordinal, @HeadingPath, @Content, @TokenEstimate)
RETURNING id;";
        await using var conn = _factory.Create();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            chunk.DocumentId, chunk.RepositoryId, chunk.Generation, chunk.Ordinal,
            chunk.HeadingPath, chunk.Content, chunk.TokenEstimate
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Chunk>> GetChunksMissingEmbeddingsAsync(
        string repoId, long generation, int limit, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, document_id AS DocumentId, repository_id AS RepositoryId, generation, ordinal,
       heading_path AS HeadingPath, content, token_estimate AS TokenEstimate
FROM chunks WHERE repository_id = @RepoId AND generation = @Generation AND embedding IS NULL
ORDER BY id LIMIT @Limit;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<Chunk>(new CommandDefinition(
            sql, new { RepoId = repoId, Generation = generation, Limit = limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SetChunkEmbeddingAsync(long chunkId, Pgvector.Vector embedding, CancellationToken ct = default)
    {
        const string sql = "UPDATE chunks SET embedding = @Embedding WHERE id = @Id;";
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = chunkId, Embedding = embedding }, cancellationToken: ct));
    }

    /// <summary>
    /// Atomically activates a newly-built generation and drops the previously-live one, in one
    /// transaction. Also stamps last_indexed_at. Call only after the new generation's documents,
    /// chunks and embeddings are fully written.
    /// </summary>
    public async Task SwapGenerationAsync(string repoId, long newGeneration, CancellationToken ct = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var oldGeneration = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT current_generation FROM repositories WHERE id = @Id FOR UPDATE;",
            new { Id = repoId }, transaction: tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE repositories SET current_generation = @NewGen, last_indexed_at = now(), updated_at = now() WHERE id = @Id;",
            new { Id = repoId, NewGen = newGeneration }, transaction: tx, cancellationToken: ct));

        if (oldGeneration != newGeneration)
        {
            // documents cascade-delete their chunks.
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM documents WHERE repository_id = @Id AND generation = @OldGen;",
                new { Id = repoId, OldGen = oldGeneration }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Deletes an incomplete generation after a failed (re)index, leaving whatever generation is
    /// still marked live (untouched by a failed reindex) fully intact.
    /// </summary>
    public async Task AbandonGenerationAsync(string repoId, long generation, CancellationToken ct = default)
    {
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM documents WHERE repository_id = @Id AND generation = @Generation;",
            new { Id = repoId, Generation = generation }, cancellationToken: ct));
    }

    // ---- Search -----------------------------------------------------------

    /// <summary>Semantic search over document chunks, optionally narrowed to one repository by ID.</summary>
    /// <remarks>Only searches each repository's live generation, so an in-progress reindex never
    /// surfaces partial/duplicate results.</remarks>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        Pgvector.Vector queryEmbedding, int topK, string? repositoryId, CancellationToken ct = default)
    {
        // Cosine distance operator <=> ; score = 1 - distance.
        const string sql = @"
SELECT r.id AS RepositoryId,
       r.slug AS RepositorySlug,
       d.path AS DocumentPath,
       c.heading_path AS HeadingPath,
       c.content AS Content,
       1 - (c.embedding <=> @Query) AS Score
FROM chunks c
JOIN documents d ON d.id = c.document_id
JOIN repositories r ON r.id = c.repository_id
WHERE c.embedding IS NOT NULL
  AND c.generation = r.current_generation
  AND (@RepoId IS NULL OR r.id = @RepoId)
ORDER BY c.embedding <=> @Query
LIMIT @TopK;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<SearchResult>(new CommandDefinition(
            sql, new { Query = queryEmbedding, TopK = topK, RepoId = repositoryId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Repository-level semantic search over the root-README embeddings. Lets a caller first find
    /// the right repo/tool, then narrow document search to that repository by ID.
    /// </summary>
    public async Task<IReadOnlyList<RepositorySearchResult>> SearchRepositoriesAsync(
        Pgvector.Vector queryEmbedding, int topK, CancellationToken ct = default)
    {
        const string sql = @"
SELECT r.id AS RepositoryId, r.slug AS Slug, r.url AS Url, r.summary AS Summary,
       (SELECT COUNT(*) FROM documents d WHERE d.repository_id = r.id AND d.generation = r.current_generation) AS Documents,
       (SELECT COUNT(*) FROM chunks c WHERE c.repository_id = r.id AND c.generation = r.current_generation) AS Chunks,
       1 - (r.readme_embedding <=> @Query) AS Score
FROM repositories r
WHERE r.readme_embedding IS NOT NULL
ORDER BY r.readme_embedding <=> @Query
LIMIT @TopK;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<RepositorySearchResult>(new CommandDefinition(
            sql, new { Query = queryEmbedding, TopK = topK }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RepositorySearchResult>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT r.id AS RepositoryId, r.slug AS Slug, r.url AS Url, r.summary AS Summary,
       COUNT(DISTINCT d.id) AS Documents,
       COUNT(DISTINCT c.id) AS Chunks,
       0.0 AS Score
FROM repositories r
LEFT JOIN documents d ON d.repository_id = r.id AND d.generation = r.current_generation
LEFT JOIN chunks c ON c.repository_id = r.id AND c.generation = r.current_generation
GROUP BY r.id, r.slug, r.url, r.summary
ORDER BY r.slug;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<RepositorySearchResult>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// One row per repository, merging document/chunk counts with the repository's latest job
    /// (status + progress). Powers the "Indexed repositories" screen where each submitted repo
    /// is a single line combining repo stats, progress and the most recent job.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryOverview>> GetRepositoryOverviewAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT r.id AS Id, r.slug AS Slug, r.url AS Url,
       (SELECT COUNT(*) FROM documents d WHERE d.repository_id = r.id AND d.generation = r.current_generation) AS Documents,
       (SELECT COUNT(*) FROM chunks c WHERE c.repository_id = r.id AND c.generation = r.current_generation) AS Chunks,
       COALESCE(j.status, 'None') AS Status,
       j.id AS JobId,
       COALESCE(j.files_total, 0) AS FilesTotal,
       COALESCE(j.files_processed, 0) AS FilesProcessed,
       COALESCE(j.chunks_total, 0) AS ChunksTotal,
       COALESCE(j.chunks_embedded, 0) AS ChunksEmbedded,
       j.error AS Error,
       COALESCE(j.updated_at, r.updated_at) AS UpdatedAt,
       r.last_indexed_at AS LastIndexedAt
FROM repositories r
LEFT JOIN LATERAL (
    SELECT id, status, files_total, files_processed, chunks_total, chunks_embedded, error, updated_at
    FROM jobs jj WHERE jj.repository_id = r.id ORDER BY jj.id DESC LIMIT 1
) j ON TRUE
ORDER BY COALESCE(j.updated_at, r.updated_at) DESC, r.slug;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<RepositoryOverview>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }
}
