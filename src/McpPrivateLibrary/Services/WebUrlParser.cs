using System.Security.Cryptography;
using System.Text;

namespace McpPrivateLibrary.Services;

/// <summary>
/// Identity for a website source: either a single page (no crawling) or the starting point of a
/// same-host crawl. Mirrors <see cref="GitHubRepoRef"/>'s shape so the rest of the pipeline
/// (repository row, job, generation-swap) treats git and web sources uniformly.
/// </summary>
public sealed record WebSourceRef(string StartUrl, string Host, bool CrawlSameDomain, int? MaxPages = null)
{
    /// <summary>Human-friendly display name: the host alone for a crawl, or host+path for a single page.</summary>
    public string Slug
    {
        get
        {
            if (CrawlSameDomain) return Host.ToLowerInvariant();
            var uri = new Uri(StartUrl);
            var path = uri.AbsolutePath.TrimEnd('/');
            return (Host + path).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Provider-qualified canonical identity. A same-host crawl is identified by host alone (so
    /// re-submitting any page on that host with crawl=true reuses the same repository); a
    /// single-page scrape is identified by the exact URL (path + query), so distinct pages on the
    /// same host are tracked as distinct repositories.
    /// </summary>
    public string CanonicalName => CrawlSameDomain
        ? $"web-crawl:{Host}".ToLowerInvariant()
        : $"web-page:{NormalizeForIdentity(StartUrl)}".ToLowerInvariant();

    public string Id => ComputeId(CanonicalName);

    public static string ComputeId(string canonicalName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalName.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Drops the fragment (never sent to the server, never affects content) for identity purposes.</summary>
    private static string NormalizeForIdentity(string url)
    {
        var uri = new Uri(url);
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.ToString();
    }
}

/// <summary>
/// Validates and normalizes an http(s) URL for website scraping. Unlike
/// <see cref="GitHubUrlParser"/>, this accepts any absolute http/https URL -- the "is this
/// scrapeable" question is answered later, by <see cref="WebScraperService"/> actually fetching it.
/// </summary>
public static class WebUrlParser
{
    public static bool TryParse(
        string? input, bool crawlSameDomain, out WebSourceRef source, out string? error, int? maxPages = null)
    {
        source = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "URL is empty.";
            return false;
        }

        input = input.Trim();

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Only absolute http:// or https:// URLs are supported.";
            return false;
        }

        source = new WebSourceRef(uri.ToString(), uri.Host, crawlSameDomain, maxPages);
        return true;
    }
}
