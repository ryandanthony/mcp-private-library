using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using McpPrivateLibrary.Configuration;
using Microsoft.Extensions.Options;
using Pgvector;

namespace McpPrivateLibrary.Services;

public interface IEmbeddingService
{
    int Dimensions { get; }
    Task<IReadOnlyList<Vector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);
    Task<Vector> EmbedOneAsync(string input, CancellationToken ct = default);
}

/// <summary>
/// Embeddings via OpenRouter's OpenAI-compatible /embeddings endpoint. When no API key is
/// configured it transparently falls back to a deterministic hash-based embedder so the whole
/// pipeline is testable offline (search quality will be poor, but plumbing works end to end).
/// </summary>
public sealed class OpenRouterEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OpenRouterEmbeddingService> _logger;
    private readonly bool _useRemote;

    public OpenRouterEmbeddingService(
        HttpClient http,
        IOptions<LibraryOptions> options,
        ILogger<OpenRouterEmbeddingService> logger)
    {
        _http = http;
        _options = options.Value.Embedding;
        _logger = logger;
        _useRemote = _options.HasApiKey;

        if (_useRemote)
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            // OpenRouter recommends these attribution headers.
            _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://github.com/mcp-private-library");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "MCP Private Library");
        }
        else
        {
            _logger.LogWarning(
                "No OpenRouter API key configured; using deterministic local embedder (dim {Dim}). Set Library:Embedding:ApiKey for real semantic search.",
                _options.Dimensions);
        }
    }

    public int Dimensions => _options.Dimensions;

    public async Task<Vector> EmbedOneAsync(string input, CancellationToken ct = default)
    {
        var result = await EmbedAsync(new[] { input }, ct);
        return result[0];
    }

    public async Task<IReadOnlyList<Vector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return Array.Empty<Vector>();
        return _useRemote
            ? await EmbedRemoteAsync(inputs, ct)
            : inputs.Select(LocalEmbed).ToList();
    }

    private async Task<IReadOnlyList<Vector>> EmbedRemoteAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var request = new EmbeddingRequest(_options.Model, inputs);

        // Simple retry with exponential backoff for 429 / 5xx.
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await _http.PostAsJsonAsync("embeddings", request, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    var status = (int)resp.StatusCode;
                    if (attempt < maxAttempts && (status == 429 || status >= 500))
                    {
                        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                        _logger.LogWarning("Embeddings HTTP {Status}; retry {Attempt}/{Max} after {Delay}ms",
                            status, attempt, maxAttempts, delay.TotalMilliseconds);
                        await Task.Delay(delay, ct);
                        continue;
                    }
                    throw new InvalidOperationException($"OpenRouter embeddings failed ({status}): {body}");
                }

                var parsed = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct)
                             ?? throw new InvalidOperationException("Empty embeddings response.");
                // Preserve input order using the index field.
                return parsed.Data
                    .OrderBy(d => d.Index)
                    .Select(d => new Vector(NormalizeDimension(d.Embedding)))
                    .ToList();
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct);
            }
        }
    }

    private float[] NormalizeDimension(float[] embedding)
    {
        if (embedding.Length == _options.Dimensions) return embedding;
        // Guard against config/model mismatch: pad or truncate to the configured column size.
        _logger.LogWarning("Embedding dim {Actual} != configured {Expected}; adjusting.",
            embedding.Length, _options.Dimensions);
        var adjusted = new float[_options.Dimensions];
        Array.Copy(embedding, adjusted, Math.Min(embedding.Length, _options.Dimensions));
        return adjusted;
    }

    /// <summary>
    /// Deterministic bag-of-words hashing embedder. Not semantically meaningful, but stable and
    /// dependency-free so the ingestion + storage + search plumbing can be exercised without a key.
    /// </summary>
    private Vector LocalEmbed(string input)
    {
        var dim = _options.Dimensions;
        var vec = new float[dim];
        foreach (var token in Tokenize(input))
        {
            var hash = BitConverter.ToUInt32(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)), 0);
            var idx = (int)(hash % (uint)dim);
            var sign = (hash & 0x80000000) == 0 ? 1f : -1f;
            vec[idx] += sign;
        }
        // L2 normalize so cosine distance behaves.
        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
            for (var i = 0; i < dim; i++) vec[i] /= norm;
        return new Vector(vec);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] List<EmbeddingDatum> Data);

    private sealed record EmbeddingDatum(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
