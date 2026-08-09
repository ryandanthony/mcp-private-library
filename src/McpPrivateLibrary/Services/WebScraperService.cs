using System.Text.RegularExpressions;
using HtmlAgilityPack;
using ReverseMarkdown;

namespace McpPrivateLibrary.Services;

/// <summary>One fetched, chrome-stripped, markdown-converted page.</summary>
public sealed record ScrapedPage(string Url, string Path, string? Title, string Markdown);

/// <summary>
/// Fetches website content for ingestion: either a single page, or a same-host crawl starting
/// from it. Strips nav/header/footer chrome before converting the remaining main content to
/// Markdown (via <see cref="ReverseMarkdown"/>), so downstream chunking/embedding sees article
/// text rather than site furniture.
///
/// No headless browser: pages that appear to require client-side JS to render their content
/// fail explicitly (<see cref="DetectJsRendered"/>) rather than silently producing an
/// empty/garbage index entry. Crawling never consults robots.txt; a same-host crawl has no
/// depth limit and no page limit unless the repository's <see cref="WebSourceRef.MaxPages"/>
/// is set, in which case the crawl stops (without failing) once that many pages have been
/// fetched, even if links remain queued.
/// </summary>
public sealed class WebScraperService
{
    private readonly HttpClient _http;
    private readonly ILogger<WebScraperService> _logger;

    // Elements that are never part of "main content" regardless of the page's layout.
    private static readonly string[] ChromeSelectors =
    {
        "//nav", "//header", "//footer", "//aside", "//script", "//style", "//noscript",
        "//*[@role='navigation']", "//*[@role='banner']", "//*[@role='contentinfo']",
    };

    public WebScraperService(HttpClient http, ILogger<WebScraperService> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(30);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; McpPrivateLibraryBot/1.0; +https://library.ants.zone)");
        }
    }

    /// <summary>
    /// Scrapes <paramref name="source"/>: a single page (<see cref="WebSourceRef.CrawlSameDomain"/>
    /// false) or a same-host breadth-first crawl starting from it. A single-page scrape propagates
    /// any fetch/parse failure (including JS-rendering detection) directly, since there's nothing
    /// else to index; a crawl logs and skips individual page failures so one bad link doesn't sink
    /// the whole run, but still fails overall if the start page itself can't be fetched.
    /// </summary>
    public async Task<IReadOnlyList<ScrapedPage>> ScrapeAsync(WebSourceRef source, CancellationToken ct)
    {
        if (!source.CrawlSameDomain)
        {
            var (page, _) = await FetchPageWithLinksAsync(source.StartUrl, host: null, ct);
            return new[] { page };
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var results = new List<ScrapedPage>();

        var start = NormalizeUrl(source.StartUrl);
        queue.Enqueue(start);
        visited.Add(start);

        var first = true;
        while (queue.Count > 0)
        {
            if (source.MaxPages is int cap && results.Count >= cap)
            {
                _logger.LogInformation(
                    "Crawl of {Host} reached MaxPages ({Cap}); stopping with {Queued} URL(s) still queued.",
                    source.Host, cap, queue.Count);
                break;
            }

            ct.ThrowIfCancellationRequested();
            var url = queue.Dequeue();

            ScrapedPage page;
            IReadOnlyList<string> links;
            try
            {
                (page, links) = await FetchPageWithLinksAsync(url, source.Host, ct);
            }
            catch (Exception ex) when (!first)
            {
                _logger.LogWarning(ex, "Skipping page {Url} during crawl of {Host}.", url, source.Host);
                continue;
            }
            first = false;

            results.Add(page);
            foreach (var link in links)
            {
                if (visited.Add(link)) queue.Enqueue(link);
            }
        }

        if (results.Count == 0)
            throw new InvalidOperationException($"Could not fetch any pages from {source.Host}.");

        return results;
    }

    private async Task<(ScrapedPage Page, IReadOnlyList<string> Links)> FetchPageWithLinksAsync(
        string url, string? host, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{url} is not an HTML page (content-type: {contentType}).");

        var html = await response.Content.ReadAsStringAsync(ct);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        DetectJsRendered(doc, finalUrl);

        var links = host is null ? Array.Empty<string>() : ExtractSameHostLinks(doc, finalUrl, host);

        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
        var mainContent = ExtractMainContent(doc);
        var markdown = ConvertToMarkdown(mainContent);

        var page = new ScrapedPage(finalUrl, BuildPagePath(finalUrl), string.IsNullOrWhiteSpace(title) ? null : title, markdown);
        return (page, links);
    }

    /// <summary>
    /// Collects same-host link targets from the raw (unstripped) document -- navigation is exactly
    /// where crawl-worthy links live, even though it's excluded from the indexed content itself.
    /// Host comparison is exact (no subdomain matching); crawling is free to walk "up" the path
    /// since nothing here is scoped to the start URL's subpath.
    /// </summary>
    private static string[] ExtractSameHostLinks(HtmlDocument doc, string pageUrl, string host)
    {
        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) return Array.Empty<string>();

        var pageUri = new Uri(pageUrl);
        var links = new List<string>();
        foreach (var a in anchors)
        {
            var href = a.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(pageUri, href, out var abs)) continue;
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) continue;
            if (!string.Equals(abs.Host, host, StringComparison.OrdinalIgnoreCase)) continue;

            links.Add(NormalizeUrl(abs.ToString()));
        }
        return links.ToArray();
    }

    /// <summary>Drops the fragment and any trailing slash so link/identity comparisons are stable.</summary>
    private static string NormalizeUrl(string url)
    {
        var uri = new Uri(url);
        var builder = new UriBuilder(uri) { Fragment = "" };
        var s = builder.Uri.ToString();
        return s.Length > 1 && s.EndsWith('/') ? s[..^1] : s;
    }

    /// <summary>Derives a document "path" (for storage/dedup) from a page's URL path.</summary>
    private static string BuildPagePath(string url)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(path) ? "index" : path;
    }

    /// <summary>Strips chrome, then picks the best available content root: main, article, or body.</summary>
    private static HtmlNode ExtractMainContent(HtmlDocument doc)
    {
        var root = doc.DocumentNode.CloneNode(deep: true);
        foreach (var selector in ChromeSelectors)
        {
            var nodes = root.SelectNodes(selector);
            if (nodes is null) continue;
            foreach (var node in nodes.ToList()) node.Remove();
        }

        return root.SelectSingleNode("//main") ?? root.SelectSingleNode("//article")
            ?? root.SelectSingleNode("//body") ?? root;
    }

    private static string ConvertToMarkdown(HtmlNode node)
    {
        var converter = new Converter();
        return converter.Convert(node.InnerHtml).Trim();
    }

    /// <summary>
    /// Heuristic: a page with barely any visible text but at least one &lt;script&gt; tag almost
    /// certainly renders its real content client-side. This scraper never executes JS, so rather
    /// than silently indexing an empty/near-empty page, fail with a clear, actionable message.
    /// </summary>
    private static void DetectJsRendered(HtmlDocument doc, string url)
    {
        var bodyText = doc.DocumentNode.SelectSingleNode("//body")?.InnerText ?? "";
        var collapsed = Regex.Replace(bodyText, "\\s+", " ").Trim();
        var hasScripts = doc.DocumentNode.SelectNodes("//script") is { Count: > 0 };

        if (collapsed.Length < 200 && hasScripts)
        {
            throw new InvalidOperationException(
                $"{url} appears to require JavaScript to render its content (found little to no text " +
                "in a plain HTTP fetch). This scraper doesn't run a headless browser, so JS-rendered " +
                "pages can't be indexed.");
        }
    }
}
