using System.Text;
using Dapper;
using McpPrivateLibrary.Configuration;
using Microsoft.Extensions.Options;

namespace McpPrivateLibrary.Data;

/// <summary>
/// Applies the database schema idempotently on startup. Keeps things simple: no external
/// migration framework, just CREATE ... IF NOT EXISTS statements plus the pgvector extension.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly LibraryOptions _options;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        NpgsqlConnectionFactory factory,
        IOptions<LibraryOptions> options,
        ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dim = _options.Embedding.Dimensions;
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        // pgvector extension must exist before we create vector columns.
        await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;");

        var sql = new StringBuilder();
        sql.Append($@"
CREATE TABLE IF NOT EXISTS repositories (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    url           TEXT NOT NULL,
    slug          TEXT NOT NULL,
    default_branch TEXT NULL,
    last_commit_sha TEXT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_repositories_slug ON repositories (slug);

CREATE TABLE IF NOT EXISTS jobs (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id   BIGINT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    url             TEXT NOT NULL,
    status          TEXT NOT NULL,
    files_total     INT NOT NULL DEFAULT 0,
    files_processed INT NOT NULL DEFAULT 0,
    chunks_total    INT NOT NULL DEFAULT 0,
    chunks_embedded INT NOT NULL DEFAULT 0,
    error           TEXT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs (status);
CREATE INDEX IF NOT EXISTS ix_jobs_repo ON jobs (repository_id);

CREATE TABLE IF NOT EXISTS documents (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id BIGINT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    path          TEXT NOT NULL,
    title         TEXT NULL,
    content_hash  TEXT NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_documents_repo_path ON documents (repository_id, path);

CREATE TABLE IF NOT EXISTS chunks (
    id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id    BIGINT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    repository_id  BIGINT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    ordinal        INT NOT NULL,
    heading_path   TEXT NULL,
    content        TEXT NOT NULL,
    token_estimate INT NOT NULL DEFAULT 0,
    embedding      vector({dim}) NULL
);
CREATE INDEX IF NOT EXISTS ix_chunks_document ON chunks (document_id);
CREATE INDEX IF NOT EXISTS ix_chunks_repo ON chunks (repository_id);
");

        await conn.ExecuteAsync(sql.ToString());

        // ANN index for cosine similarity search. IVFFlat needs data to train, so it's fine to
        // create empty; it will be usable once rows exist. HNSW would also work but is heavier.
        await conn.ExecuteAsync(@"
CREATE INDEX IF NOT EXISTS ix_chunks_embedding
    ON chunks USING hnsw (embedding vector_cosine_ops);");

        _logger.LogInformation("Database schema ensured (vector dim = {Dim}).", dim);
    }
}
