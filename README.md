# MCP Private Library

An MCP (Model Context Protocol) server that indexes the Markdown documentation of GitHub repositories and makes it semantically searchable. Submit a GitHub repo URL through a minimal web UI (or the HTTP API); the app clones the repo, extracts every Markdown file, chunks and embeds the content, and stores the vectors in Postgres. MCP clients can then run semantic search over the indexed docs. Built in C# / ASP.NET Core with Dapper for data access, PostgreSQL + [pgvector](https://github.com/pgvector/pgvector) for vector similarity search, and OpenRouter for embeddings (with an offline deterministic fallback embedder when no API key is set).

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

   If you leave the API key empty, the app falls back to an offline, deterministic
   embedder, which is handy for local development and testing without network access.
   (`appsettings.Local.json` is git-ignored.)

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

| Method | Path                     | Description                                             |
| ------ | ------------------------ | ------------------------------------------------------- |
| POST   | `/api/jobs`              | Submit a GitHub URL to start a new indexing job.        |
| GET    | `/api/jobs/{id}`         | Get the status and progress of a single job.            |
| GET    | `/api/jobs`              | List all jobs.                                          |
| GET    | `/api/repositories`      | List repositories that have been indexed.               |

Job status reflects the processing pipeline (for example: queued, cloning,
parsing, chunking, embedding, done, or failed) along with per-file progress counts.

## MCP endpoint

The MCP server is exposed at `/mcp` over the Streamable HTTP transport. Point an
MCP client at `http://localhost:5171/mcp`.

Available tools:

- **`search_docs`** — semantic search over the indexed Markdown, returning the most
  relevant chunks with their source metadata.
- **`list_repositories`** — list the repositories that have been indexed.
- **`job_status`** — check the status/progress of an indexing job.

## Configuration

Configuration binds to the `Library` section (see `appsettings.json`). Options can
be overridden via `appsettings.Local.json`, environment variables, or standard
ASP.NET Core configuration providers.

| Key                          | Description                                                                                   | Default                                                                          |
| ---------------------------- | --------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| `ConnectionString`           | Postgres connection string.                                                                    | `Host=localhost;Port=5432;Database=mcp_library;Username=postgres;Password=postgres` |
| `WorkDirectory`              | Directory where repositories are cloned. Empty string uses a temp directory default.           | `""` (temp dir)                                                                  |
| `CleanupClones`              | Delete the cloned repo from disk once ingestion finishes.                                       | `true`                                                                            |
| `Embedding.ApiKey`           | OpenRouter API key. If empty, a deterministic local embedder is used (offline-friendly).        | `""`                                                                             |
| `Embedding.BaseUrl`          | Embeddings API base URL.                                                                        | `https://openrouter.ai/api/v1`                                                   |
| `Embedding.Model`            | Embedding model identifier.                                                                     | `openai/text-embedding-3-small`                                                 |
| `Embedding.Dimensions`       | Vector dimension for the chosen model (must match the pgvector column size).                    | `1536`                                                                            |
| `Embedding.BatchSize`        | Number of chunks sent to the embeddings endpoint per request.                                   | `32`                                                                             |
| `Chunking.MaxChars`          | Target maximum characters per chunk.                                                            | `2000`                                                                            |
| `Chunking.Overlap`           | Character overlap between adjacent chunks split from the same section.                          | `200`                                                                            |
