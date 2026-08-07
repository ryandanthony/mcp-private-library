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
    id             TEXT PRIMARY KEY,
    url            TEXT NOT NULL,
    slug           TEXT NOT NULL,
    canonical_name TEXT NOT NULL,
    summary        TEXT NULL,
    readme_embedding vector({dim}) NULL,
    default_branch TEXT NULL,
    last_commit_sha TEXT NULL,
    current_generation BIGINT NOT NULL DEFAULT 0,
    last_indexed_at TIMESTAMPTZ NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_repositories_slug ON repositories (slug);
CREATE UNIQUE INDEX IF NOT EXISTS ux_repositories_canonical ON repositories (canonical_name);
-- Additive columns for databases created before generation-based reindexing existed.
ALTER TABLE repositories ADD COLUMN IF NOT EXISTS current_generation BIGINT NOT NULL DEFAULT 0;
ALTER TABLE repositories ADD COLUMN IF NOT EXISTS last_indexed_at TIMESTAMPTZ NULL;

CREATE TABLE IF NOT EXISTS jobs (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id   TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
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

-- documents/chunks carry a `generation` (the id of the job that wrote them). Only rows whose
-- generation matches repositories.current_generation are the ""live"" index; a reindex writes an
-- entirely new generation alongside the old one (which stays fully live/searchable throughout)
-- and only deletes the old generation once the new one is completely built, in one transaction.
CREATE TABLE IF NOT EXISTS documents (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    path          TEXT NOT NULL,
    title         TEXT NULL,
    content_hash  TEXT NOT NULL,
    generation    BIGINT NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
ALTER TABLE documents ADD COLUMN IF NOT EXISTS generation BIGINT NOT NULL DEFAULT 0;
-- A path is only unique within a generation now (two generations can both have README.md).
DROP INDEX IF EXISTS ux_documents_repo_path;
CREATE UNIQUE INDEX IF NOT EXISTS ux_documents_repo_gen_path ON documents (repository_id, generation, path);

CREATE TABLE IF NOT EXISTS chunks (
    id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id    BIGINT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    repository_id  TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    generation     BIGINT NOT NULL DEFAULT 0,
    ordinal        INT NOT NULL,
    heading_path   TEXT NULL,
    content        TEXT NOT NULL,
    token_estimate INT NOT NULL DEFAULT 0,
    embedding      vector({dim}) NULL
);
ALTER TABLE chunks ADD COLUMN IF NOT EXISTS generation BIGINT NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS ix_chunks_document ON chunks (document_id);
CREATE INDEX IF NOT EXISTS ix_chunks_repo ON chunks (repository_id);
CREATE INDEX IF NOT EXISTS ix_chunks_repo_gen ON chunks (repository_id, generation);
CREATE INDEX IF NOT EXISTS ix_documents_repo_gen ON documents (repository_id, generation);
");

        await conn.ExecuteAsync(sql.ToString());

        // ANN indexes for cosine similarity search (HNSW). Safe to create empty.
        await conn.ExecuteAsync(@"
CREATE INDEX IF NOT EXISTS ix_chunks_embedding
    ON chunks USING hnsw (embedding vector_cosine_ops);
CREATE INDEX IF NOT EXISTS ix_repositories_readme_embedding
    ON repositories USING hnsw (readme_embedding vector_cosine_ops);");

        _logger.LogInformation("Database schema ensured (vector dim = {Dim}).", dim);
    }
}
