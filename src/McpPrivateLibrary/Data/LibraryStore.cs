using Dapper;
using McpPrivateLibrary.Models;

namespace McpPrivateLibrary.Data;

/// <summary>Data access for repositories, jobs, documents and chunks using Dapper.</summary>
public sealed class LibraryStore
{
    private readonly NpgsqlConnectionFactory _factory;

    public LibraryStore(NpgsqlConnectionFactory factory) => _factory = factory;

    // ---- Repositories -----------------------------------------------------

    public async Task<Repository> UpsertRepositoryAsync(string url, string slug, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO repositories (url, slug)
VALUES (@Url, @Slug)
ON CONFLICT (slug) DO UPDATE SET url = EXCLUDED.url, updated_at = now()
RETURNING id, url, slug, default_branch AS DefaultBranch, last_commit_sha AS LastCommitSha,
          created_at AS CreatedAt, updated_at AS UpdatedAt;";
        await using var conn = _factory.Create();
        return await conn.QuerySingleAsync<Repository>(
            new CommandDefinition(sql, new { Url = url, Slug = slug }, cancellationToken: ct));
    }

    public async Task UpdateRepositoryCommitAsync(long repoId, string? branch, string? sha, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE repositories SET default_branch = @Branch, last_commit_sha = @Sha, updated_at = now()
WHERE id = @Id;";
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = repoId, Branch = branch, Sha = sha }, cancellationToken: ct));
    }

    // ---- Jobs -------------------------------------------------------------

    public async Task<Job> CreateJobAsync(long repoId, string url, CancellationToken ct = default)
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

    public async Task<IReadOnlyList<Job>> ClaimQueuedJobsAsync(int limit, CancellationToken ct = default)
    {
        // Atomically claim queued jobs so a worker won't double-process. Uses SKIP LOCKED.
        const string sql = @"
WITH claimed AS (
    SELECT id FROM jobs WHERE status = @Queued ORDER BY id LIMIT @Limit FOR UPDATE SKIP LOCKED
)
UPDATE jobs j SET status = @Cloning, updated_at = now()
FROM claimed WHERE j.id = claimed.id
RETURNING j.id, j.repository_id AS RepositoryId, j.url, j.status, j.files_total AS FilesTotal,
          j.files_processed AS FilesProcessed, j.chunks_total AS ChunksTotal,
          j.chunks_embedded AS ChunksEmbedded, j.error, j.created_at AS CreatedAt, j.updated_at AS UpdatedAt;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<Job>(new CommandDefinition(sql, new
        {
            Queued = JobStatus.Queued.ToString(),
            Cloning = JobStatus.Cloning.ToString(),
            Limit = limit
        }, cancellationToken: ct));
        return rows.ToList();
    }

    // ---- Documents & Chunks ----------------------------------------------

    /// <summary>Removes all documents/chunks for a repo so re-ingestion starts clean.</summary>
    public async Task ClearRepositoryContentAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM documents WHERE repository_id = @Id;", new { Id = repoId }, cancellationToken: ct));
    }

    public async Task<long> InsertDocumentAsync(Document doc, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO documents (repository_id, path, title, content_hash)
VALUES (@RepositoryId, @Path, @Title, @ContentHash)
ON CONFLICT (repository_id, path)
DO UPDATE SET title = EXCLUDED.title, content_hash = EXCLUDED.content_hash
RETURNING id;";
        await using var conn = _factory.Create();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            doc.RepositoryId, doc.Path, doc.Title, doc.ContentHash
        }, cancellationToken: ct));
    }

    public async Task<long> InsertChunkAsync(Chunk chunk, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO chunks (document_id, repository_id, ordinal, heading_path, content, token_estimate)
VALUES (@DocumentId, @RepositoryId, @Ordinal, @HeadingPath, @Content, @TokenEstimate)
RETURNING id;";
        await using var conn = _factory.Create();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            chunk.DocumentId, chunk.RepositoryId, chunk.Ordinal, chunk.HeadingPath, chunk.Content, chunk.TokenEstimate
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Chunk>> GetChunksMissingEmbeddingsAsync(long repoId, int limit, CancellationToken ct = default)
    {
        const string sql = @"
SELECT id, document_id AS DocumentId, repository_id AS RepositoryId, ordinal,
       heading_path AS HeadingPath, content, token_estimate AS TokenEstimate
FROM chunks WHERE repository_id = @RepoId AND embedding IS NULL ORDER BY id LIMIT @Limit;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<Chunk>(new CommandDefinition(
            sql, new { RepoId = repoId, Limit = limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SetChunkEmbeddingAsync(long chunkId, Pgvector.Vector embedding, CancellationToken ct = default)
    {
        const string sql = "UPDATE chunks SET embedding = @Embedding WHERE id = @Id;";
        await using var conn = _factory.Create();
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = chunkId, Embedding = embedding }, cancellationToken: ct));
    }

    // ---- Search -----------------------------------------------------------

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        Pgvector.Vector queryEmbedding, int topK, string? repoSlug, CancellationToken ct = default)
    {
        // Cosine distance operator <=> ; score = 1 - distance.
        const string sql = @"
SELECT r.slug AS RepositorySlug,
       d.path AS DocumentPath,
       c.heading_path AS HeadingPath,
       c.content AS Content,
       1 - (c.embedding <=> @Query) AS Score
FROM chunks c
JOIN documents d ON d.id = c.document_id
JOIN repositories r ON r.id = c.repository_id
WHERE c.embedding IS NOT NULL
  AND (@Slug IS NULL OR r.slug = @Slug)
ORDER BY c.embedding <=> @Query
LIMIT @TopK;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync<SearchResult>(new CommandDefinition(
            sql, new { Query = queryEmbedding, TopK = topK, Slug = repoSlug }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<(string Slug, string Url, int Documents, int Chunks)>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT r.slug AS Slug, r.url AS Url,
       COUNT(DISTINCT d.id) AS Documents,
       COUNT(DISTINCT c.id) AS Chunks
FROM repositories r
LEFT JOIN documents d ON d.repository_id = r.id
LEFT JOIN chunks c ON c.repository_id = r.id
GROUP BY r.id, r.slug, r.url
ORDER BY r.slug;";
        await using var conn = _factory.Create();
        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(x => ((string)x.slug, (string)x.url, (int)(long)x.documents, (int)(long)x.chunks)).ToList();
    }
}
