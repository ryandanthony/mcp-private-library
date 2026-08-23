using McpPrivateLibrary.Auth;
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
builder.Services.AddHttpClient<WebScraperService>();

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
        // Chooses ApiKey / Bearer (-> MCP RFC 9728 challenge) / Cookie per request. The two
        // Authorization-header credentials are told apart by their scheme token, so an API key and
        // a Keycloak JWT can coexist on the same endpoints without either shadowing the other.
        // Browser navigations (no Authorization header) fall through to the cookie session.
        .AddPolicyScheme("Smart", "ApiKey, Bearer or Cookie", options =>
        {
            options.ForwardDefaultSelector = context =>
                SelectScheme(context, McpAuthenticationDefaults.AuthenticationScheme);
        })
        // Same idea as "Smart", but the fallback for a request with no (or an unrecognised)
        // Authorization header is the MCP/Bearer scheme rather than the cookie scheme. MCP clients
        // probe unauthenticated first and depend on the resulting 401's
        // `WWW-Authenticate: Bearer resource_metadata="..."` to discover how to log in; routing
        // those probes to Cookie instead would swallow that header. Layering it on top of the same
        // selector keeps API keys working on /mcp without disturbing OAuth discovery.
        .AddPolicyScheme("McpSmart", "ApiKey or MCP Bearer", options =>
        {
            options.ForwardDefaultSelector = context =>
                SelectScheme(context, McpAuthenticationDefaults.AuthenticationScheme, fallbackToCookie: false);
        })
        // Database-backed, user-scoped API keys for non-interactive clients.
        .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyToken.Scheme, _ => { })
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

    // force: false -- respects the in-flight and recent-index guards so re-submitting a URL
    // that's already indexing (or was indexed within Library:MinReindexInterval) doesn't queue
    // a redundant duplicate job. Use the Reindex button/endpoint to force a refresh.
    var result = await submitter.SubmitAsync(req.Url, force: false, ct);
    return JobSubmissionResult(result);
});

// Website source: crawlSameDomain selects a same-host crawl from the given start page instead of
// a single-page scrape. Same in-flight/recent-index guards as the git submission path above.
api.MapPost("/jobs/web", async (SubmitWebRequest req, IJobSubmitter submitter, CancellationToken ct) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "A URL is required." });

    var result = await submitter.SubmitWebAsync(req.Url, req.CrawlSameDomain, req.MaxPages, force: false, ct);
    return JobSubmissionResult(result);
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

// Stops an in-flight (or still-queued) job. Signals the running pipeline's cancellation token if
// it's already processing, or marks it Cancelled directly if it hasn't started yet; either way the
// job settles into the terminal Cancelled status without needing to kill the process or touch the
// DB by hand. Returns 404 if the job doesn't exist or is already terminal (nothing to cancel).
api.MapPost("/jobs/{id:long}/cancel", async (long id, IJobSubmitter submitter, CancellationToken ct) =>
{
    var cancelled = await submitter.TryCancelAsync(id, ct);
    return cancelled
        ? Results.Ok(new { id, status = nameof(JobStatus.Cancelled) })
        : Results.NotFound(new { error = "Job not found or already finished." });
});

