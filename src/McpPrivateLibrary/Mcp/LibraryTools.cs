using System.ComponentModel;
using System.Text;
using McpPrivateLibrary.Data;
using McpPrivateLibrary.Models;
using McpPrivateLibrary.Services;
using ModelContextProtocol.Server;

namespace McpPrivateLibrary.Mcp;

/// <summary>
/// MCP tools that expose the private documentation library to MCP clients.
/// Provides semantic search over indexed docs, repository listing, and job
/// progress inspection. Tool methods are instance methods; their dependencies
/// (<see cref="LibraryStore"/> and <see cref="IEmbeddingService"/>) are supplied
/// by dependency injection.
/// </summary>
[McpServerToolType]
public sealed class LibraryTools
{
    /// <summary>Approximate maximum length of a content snippet included in search output.</summary>
    private const int SnippetLength = 500;

    private readonly LibraryStore _store;
    private readonly IEmbeddingService _embeddings;

    /// <summary>
    /// Creates the tool set.
    /// </summary>
    /// <param name="store">Data access for repositories, jobs and search.</param>
    /// <param name="embeddings">Embedding service used to encode search queries.</param>
    public LibraryTools(LibraryStore store, IEmbeddingService embeddings)
    {
        _store = store;
        _embeddings = embeddings;
    }

    /// <summary>
    /// Semantically searches the indexed Markdown documentation and returns the
    /// closest matching chunks. The query is embedded with the same model used
    /// during ingestion, then compared against stored chunk embeddings.
    /// </summary>
    /// <param name="query">Natural-language search query.</param>
    /// <param name="topK">Maximum number of hits to return (defaults to 5).</param>
    /// <param name="repositorySlug">Optional "owner/repo" slug to restrict the search to a single repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A formatted, human-readable list of search hits.</returns>
    [McpServerTool]
    [Description("Semantically search the indexed private documentation library. Embeds the query and returns the most relevant documentation chunks, each with its repository, document path, heading breadcrumb, similarity score and a content snippet. Optionally restrict to a single repository by its 'owner/repo' slug.")]
    public async Task<string> search_docs(
        [Description("Natural-language search query to find relevant documentation.")] string query,
        [Description("Maximum number of results to return (default 5).")] int topK = 5,
        [Description("Optional repository slug in 'owner/repo' form to limit the search to one repository.")] string? repositorySlug = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Please provide a non-empty search query.";

        if (topK < 1)
            topK = 1;

        var slug = string.IsNullOrWhiteSpace(repositorySlug) ? null : repositorySlug.Trim();

        var embedding = await _embeddings.EmbedOneAsync(query, cancellationToken);
        var results = await _store.SearchAsync(embedding, topK, slug, cancellationToken);

        if (results.Count == 0)
        {
            var scope = slug is null ? "the library" : $"repository '{slug}'";
            return $"No matching documentation found in {scope} for query: \"{query}\".";
        }

        var sb = new StringBuilder();
        sb.Append("Found ").Append(results.Count)
          .Append(results.Count == 1 ? " result" : " results")
          .Append(" for \"").Append(query).Append("\":").AppendLine().AppendLine();

        var rank = 1;
        foreach (var hit in results)
        {
            sb.Append(rank++).Append(". ")
              .Append(hit.RepositorySlug).Append(" — ").Append(hit.DocumentPath).AppendLine();

            if (!string.IsNullOrWhiteSpace(hit.HeadingPath))
                sb.Append("   Heading: ").Append(hit.HeadingPath).AppendLine();

            sb.Append("   Score: ").Append(hit.Score.ToString("F3")).AppendLine();
            sb.Append("   ").Append(Snippet(hit.Content)).AppendLine();
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Lists every repository that has been ingested into the library, along
    /// with the number of documents and chunks stored for each.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A formatted list of repositories, or a notice when none exist.</returns>
    [McpServerTool]
    [Description("List all repositories currently indexed in the private documentation library, including each repository's slug, source URL, document count and chunk count.")]
    public async Task<string> list_repositories(CancellationToken cancellationToken = default)
    {
        var repos = await _store.ListRepositoriesAsync(cancellationToken);

        if (repos.Count == 0)
            return "No repositories have been indexed yet.";

        var sb = new StringBuilder();
        sb.Append(repos.Count)
          .Append(repos.Count == 1 ? " repository indexed:" : " repositories indexed:")
          .AppendLine().AppendLine();

        foreach (var repo in repos)
        {
            sb.Append("- ").Append(repo.Slug).AppendLine();
            sb.Append("   URL: ").Append(repo.Url).AppendLine();
            sb.Append("   Documents: ").Append(repo.Documents)
              .Append(", Chunks: ").Append(repo.Chunks).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reports the current status and progress of an ingestion job by id.
    /// </summary>
    /// <param name="jobId">Identifier of the ingestion job to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A formatted status summary, or a not-found notice.</returns>
    [McpServerTool]
    [Description("Get the status and progress of a documentation ingestion job by its numeric id, including current stage, files processed vs. total, chunks embedded vs. total, and any error message.")]
    public async Task<string> job_status(
        [Description("The numeric id of the ingestion job to inspect.")] long jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _store.GetJobAsync(jobId, cancellationToken);

        if (job is null)
            return $"No job found with id {jobId}.";

        var sb = new StringBuilder();
        sb.Append("Job #").Append(job.Id).Append(" — ").Append(job.Url).AppendLine();
        sb.Append("Status: ").Append(job.Status).AppendLine();
        sb.Append("Files: ").Append(job.FilesProcessed).Append(" / ").Append(job.FilesTotal)
          .Append(" processed").AppendLine();
        sb.Append("Chunks: ").Append(job.ChunksEmbedded).Append(" / ").Append(job.ChunksTotal)
          .Append(" embedded").AppendLine();

        if (!string.IsNullOrWhiteSpace(job.Error))
            sb.Append("Error: ").Append(job.Error).AppendLine();

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Produces a single-line, whitespace-collapsed snippet of chunk content,
    /// trimmed to roughly <see cref="SnippetLength"/> characters.
    /// </summary>
    /// <param name="content">The raw chunk content.</param>
    /// <returns>A trimmed snippet suitable for inline display.</returns>
    private static string Snippet(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var collapsed = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= SnippetLength
            ? collapsed
            : collapsed[..SnippetLength].TrimEnd() + "…";
    }
}
