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

    public AuthOptions Auth { get; set; } = new();
}

/// <summary>
/// OIDC/OAuth2 configuration. The identity provider is Keycloak (realm <c>ants</c> at
/// <c>id.ants.zone</c>); this app is a resource server (validates bearer tokens for
/// <c>/api</c> and <c>/mcp</c>) and, for the browser UI, an OIDC relying party (cookie
/// login via the confidential <c>mcp-private-library</c> client).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// Gate for local development: when false, authentication/authorization middleware is
    /// not wired up at all and every endpoint is open. Defaults to true (secure by default);
    /// set to false only in appsettings.Local.json for offline dev without Keycloak running.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>OIDC issuer / JWT authority, e.g. https://id.ants.zone/realms/ants.</summary>
    [Required]
    public string Authority { get; set; } = "https://id.ants.zone/realms/ants";

    /// <summary>
    /// Expected `aud` claim on bearer tokens (RFC 8707 resource indicator), and the
    /// `resource` value advertised in the MCP protected-resource metadata document.
    /// Also used as this server's identifier for the OAuth authorization_servers list.
    /// </summary>
    [Required]
    public string Audience { get; set; } = "https://library.ants.zone";

    /// <summary>Confidential client id used for the browser (cookie+OIDC) login flow.</summary>
    [Required]
    public string ClientId { get; set; } = "mcp-private-library";

    /// <summary>Confidential client secret for the browser login flow. Required in production.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Public client id MCP clients (agents/CLIs) should use for their own OAuth
    /// authorization-code+PKCE flow, surfaced only for documentation/discovery purposes
    /// (the actual value clients use comes from Keycloak's dynamic client registration
    /// or is configured manually in the MCP client).
    /// </summary>
    public string McpClientId { get; set; } = "mcp-private-library-mcp";
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
