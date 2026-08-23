namespace McpPrivateLibrary.Models;

public enum JobStatus
{
    Queued,
    Cloning,
    /// <summary>Fetching pages of a website (crawl or single-page). The web-source equivalent of Cloning.</summary>
    Scraping,
    Discovering,
    Chunking,
    Embedding,
    Completed,
    Failed,
    /// <summary>User-requested stop of an in-flight job. Terminal, like Completed/Failed:
    /// never re-queued on restart and never counted as "in flight" for dedupe purposes.</summary>
    Cancelled
}

/// <summary>Where a repository's content comes from, driving which ingestion pipeline runs.</summary>
public enum RepositorySourceType
{
    /// <summary>Cloned from a GitHub repository; Markdown files discovered on disk.</summary>
    Git,
    /// <summary>Scraped from a website (single page or same-domain crawl) and converted to Markdown.</summary>
    Web,
}

public sealed class Repository
{
    /// <summary>Stable hash ID: sha256("github.com/owner/repo")[..16] (or the web equivalent).</summary>
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Normalized owner/name, e.g. "org/repo" (git) or a host[/path] (web).</summary>
    public string Slug { get; set; } = "";
    /// <summary>Provider-qualified canonical name, e.g. "github.com/org/repo" or "web-crawl:docs.example.com".</summary>
    public string CanonicalName { get; set; } = "";
    /// <summary>Short summary (derived from the root README, or the start page for websites) used for repo-level search display.</summary>
    public string? Summary { get; set; }
    public string? DefaultBranch { get; set; }
    public string? LastCommitSha { get; set; }
    /// <summary>Whether this repository's content is cloned from git or scraped from the web.</summary>
    public RepositorySourceType SourceType { get; set; } = RepositorySourceType.Git;
    /// <summary>Web sources only: whether ingestion crawls same-host links from the start URL rather than fetching only that one page.</summary>
    public bool CrawlSameDomain { get; set; }
    /// <summary>Web sources only: optional cap on the number of pages a crawl will fetch. Null means no limit.</summary>
    public int? MaxPages { get; set; }
    /// <summary>
    /// Generation number of the currently-live documents/chunks. Ingestion writes new content
    /// under a fresh generation (the job id) alongside the current one, then atomically swaps
    /// (deletes the old generation, activates the new) once the new one is fully embedded. Reads
    /// always filter to this value, so an in-progress reindex is invisible until it completes.
    /// </summary>
    public long CurrentGeneration { get; set; }
    /// <summary>When the current generation finished successfully (null if never fully indexed).</summary>
    public DateTimeOffset? LastIndexedAt { get; set; }
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

public enum JobCreationOutcome
{
    /// <summary>A new job was created and should be enqueued.</summary>
    Created,
    /// <summary>A job for this repository is already queued/running; no new job was created.</summary>
    AlreadyInFlight,
    /// <summary>The repository was indexed too recently; no new job was created.</summary>
    TooRecent,
    /// <summary>The submitted URL couldn't be parsed as a GitHub repo URL.</summary>
    InvalidUrl,
}

/// <summary>Result of <see cref="Data.LibraryStore.TryCreateJobAsync"/>.</summary>
public sealed class JobCreationResult
{
    public JobCreationOutcome Outcome { get; private init; }
    /// <summary>The newly-created job (Created), or the existing in-flight job (AlreadyInFlight).</summary>
    public Job? Job { get; private init; }
    /// <summary>When the repo was last successfully indexed (TooRecent only).</summary>
    public DateTimeOffset? LastIndexedAt { get; private init; }

    public static JobCreationResult Created(Job job) => new() { Outcome = JobCreationOutcome.Created, Job = job };
    public static JobCreationResult AlreadyInFlight(Job job) => new() { Outcome = JobCreationOutcome.AlreadyInFlight, Job = job };
    public static JobCreationResult TooRecent(DateTimeOffset lastIndexedAt) => new() { Outcome = JobCreationOutcome.TooRecent, LastIndexedAt = lastIndexedAt };
}

/// <summary>
/// A long-lived, user-scoped credential for non-interactive callers (MCP hosts, CLIs, scripts)
/// that can't run an OAuth authorization-code flow. Presented as
/// <c>Authorization: ApiKey mcpl_&lt;KeyId&gt;_&lt;secret&gt;</c>.
///
/// Only the SHA-256 of the secret half is persisted (<see cref="SecretHash"/>); the plaintext is
/// returned exactly once at creation and is unrecoverable afterwards. <see cref="KeyId"/> is the
/// public, non-secret half: it's what the lookup indexes on, so verification is a single indexed
/// read plus one constant-time hash comparison rather than a scan-and-compare over every key.
/// </summary>
public sealed class ApiKey
{
    public long Id { get; set; }
    /// <summary>Public identifier embedded in the token; used to look the key up. Not a secret.</summary>
    public string KeyId { get; set; } = "";
    /// <summary>Base64 SHA-256 of the secret half of the token.</summary>
    public string SecretHash { get; set; } = "";
    /// <summary>Owning user's stable IdP subject claim (`sub`). Keys are scoped per user.</summary>
    public string OwnerSubject { get; set; } = "";
    /// <summary>Display name of the owner at creation time, for showing in the UI/audit.</summary>
    public string? OwnerName { get; set; }
    /// <summary>User-supplied label, e.g. "jcode laptop".</summary>
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Optional hard expiry. Null means the key lives until revoked.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>
    /// Coarse last-use timestamp for spotting stale keys. Written opportunistically (at most once
    /// per <see cref="Auth.ApiKeyAuthenticationHandler"/> throttle window) so a busy key doesn't
    /// turn every authenticated request into a database write.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>Set when the user revokes the key. Revoked keys are kept for audit, never deleted.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt is not null;
    public bool IsExpired => ExpiresAt is not null && ExpiresAt <= DateTimeOffset.UtcNow;
    public bool IsActive => !IsRevoked && !IsExpired;
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
    /// <summary>
    /// Which ingestion generation this row belongs to (the job id that wrote it). Only rows whose
    /// generation matches the owning repository's <see cref="Repository.CurrentGeneration"/> are
    /// "live"; older/abandoned generations are cleaned up after a successful or failed reindex.
    /// </summary>
    public long Generation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Chunk
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public string RepositoryId { get; set; } = "";
    /// <summary>Same generation as the owning <see cref="Document"/>; denormalized for fast filtering.</summary>
    public long Generation { get; set; }
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
    /// <summary>When the currently-live index generation finished successfully (null if never indexed).</summary>
    public DateTimeOffset? LastIndexedAt { get; set; }
}
