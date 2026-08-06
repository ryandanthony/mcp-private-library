# MCP Private Library

An MCP (Model Context Protocol) server that indexes the Markdown documentation of GitHub repositories and makes it semantically searchable. Submit a GitHub repo URL through a minimal web UI (or the HTTP API); the app clones the repo, extracts every Markdown file, chunks and embeds the content, and stores the vectors in Postgres. MCP clients can then run semantic search over the indexed docs. Built in C# / ASP.NET Core with Dapper for data access, PostgreSQL + [pgvector](https://github.com/pgvector/pgvector) for vector similarity search, and OpenRouter for embeddings. An OpenRouter API key is required; the app fails fast at startup if one is not configured.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (for the Postgres + pgvector container)
- [git](https://git-scm.com/) (used to clone target repositories)

## Quick start

1. Start Postgres (Postgres 17 with the `vector` extension) via Docker Compose:

   ```bash
   docker compose up -d
   ```

2. (Optional) Configure an OpenRouter API key for real embeddings. Copy the example
   local settings file and add your key:

   ```bash
   cp src/McpPrivateLibrary/appsettings.Local.json.example src/McpPrivateLibrary/appsettings.Local.json
   # edit appsettings.Local.json and set Library:Embedding:ApiKey
   ```

   An OpenRouter API key is required. If it is empty, the app fails fast at startup
   with a clear error. (`appsettings.Local.json` is git-ignored.)

3. Run the app:

   ```bash
   dotnet run --project src/McpPrivateLibrary
   ```

4. Open the web UI at <http://localhost:5171>.

5. Submit a GitHub repository URL to start an indexing job, for example an HTTPS
   clone URL like `https://github.com/org/repo.git` (SSH URLs such as
   `git@github.com:org/repo.git` are also accepted). You can do this from the UI or
   directly against the API:

   ```bash
   curl -X POST http://localhost:5171/api/jobs \
     -H "Content-Type: application/json" \
     -d '{"url":"https://github.com/org/repo.git"}'
   ```

## HTTP API

| Method | Path                       | Description                                                             |
| ------ | -------------------------- | ----------------------------------------------------------------------- |
| POST   | `/api/jobs`                | Submit a GitHub URL to start a new indexing job.                        |
| GET    | `/api/jobs/{id}`           | Get the status and progress of a single job.                            |
| GET    | `/api/jobs`                | List all jobs.                                                          |
| GET    | `/api/repositories`        | List indexed repositories (each with its hash `id`, slug and counts).   |
| POST   | `/api/repositories/search` | Repository-level search: find a repo/tool by its README. `{query,topK}`.|
| POST   | `/api/search`              | Document search. Narrow to one repo with `repositoryId`. `{query,topK,repositoryId}`. |

### Repository IDs

Each repository has a stable hash `id` = `sha256("github.com/<owner>/<repo>")` truncated to
16 hex chars (e.g. `github.com/modelcontextprotocol/csharp-sdk` -> `b313806361dfd9a1`). The
same repo always maps to the same ID. Use `/api/repositories/search` (or the `search_repositories`
MCP tool) to discover a repo's ID, then pass it as `repositoryId` to narrow document search.

Job status reflects the processing pipeline (for example: queued, cloning,
parsing, chunking, embedding, done, or failed) along with per-file progress counts.

## MCP endpoint

The MCP server is exposed at `/mcp` over the Streamable HTTP transport. Point an
MCP client at `http://localhost:5171/mcp`.

Available tools:

- **`search_repositories`** — semantic search *for* repositories/tools by matching each
  repository's root README. Returns matches with their hash `id` (use it to narrow `search_docs`).
- **`search_docs`** — semantic search over the indexed Markdown, returning the most
  relevant chunks with their source metadata. Optionally narrow to one repo via `repositoryId`.
- **`list_repositories`** — list the repositories that have been indexed (with their hash IDs).
- **`job_status`** — check the status/progress of an indexing job.

During ingestion the repository's root `README.md` is embedded as a repo-level vector, which is
what `search_repositories` matches against.

## Configuration

Configuration binds to the `Library` section (see `appsettings.json`). Options can
be overridden via `appsettings.Local.json`, environment variables, or standard
ASP.NET Core configuration providers.

| Key                          | Description                                                                                   | Default                                                                          |
| ---------------------------- | --------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| `ConnectionString`           | Postgres connection string.                                                                    | `Host=localhost;Port=5432;Database=mcp_library;Username=postgres;Password=postgres` |
| `WorkDirectory`              | Directory where repositories are cloned. Empty string uses a temp directory default.           | `""` (temp dir)                                                                  |
| `CleanupClones`              | Delete the cloned repo from disk once ingestion finishes.                                       | `true`                                                                            |
| `Embedding.ApiKey`           | OpenRouter API key. **Required** — the app fails fast at startup if it is empty.                 | `""`                                                                             |
| `Embedding.BaseUrl`          | Embeddings API base URL.                                                                        | `https://openrouter.ai/api/v1`                                                   |
| `Embedding.Model`            | Embedding model identifier.                                                                     | `openai/text-embedding-3-small`                                                 |
| `Embedding.Dimensions`       | Vector dimension for the chosen model (must match the pgvector column size).                    | `1536`                                                                            |
| `Embedding.BatchSize`        | Number of chunks sent to the embeddings endpoint per request.                                   | `32`                                                                             |
| `Chunking.MaxChars`          | Target maximum characters per chunk.                                                            | `2000`                                                                            |
| `Chunking.Overlap`           | Character overlap between adjacent chunks split from the same section.                          | `200`                                                                            |
