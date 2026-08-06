using System.ComponentModel.DataAnnotations;

namespace McpPrivateLibrary.Configuration;

public sealed class LibraryOptions
{
    public const string SectionName = "Library";

    /// <summary>Postgres connection string.</summary>
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=mcp_library;Username=postgres;Password=postgres";

    /// <summary>Directory where repositories are cloned for processing.</summary>
    public string WorkDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "mcp-private-library");

    /// <summary>Delete the cloned repo from disk once ingestion finishes.</summary>
    public bool CleanupClones { get; set; } = true;

    public EmbeddingOptions Embedding { get; set; } = new();

    public ChunkingOptions Chunking { get; set; } = new();
}

public sealed class EmbeddingOptions
{
    /// <summary>OpenRouter API key. Required; the app fails fast at startup if it is missing.</summary>
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string Model { get; set; } = "openai/text-embedding-3-small";

    /// <summary>Vector dimension for the chosen model. text-embedding-3-small = 1536.</summary>
    [Range(1, 8192)]
    public int Dimensions { get; set; } = 1536;

    /// <summary>How many chunks to send to the embeddings endpoint per request.</summary>
    [Range(1, 256)]
    public int BatchSize { get; set; } = 32;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class ChunkingOptions
{
    /// <summary>Target maximum characters per chunk.</summary>
    [Range(200, 20000)]
    public int MaxChars { get; set; } = 2000;

    /// <summary>Character overlap between adjacent chunks split from the same section.</summary>
    [Range(0, 4000)]
    public int Overlap { get; set; } = 200;
}
