using System.Security.Cryptography;
using System.Text;
using McpPrivateLibrary.Configuration;

namespace McpPrivateLibrary.Services;

public sealed record MarkdownFile(string RelativePath, string AbsolutePath);

public sealed record MarkdownChunk(int Ordinal, string? HeadingPath, string Content)
{
    public int TokenEstimate => Math.Max(1, Content.Length / 4);
}

/// <summary>
/// Discovers Markdown files in a cloned repo and splits them into heading-aware chunks.
/// Chunking strategy: walk headings to build a breadcrumb, accumulate section body text,
/// then split oversized sections into overlapping windows bounded by MaxChars.
/// </summary>
public sealed class MarkdownProcessor
{
    private static readonly string[] MarkdownExtensions = { ".md", ".markdown", ".mdx" };

    // Directories we never want to descend into.
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", ".github", "vendor", "dist", "build", ".next", ".venv", "target"
    };

    private readonly ChunkingOptions _options;

    public MarkdownProcessor(ChunkingOptions options) => _options = options;

    public IReadOnlyList<MarkdownFile> Discover(string repoRoot)
    {
        var results = new List<MarkdownFile>();
        var rootFull = Path.GetFullPath(repoRoot);

        foreach (var path in EnumerateFiles(rootFull))
        {
            var ext = Path.GetExtension(path);
            if (!MarkdownExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
            results.Add(new MarkdownFile(rel, path));
        }

        return results.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subDirs;
            IEnumerable<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (IgnoredDirs.Contains(name)) continue;
                stack.Push(sub);
            }
            foreach (var file in files) yield return file;
        }
    }

    public static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Extracts an H1 (or first heading) as a document title, if present.</summary>
    public static string? ExtractTitle(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal))
                return line[2..].Trim();
        }
        return null;
    }

    public IReadOnlyList<MarkdownChunk> Chunk(string content)
    {
        content = StripFrontMatter(content);
        var sections = SplitByHeadings(content);

        var chunks = new List<MarkdownChunk>();
        var ordinal = 0;
        foreach (var (headingPath, body) in sections)
        {
            var text = body.Trim();
            if (text.Length == 0) continue;

            foreach (var window in SplitLongText(text))
            {
                // Prefix the heading breadcrumb so each chunk carries local context for embedding.
                var content2 = headingPath is null ? window : $"{headingPath}\n\n{window}";
                chunks.Add(new MarkdownChunk(ordinal++, headingPath, content2));
            }
        }

        // A file with no headings and no body still yields nothing; that's fine.
        return chunks;
    }

    private static string StripFrontMatter(string content)
    {
        // YAML front-matter delimited by --- at the very top.
        if (!content.StartsWith("---", StringComparison.Ordinal)) return content;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return content;
        var after = content.IndexOf('\n', end + 1);
        return after < 0 ? "" : content[(after + 1)..];
    }

    private static IEnumerable<(string? HeadingPath, string Body)> SplitByHeadings(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var headingStack = new List<(int Level, string Text)>();
        var body = new StringBuilder();
        bool inFence = false;

        (string? headingPath, string body) Current()
        {
            var path = headingStack.Count == 0
                ? null
                : string.Join(" > ", headingStack.Select(h => h.Text));
            return (path, body.ToString());
        }

        var sections = new List<(string?, string)>();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && TryParseHeading(line, out var level, out var text))
            {
                // Flush the accumulated body under the previous heading.
                if (body.Length > 0)
                {
                    sections.Add(Current());
                    body.Clear();
                }
                // Pop headings at or below this level, then push the new one.
                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add((level, text));
            }
            else
            {
                body.Append(line).Append('\n');
            }
        }
        if (body.Length > 0) sections.Add(Current());

        return sections;
    }

    private static bool TryParseHeading(string line, out int level, out string text)
    {
        level = 0;
        text = "";
        var i = 0;
        while (i < line.Length && line[i] == '#') i++;
        if (i == 0 || i > 6) return false;
        if (i >= line.Length || line[i] != ' ') return false;
        level = i;
        text = line[(i + 1)..].Trim();
        return text.Length > 0;
    }

    private IEnumerable<string> SplitLongText(string text)
    {
        if (text.Length <= _options.MaxChars)
        {
            yield return text;
            yield break;
        }

        var step = Math.Max(1, _options.MaxChars - _options.Overlap);
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + _options.MaxChars);
            // Try to break on a paragraph or newline boundary near the end for cleaner chunks.
            if (end < text.Length)
            {
                var lastBreak = text.LastIndexOf('\n', end - 1, Math.Min(end - start, _options.Overlap + 1));
                if (lastBreak > start) end = lastBreak;
            }
            yield return text[start..end].Trim();
            if (end >= text.Length) break;
            start = Math.Max(end - _options.Overlap, start + step);
        }
    }
}