// Reindex an already-submitted repository: re-runs the same clone -> chunk -> embed pipeline
// against a fresh generation, then atomically swaps it in for the old one (see LibraryStore's
// SwapGenerationAsync / IngestionService). The current index stays fully live and searchable
// for the entire duration; nothing is cleared upfront. This is a deliberate user action, so it
// bypasses the recent-index cooldown (force: true) -- but still refuses to queue a second job
// if one is already running for this repo. IJobSubmitter.ReindexAsync routes to the git or web
// pipeline based on the repository's own source type, so this works for web-sourced repos too
// (submitting the stored URL through the GitHub-only path here was the "Only GitHub HTTPS or SSH
// clone URLs are supported" bug for those).
api.MapPost("/repositories/{id}/reindex", async (string id, IJobSubmitter submitter, CancellationToken ct) =>
{
    var result = await submitter.ReindexAsync(id, ct);
    if (result is null)
        return Results.NotFound(new { error = "Repository not found." });

    return JobSubmissionResult(result);
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

// ---- API keys ---------------------------------------------------------------
// Long-lived credentials for MCP hosts/CLIs that can't run an OAuth code flow, scoped to the
// creating user. Managed under /api/keys, but deliberately NOT usable via an API key: minting is
// restricted to an interactive login (cookie session or Keycloak bearer token). If a key could
// mint more keys, revoking a leaked one wouldn't actually end the compromise -- the attacker would
// already have issued replacements. Requiring a real login makes revocation final.
if (authOptions.Enabled)
{
    var keys = app.MapGroup("/api/keys").RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(
            CookieAuthenticationDefaults.AuthenticationScheme,
            McpAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());

    keys.MapGet("/", async (ClaimsPrincipal user, LibraryStore store, CancellationToken ct) =>
    {
        var subject = Subject(user);
        if (subject is null) return Results.Unauthorized();

        var rows = await store.ListApiKeysAsync(subject, ct);
        return Results.Ok(rows.Select(ToApiKeyDto));
    });

    keys.MapPost("/", async (CreateApiKeyRequest? req, ClaimsPrincipal user, LibraryStore store, CancellationToken ct) =>
    {
        var subject = Subject(user);
        if (subject is null) return Results.Unauthorized();

        var name = req?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "A name is required so you can tell your keys apart." });
        if (name.Length > 100)
            return Results.BadRequest(new { error = "Name must be 100 characters or fewer." });

        DateTimeOffset? expiresAt = null;
        if (req?.ExpiresInDays is { } days)
        {
            if (days is < 1 or > 3650)
                return Results.BadRequest(new { error = "Expiry must be between 1 and 3650 days." });
            expiresAt = DateTimeOffset.UtcNow.AddDays(days);
        }

        var generated = ApiKeyToken.Generate();
        var created = await store.CreateApiKeyAsync(new ApiKey
        {
            KeyId = generated.KeyId,
            SecretHash = generated.SecretHash,
            OwnerSubject = subject,
            OwnerName = user.Identity?.Name,
            Name = name,
            ExpiresAt = expiresAt,
        }, ct);

        // The only time the plaintext token exists outside the caller's hands. It is not stored
        // and cannot be recovered; losing it means creating a new key.
        return Results.Created($"/api/keys/{created.Id}", new
        {
            key = ToApiKeyDto(created),
            token = generated.Token,
            authorizationHeader = $"{ApiKeyToken.Scheme} {generated.Token}",
            warning = "Copy this token now. It is hashed on the server and cannot be shown again."
        });
    });

    // Revocation takes effect on the very next request: the auth handler reads revoked_at on each
    // authentication rather than caching validity, so there is no window where a revoked key still
    // works. 404 (not 403) for someone else's key id, so ids aren't probeable across accounts.
    keys.MapDelete("/{id:long}", async (long id, ClaimsPrincipal user, LibraryStore store, CancellationToken ct) =>
    {
        var subject = Subject(user);
        if (subject is null) return Results.Unauthorized();

        var revoked = await store.RevokeApiKeyAsync(id, subject, ct);
        return revoked
            ? Results.Ok(new { id, revoked = true })
            : Results.NotFound(new { error = "API key not found." });
    });
}

// ---- MCP endpoint -----------------------------------------------------------
var mcp = app.MapMcp("/mcp");
if (authOptions.Enabled)
{
    // Force the MCP-flavoured policy scheme specifically (not the browser-oriented "Smart"
    // scheme used by /api): MCP clients probe with no Authorization header on their first
    // request and rely on the 401's WWW-Authenticate: Bearer resource_metadata="..."
    // header to discover how to authenticate. Under "Smart", a header-less request
    // would route to the Cookie scheme instead and never receive that header. "McpSmart"
    // keeps that behaviour while still accepting `Authorization: ApiKey ...`.
    mcp.RequireAuthorization(policy => policy
        .AddAuthenticationSchemes("McpSmart")
        .RequireAuthenticatedUser());
}

