using System.Security.Claims;
using System.Text.Encodings.Web;
using McpPrivateLibrary.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace McpPrivateLibrary.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The `Authorization` scheme this handler answers to.</summary>
    public string Scheme { get; set; } = ApiKeyToken.Scheme;

    /// <summary>
    /// How stale <c>last_used_at</c> may be before a successful authentication writes it again.
    /// Without this, a chatty MCP client would issue an UPDATE on every single request purely to
    /// maintain a field nobody reads more than once a day.
    /// </summary>
    public TimeSpan LastUsedWriteInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Authenticates <c>Authorization: ApiKey mcpl_&lt;keyId&gt;_&lt;secret&gt;</c> against the
/// <c>api_keys</c> table and materialises the owning user as a ClaimsPrincipal, so every
/// downstream endpoint sees the same shape of identity it would from a Keycloak JWT.
///
/// Registered as a sibling of JwtBearer rather than folded into it: the two credentials are
/// validated in completely different ways (JWKS signature check vs. database lookup), and keeping
/// them as separate schemes means a bad API key can never be mistaken for a bad JWT, each gets its
/// own WWW-Authenticate challenge, and either can be disabled independently.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    /// <summary>Marks a principal as having come from an API key rather than an interactive login.</summary>
    public const string AuthMethodClaim = "amr";
    public const string ApiKeyIdClaim = "api_key_id";

    private readonly LibraryStore _store;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        LibraryStore store)
        : base(options, logger, encoder)
    {
        _store = store;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No / non-matching Authorization header: NoResult (not Fail) so the request can fall
        // through to another scheme. Failing here would abort the whole authentication pipeline.
        if (!AuthorizationHeaderParser.TryGetToken(Request.Headers.Authorization, Options.Scheme, out var token))
            return AuthenticateResult.NoResult();

        if (!ApiKeyToken.TryParse(token, out var keyId, out var secret))
            return AuthenticateResult.Fail("Malformed API key.");

        var key = await _store.FindApiKeyByKeyIdAsync(keyId, Context.RequestAborted);

        // Always run the (constant-time) secret comparison, even for an unknown key id, against a
        // dummy hash. Short-circuiting on "unknown key" would make unknown-vs-known distinguishable
        // by response time, letting an attacker enumerate valid key ids.
        var secretOk = ApiKeyToken.VerifySecret(secret, key?.SecretHash ?? DummyHash);

        if (key is null || !secretOk)
        {
            Logger.LogDebug("API key authentication failed for key id {KeyId}.", keyId);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (key.IsRevoked)
            return AuthenticateResult.Fail("This API key has been revoked.");

        if (key.IsExpired)
            return AuthenticateResult.Fail("This API key has expired.");

        // Best-effort, throttled usage stamp. A failure here must never turn a valid credential
        // into a 401, so it's fire-and-forget with its own try/catch.
        if (key.LastUsedAt is null || DateTimeOffset.UtcNow - key.LastUsedAt > Options.LastUsedWriteInterval)
            _ = TouchQuietlyAsync(key.Id);

        var identity = new ClaimsIdentity(
            [
                // `sub` mirrors the JWT claim, so authorization code and per-user data access work
                // identically whether the caller arrived with a token or a key.
                new Claim(ClaimTypes.NameIdentifier, key.OwnerSubject),
                new Claim("sub", key.OwnerSubject),
                new Claim("preferred_username", key.OwnerName ?? key.OwnerSubject),
                new Claim(AuthMethodClaim, Options.Scheme),
                new Claim(ApiKeyIdClaim, key.Id.ToString()),
            ],
            authenticationType: Scheme.Name,
            nameType: "preferred_username",
            roleType: "roles");

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>
    /// Tells the client which credential this endpoint wants. Deliberately advertises `ApiKey`
    /// only; the MCP/Bearer scheme emits its own RFC 9728 challenge for OAuth clients.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", $"{Options.Scheme} realm=\"mcp-private-library\"");
        return Task.CompletedTask;
    }

    private async Task TouchQuietlyAsync(long id)
    {
        try
        {
            await _store.TouchApiKeyAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to update last_used_at for API key {Id}.", id);
        }
    }

    /// <summary>SHA-256 of a value no generated key can produce, used for the timing-equalising compare.</summary>
    private static readonly string DummyHash = ApiKeyToken.HashSecret("\0unknown-api-key\0");
}

internal static class AuthorizationHeaderParser
{
    /// <summary>
    /// Pulls the token out of the first <c>Authorization</c> header matching <paramref name="scheme"/>.
    /// Iterates all header values because a client may legitimately send more than one credential
    /// (e.g. an API key alongside a bearer token) and we want ours regardless of ordering.
    /// </summary>
    public static bool TryGetToken(StringValues headers, string scheme, out string token)
    {
        token = "";
        foreach (var raw in headers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var value = raw.AsSpan().Trim();
            if (value.Length <= scheme.Length) continue;
            if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) continue;
            if (value[scheme.Length] != ' ') continue;

            token = value[(scheme.Length + 1)..].Trim().ToString();
            if (token.Length > 0) return true;
        }
        return false;
    }
}
