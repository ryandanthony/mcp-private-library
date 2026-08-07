using McpPrivateLibrary.Configuration;
using McpPrivateLibrary.Data;
using McpPrivateLibrary.Ingestion;
using McpPrivateLibrary.Mcp;
using McpPrivateLibrary.Models;
using McpPrivateLibrary.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Teach Dapper how to read/write pgvector's `vector` type as Pgvector.Vector.
Dapper.SqlMapper.AddTypeHandler(new Pgvector.Dapper.VectorTypeHandler());

// Local (git-ignored) settings can hold the OpenRouter key without committing secrets.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ---- Options ----------------------------------------------------------------
builder.Services
    .AddOptions<LibraryOptions>()
    .Bind(builder.Configuration.GetSection(LibraryOptions.SectionName))
    .ValidateDataAnnotations();

// ---- Data / services --------------------------------------------------------
builder.Services.AddSingleton<NpgsqlConnectionFactory>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<LibraryStore>();

builder.Services.AddSingleton<GitCloneService>();
builder.Services.AddSingleton(sp =>
    new MarkdownProcessor(sp.GetRequiredService<IOptions<LibraryOptions>>().Value.Chunking));

builder.Services.AddHttpClient<IEmbeddingService, OpenRouterEmbeddingService>();

// Ingestion pipeline + background worker.
builder.Services.AddScoped<IngestionService>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddSingleton<IJobSubmitter>(sp => sp.GetRequiredService<IngestionQueue>());
builder.Services.AddHostedService<IngestionWorker>();

// ---- MCP server (Streamable HTTP transport) ---------------------------------
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<LibraryTools>();

// ---- Authentication / authorization ------------------------------------------
// Identity provider is Keycloak (realm `ants` at id.ants.zone). Two ways in:
//   - Browser UI (index.html + /api/*): cookie session via OIDC authorization code
//     flow against the confidential `mcp-private-library` client.
//   - API / MCP clients (curl, agents, MCP hosts): JWT bearer tokens, validated
//     directly against Keycloak's JWKS. No client secret needed for these callers.
// `/mcp` additionally publishes RFC 9728 Protected Resource Metadata via AddMcp()
// so MCP clients can self-discover the authorization server and required scopes.
var authOptions = builder.Configuration.GetSection(LibraryOptions.SectionName).Get<LibraryOptions>()?.Auth
    ?? new AuthOptions();

if (authOptions.Enabled)
{
    builder.Services
        .AddAuthentication(options =>
        {
            // "Smart" policy scheme below picks Bearer vs. Cookie per-request; this is
            // both the default authenticate AND challenge scheme (a bare 401/redirect
            // with no other signal falls back to the cookie scheme).
            options.DefaultScheme = "Smart";
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        // Chooses Bearer (-> MCP RFC 9728 challenge) for API/MCP callers that send an
        // Authorization header, Cookie for browser navigations/fetches that don't.
        // Applies uniformly to authenticate AND challenge, so /api and /mcp both "just
        // work" for either kind of caller without per-endpoint scheme wiring.
        .AddPolicyScheme("Smart", "Bearer or Cookie", options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.Count > 0
                    ? McpAuthenticationDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authOptions.Authority;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = authOptions.Authority,
                ValidateAudience = true,
                ValidAudience = authOptions.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                NameClaimType = "preferred_username",
                RoleClaimType = "roles",
            };
        })
        // Adds the /.well-known/oauth-protected-resource endpoint and the
        // WWW-Authenticate: Bearer resource_metadata="..." challenge header. Its
        // ForwardAuthenticate defaults to "Bearer", so it authenticates via JwtBearer
        // above and only adds the RFC 9728 metadata/header on top.
        .AddMcp(options =>
        {
            options.ResourceMetadata = new()
            {
                Resource = authOptions.Audience,
                ResourceName = "MCP Private Library",
                AuthorizationServers = { authOptions.Authority },
                ScopesSupported = ["openid", "profile", "mcp:tools"],
            };
        })
        // Cookie session for the browser UI (index.html + same-origin fetch calls to
        // /api). Never redirects on its own — /api and /mcp must return a plain
        // 401/403 for fetch()/MCP clients; the SPA drives an explicit /auth/login
        // navigation instead (see the auth endpoints below).
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "mcp-library.auth";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.Authority = authOptions.Authority;
            options.ClientId = authOptions.ClientId;
            options.ClientSecret = authOptions.ClientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            // The app only needs the user's identity (claims), never replays the id/access/
            // refresh tokens downstream, so don't stuff them into the auth cookie. With
            // SaveTokens=true the cookie (plus the id_token, access_token, refresh_token,
            // and their expiry) can exceed nginx's default proxy header buffer size and
            // blow up /signin-oidc with "upstream sent too big header".
            options.SaveTokens = false;
            // Keycloak advertises a PAR endpoint but doesn't require it
            // (require_pushed_authorization_requests=false); the .NET OIDC handler's
            // "UseIfAvailable" default then attempts PAR and fails against Keycloak's
            // PAR endpoint. Plain authorization-code + PKCE is sufficient here.
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.GetClaimsFromUserInfoEndpoint = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidAudience = authOptions.ClientId,
                NameClaimType = "preferred_username",
            };
        });

    builder.Services.AddAuthorization();
}

