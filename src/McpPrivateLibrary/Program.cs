using McpPrivateLibrary.Configuration;
using McpPrivateLibrary.Data;
using McpPrivateLibrary.Ingestion;
using McpPrivateLibrary.Mcp;
using McpPrivateLibrary.Models;
using McpPrivateLibrary.Services;
using Microsoft.Extensions.Options;

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

var app = builder.Build();

// Apply the schema (and pgvector extension) before serving traffic.
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- HTTP API ---------------------------------------------------------------
var api = app.MapGroup("/api");

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

api.MapGet("/repositories", async (LibraryStore store, CancellationToken ct) =>
{
    var repos = await store.ListRepositoriesAsync(ct);
    return Results.Ok(repos.Select(r => new
    {
        slug = r.Slug,
        url = r.Url,
        documents = r.Documents,
        chunks = r.Chunks
    }));
});

// ---- MCP endpoint -----------------------------------------------------------
app.MapMcp("/mcp");

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

public sealed record SubmitRequest(string Url);
