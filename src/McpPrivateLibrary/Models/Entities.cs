namespace McpPrivateLibrary.Models;

public enum JobStatus
{
    Queued,
    Cloning,
    Discovering,
    Chunking,
    Embedding,
    Completed,
    Failed
}

public sealed class Repository
{
    /// <summary>Stable hash ID: sha256("github.com/owner/repo")[..16].</summary>
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Normalized owner/name, e.g. "org/repo".</summary>
    public string Slug { get; set; } = "";
    /// <summary>Provider-qualified canonical name, e.g. "github.com/org/repo".</summary>
    public string CanonicalName { get; set; } = "";
    /// <summary>Short summary (derived from the root README) used for repo-level search display.</summary>
    public string? Summary { get; set; }
    public string? DefaultBranch { get; set; }
    public string? LastCommitSha { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Job
{
    public long Id { get; set; }
    public string RepositoryId { get; set; } = "";
    public string Url { get; set; } = "";
    public JobStatus Status { get; set; }
    public int FilesTotal { get; set; }
    public int FilesProcessed { get; set; }
    public int ChunksTotal { get; set; }
    public int ChunksEmbedded { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Document
{
    public long Id { get; set; }
    public string RepositoryId { get; set; } = "";
    /// <summary>Path relative to the repo root.</summary>
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>SHA-256 of the file contents, used to skip unchanged files.</summary>
    public string ContentHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Chunk
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public string RepositoryId { get; set; } = "";
    public int Ordinal { get; set; }
    /// <summary>Heading breadcrumb, e.g. "Guide &gt; Installation".</summary>
    public string? HeadingPath { get; set; }
    public string Content { get; set; } = "";
    public int TokenEstimate { get; set; }
}

/// <summary>A document-chunk search hit returned to callers / MCP clients.</summary>
public sealed class SearchResult
{
    public string RepositoryId { get; set; } = "";
    public string RepositorySlug { get; set; } = "";
    public string DocumentPath { get; set; } = "";
    public string? HeadingPath { get; set; }
    public string Content { get; set; } = "";
    public double Score { get; set; }
}

/// <summary>A repository-level search hit (matches on the root README embedding).</summary>
public sealed class RepositorySearchResult
{
    public string RepositoryId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Summary { get; set; }
    public int Documents { get; set; }
    public int Chunks { get; set; }
    public double Score { get; set; }
}

/// <summary>
/// One row per indexed repository, merging repo stats with its latest ingestion job so the UI
/// can render a single line per submitted repo (repos + progress + recent job combined).
/// </summary>
public sealed class RepositoryOverview
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Url { get; set; } = "";
    public int Documents { get; set; }
    public int Chunks { get; set; }
    /// <summary>Latest job status, or "None" if the repo has no job.</summary>
    public string Status { get; set; } = "None";
    public long? JobId { get; set; }
    public int FilesTotal { get; set; }
    public int FilesProcessed { get; set; }
    public int ChunksTotal { get; set; }
    public int ChunksEmbedded { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