var app = builder.Build();

// Fail fast if embeddings aren't configured: no key means no ingestion or search.
{
    var embedding = app.Services.GetRequiredService<IOptions<LibraryOptions>>().Value.Embedding;
    if (!embedding.HasApiKey)
    {
        app.Logger.LogCritical(
            "No OpenRouter API key configured. Set Library:Embedding:ApiKey (e.g. in appsettings.Local.json). There is no offline fallback.");
        throw new InvalidOperationException("Library:Embedding:ApiKey is required.");
    }
}

// Apply the schema (and pgvector extension) before serving traffic.
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();

if (authOptions.Enabled)
{
    // Behind nginx (TLS-terminating reverse proxy at library.ants.zone): trust its
    // X-Forwarded-Proto/Host so redirect_uri generation and the `Secure` cookie flag
    // are computed correctly instead of assuming plain http://.
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        // nginx is the only hop; no known proxy IP list needed on a private LAN box.
        KnownIPNetworks = { },
        KnownProxies = { },
    });

    app.UseAuthentication();
    app.UseAuthorization();

    // ---- Auth endpoints (browser UI) -----------------------------------------
    var auth = app.MapGroup("/auth");

    // Kicks off the OIDC authorization-code flow; Keycloak redirects back to
    // /signin-oidc, which signs the user into the cookie scheme and then redirects
    // here again to `returnUrl` (defaults to the app root).
    auth.MapGet("/login", (string? returnUrl, HttpContext ctx) =>
        Results.Challenge(
            new AuthenticationProperties { RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl },
            [OpenIdConnectDefaults.AuthenticationScheme]));

    auth.MapPost("/logout", (HttpContext ctx) =>
        Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

    // Lets the SPA render a login/logout state without guessing from cookie presence.
    auth.MapGet("/me", (ClaimsPrincipal user) =>
    {
        if (user.Identity?.IsAuthenticated != true)
            return Results.Ok(new { authenticated = false });

        return Results.Ok(new
        {
            authenticated = true,
            name = user.Identity.Name,
            email = user.FindFirstValue("email"),
        });
    });
}

// ---- HTTP API ---------------------------------------------------------------
var api = app.MapGroup("/api");
if (authOptions.Enabled)
    api.RequireAuthorization();

api.MapPost("/jobs", async (SubmitRequest req, IJobSubmitter submitter, CancellationToken ct) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "A GitHub URL is required." });

    var result = await submitter.SubmitAsync(req.Url, ct);
    if (result.Status == JobStatus.Failed)
        return Results.BadRequest(new { error = result.Message });

    return Results.Ok(new { jobId = result.JobId, status = result.Status.ToString(), message = result.Message });
});

api.MapGet("/jobs", async (LibraryStore store, CancellationToken ct) =>
{
    var jobs = await store.ListJobsAsync(50, ct);
    return Results.Ok(jobs.Select(ToDto));
});

api.MapGet("/jobs/{id:long}", async (long id, LibraryStore store, CancellationToken ct) =>
{
    var job = await store.GetJobAsync(id, ct);
    return job is null ? Results.NotFound(new { error = "Job not found." }) : Results.Ok(ToDto(job));
});

