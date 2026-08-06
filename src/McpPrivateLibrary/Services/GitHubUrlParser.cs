using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace McpPrivateLibrary.Services;

public sealed record GitHubRepoRef(string CloneUrl, string Owner, string Name)
{
    /// <summary>Human-friendly "owner/repo".</summary>
    public string Slug => $"{Owner}/{Name}".ToLowerInvariant();

    /// <summary>Provider-qualified canonical identity, e.g. "github.com/modelcontextprotocol/csharp-sdk".</summary>
    public string CanonicalName => $"github.com/{Owner}/{Name}".ToLowerInvariant();

    /// <summary>
    /// Stable, collision-resistant repo ID: SHA-256 of the canonical name, hex-encoded and
    /// truncated to 16 chars (64 bits). Deterministic across runs so the same repo always maps
    /// to the same ID.
    /// </summary>
    public string Id => ComputeId(CanonicalName);

    public static string ComputeId(string canonicalName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalName.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Parses and validates GitHub URLs (HTTPS or SSH) that can be used for cloning.
/// Non-GitHub / non-cloneable inputs are rejected per the requirements.
/// </summary>
public static partial class GitHubUrlParser
{
    // https://github.com/owner/repo(.git)?(/...)?  and  git@github.com:owner/repo(.git)?
    [GeneratedRegex(@"^(?:https?://(?:www\.)?github\.com/|git@github\.com:)(?<owner>[\w.\-]+)/(?<name>[\w.\-]+?)(?:\.git)?/?(?:$|[#?].*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRegex();

    public static bool TryParse(string? input, out GitHubRepoRef repo, out string? error)
    {
        repo = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "URL is empty.";
            return false;
        }

        input = input.Trim();
        var m = GitHubRegex().Match(input);
        if (!m.Success)
        {
            error = "Only GitHub HTTPS or SSH clone URLs are supported (e.g. https://github.com/org/repo or git@github.com:org/repo.git).";
            return false;
        }

        var owner = m.Groups["owner"].Value;
        var name = m.Groups["name"].Value;

        // Guard against pathological names that slipped through.
        if (name.Equals(".", StringComparison.Ordinal) || name.Equals("..", StringComparison.Ordinal))
        {
            error = "Invalid repository name.";
            return false;
        }

        // Always clone over HTTPS; it works for public repos without SSH keys.
        var cloneUrl = $"https://github.com/{owner}/{name}.git";
        repo = new GitHubRepoRef(cloneUrl, owner, name);
        return true;
    }
}