app.Run();

/// <summary>
/// Picks the authentication scheme for a request by looking at the `Authorization` header's
/// scheme token. This is what lets API keys and Keycloak bearer tokens coexist: each credential
/// names its own handler on the wire, so neither has to guess at, or fall through, the other.
/// A request with no Authorization header is a browser navigation (cookie session) on /api, or an
/// MCP discovery probe (which must reach the Bearer challenge) on /mcp.
/// </summary>
static string SelectScheme(HttpContext context, string bearerScheme, bool fallbackToCookie = true)
{
    foreach (var value in context.Request.Headers.Authorization)
    {
        if (string.IsNullOrWhiteSpace(value)) continue;
        if (value.StartsWith(ApiKeyToken.Scheme + " ", StringComparison.OrdinalIgnoreCase))
            return ApiKeyToken.Scheme;
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return bearerScheme;
    }

    // An Authorization header we don't recognise still isn't a cookie session; hand it to the
    // bearer handler so the caller gets a credential-shaped 401 rather than a silent redirect.
    if (context.Request.Headers.Authorization.Count > 0)
        return bearerScheme;

    return fallbackToCookie ? CookieAuthenticationDefaults.AuthenticationScheme : bearerScheme;
}

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

// Maps a job submission outcome to an HTTP response. Created/AlreadyInFlight are both "OK, here's
// the job to watch" (200) -- AlreadyInFlight isn't an error, it's telling the caller a job is
// already running and pointing them at it instead of creating a duplicate. TooRecent is a real
// "not doing that" (409 Conflict, distinct from 400 so callers can tell "try again differently"
// apart from "this request is malformed"). InvalidUrl is the pre-existing 400 case.
static IResult JobSubmissionResult(JobSubmission result) => result.Outcome switch
{
    JobCreationOutcome.Created or JobCreationOutcome.AlreadyInFlight =>
        Results.Ok(new { jobId = result.JobId, status = result.Status.ToString(), message = result.Message, alreadyInFlight = result.Outcome == JobCreationOutcome.AlreadyInFlight }),
    JobCreationOutcome.TooRecent =>
        Results.Conflict(new { error = result.Message }),
    _ => Results.BadRequest(new { error = result.Message }),
};

static string? Snippet(string? text, int max)
{
    if (string.IsNullOrWhiteSpace(text)) return text;
    var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
}

/// <summary>
/// The user's stable identifier. Prefers the IdP `sub` claim over the username or email, both of
/// which a user can change -- a rename must not orphan their existing keys or silently hand them
/// to whoever claims the old name next.
/// </summary>
static string? Subject(ClaimsPrincipal user)
{
    var sub = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
    return string.IsNullOrWhiteSpace(sub) ? null : sub;
}

/// <summary>
/// Public projection of an API key. Never includes the secret or its hash: only the non-secret
/// prefix, which is enough to match a row against a configured client but useless as a credential.
/// </summary>
static object ToApiKeyDto(ApiKey k) => new
{
    id = k.Id,
    name = k.Name,
    prefix = ApiKeyToken.DisplayPrefix(k.KeyId),
    createdAt = k.CreatedAt,
    expiresAt = k.ExpiresAt,
    lastUsedAt = k.LastUsedAt,
    revokedAt = k.RevokedAt,
    active = k.IsActive,
    status = k.IsRevoked ? "Revoked" : k.IsExpired ? "Expired" : "Active",
};

public sealed record SubmitRequest(string Url);
public sealed record SubmitWebRequest(string Url, bool CrawlSameDomain, int? MaxPages);

/// <summary>
/// Request body for minting an API key. <c>ExpiresInDays</c> is optional; omitting it creates a
/// key that lives until explicitly revoked.
/// </summary>
public sealed record CreateApiKeyRequest(string? Name, int? ExpiresInDays);

public sealed record SearchRequest(string Query, int? TopK, string? RepositoryId);

public sealed record RepoSearchRequest(string Query, int? TopK);