// Reindex an already-submitted repository: re-runs the same clone -> chunk -> embed pipeline
// against a fresh generation, then atomically swaps it in for the old one (see LibraryStore's
// SwapGenerationAsync / IngestionService). The current index stays fully live and searchable
// for the entire duration; nothing is cleared upfront.
api.MapPost("/repositories/{id}/reindex", async (string id, LibraryStore store, IJobSubmitter submitter, CancellationToken ct) =>
{
    var repo = await store.GetRepositoryAsync(id, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Repository not found." });

    var result = await submitter.SubmitAsync(repo.Url, ct);
    if (result.Status == JobStatus.Failed)
        return Results.BadRequest(new { error = result.Message });

    return Results.Ok(new { jobId = result.JobId, status = result.Status.ToString(), message = result.Message });
});

api.MapGet("/repositories", async (LibraryStore store, CancellationToken ct) =>
{
    var repos = await store.ListRepositoriesAsync(ct);
    return Results.Ok(repos.Select(r => new
    {
        id = r.RepositoryId,
        slug = r.Slug,
        url = r.Url,
        summary = r.Summary,
        documents = r.Documents,
        chunks = r.Chunks
    }));
});

// One line per indexed repository: repo stats merged with its latest job (status + progress).
// Powers the "Indexed repositories" screen.
api.MapGet("/repos/overview", async (LibraryStore store, CancellationToken ct) =>
{
    var rows = await store.GetRepositoryOverviewAsync(ct);
    return Results.Ok(rows.Select(r => new
    {
        id = r.Id,
        slug = r.Slug,
        url = r.Url,
        documents = r.Documents,
        chunks = r.Chunks,
        status = r.Status,
        jobId = r.JobId,
        filesTotal = r.FilesTotal,
        filesProcessed = r.FilesProcessed,
        chunksTotal = r.ChunksTotal,
        chunksEmbedded = r.ChunksEmbedded,
        error = r.Error,
        updatedAt = r.UpdatedAt,
        lastIndexedAt = r.LastIndexedAt
    }));
});

// Repository-level semantic search (find a repo/tool by its README embedding).
api.MapPost("/repositories/search", async (RepoSearchRequest req, IEmbeddingService embeddings, LibraryStore store, CancellationToken ct) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "A search query is required." });

    var topK = req.TopK is > 0 and <= 50 ? req.TopK.Value : 5;
    var embedding = await embeddings.EmbedOneAsync(req.Query, ct);
    var results = await store.SearchRepositoriesAsync(embedding, topK, ct);

    return Results.Ok(new
    {
        query = req.Query,
        count = results.Count,
        results = results.Select(r => new
        {
            id = r.RepositoryId,
            slug = r.Slug,
            url = r.Url,
            summary = Snippet(r.Summary, 300),
            documents = r.Documents,
            chunks = r.Chunks,
            score = r.Score
        })
    });
});

// Semantic search over document chunks, mirroring the MCP `search_docs` tool.
// Narrow to a specific repository by passing its hash ID in `repositoryId`.
api.MapPost("/search", async (SearchRequest req, IEmbeddingService embeddings, LibraryStore store, CancellationToken ct) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "A search query is required." });

    var topK = req.TopK is > 0 and <= 50 ? req.TopK.Value : 5;
    var repoId = string.IsNullOrWhiteSpace(req.RepositoryId) ? null : req.RepositoryId.Trim();

    var embedding = await embeddings.EmbedOneAsync(req.Query, ct);
    var results = await store.SearchAsync(embedding, topK, repoId, ct);

    return Results.Ok(new
    {
        query = req.Query,
        repositoryId = repoId,
        count = results.Count,
        results = results.Select(r => new
        {
            repositoryId = r.RepositoryId,
            repositorySlug = r.RepositorySlug,
            documentPath = r.DocumentPath,
            headingPath = r.HeadingPath,
            content = r.Content,
            score = r.Score
        })
    });
});

// ---- MCP endpoint -----------------------------------------------------------
var mcp = app.MapMcp("/mcp");
if (authOptions.Enabled)
{
    // Force the McpAuth scheme specifically (not the header-sniffing "Smart" scheme
    // used by /api): MCP clients probe with no Authorization header on their first
    // request and rely on the 401's WWW-Authenticate: Bearer resource_metadata="..."
    // header to discover how to authenticate. Under "Smart", a header-less request
    // would route to the Cookie scheme instead and never receive that header.
    mcp.RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());
}

app.Run();

static object ToDto(Job j) => new
{
    id = j.Id,
    url = j.Url,
    status = j.Status.ToString(),
    filesTotal = j.FilesTotal,
    filesProcessed = j.FilesProcessed,
    chunksTotal = j.ChunksTotal,
    chunksEmbedded = j.ChunksEmbedded,
    error = j.Error,
    createdAt = j.CreatedAt,
    updatedAt = j.UpdatedAt
};

static string? Snippet(string? text, int max)
{
    if (string.IsNullOrWhiteSpace(text)) return text;
    var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
}

public sealed record SubmitRequest(string Url);

public sealed record SearchRequest(string Query, int? TopK, string? RepositoryId);

public sealed record RepoSearchRequest(string Query, int? TopK);
