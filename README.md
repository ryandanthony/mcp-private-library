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
| GET    | `/api/keys`                | List your API keys (never returns the secret).                          |
| POST   | `/api/keys`                | Create an API key. `{name, expiresInDays?}`. Returns the token **once**.|
| DELETE | `/api/keys/{id}`           | Revoke one of your API keys. Takes effect immediately.                  |

## Authentication

Two credentials are accepted, and they work side by side on both `/api` and `/mcp`. The
`Authorization` header's scheme decides which one is used, so neither can shadow the other:

| Caller                        | Header                              |
| ----------------------------- | ----------------------------------- |
| Browser UI                    | cookie session (OIDC login)         |
| OAuth-capable MCP client      | `Authorization: Bearer <jwt>`       |
| Scripts, CLIs, MCP hosts      | `Authorization: ApiKey <token>`     |

### API keys

API keys exist for clients that can't run an OAuth authorization-code flow. Each key is
**scoped to the user who created it** and acts entirely as that user.

Create and revoke them under **API keys** in the web UI, or via `/api/keys`. A key looks like:

```
mcpl_<keyId>_<secret>
```

Use it like this:

```bash
curl -H "Authorization: ApiKey mcpl_ab12cd34ef56gh78_xxxxxxxx" \
     https://library.ants.zone/api/repositories
```

Or in an MCP client config:

```json
{
  "mcp-private-library": {
    "type": "http",
    "url": "https://library.ants.zone/mcp",
    "headers": { "Authorization": "ApiKey mcpl_ab12cd34ef56gh78_xxxxxxxx" }
  }
}
```

Notes:

- The token is shown **once**, at creation. Only a SHA-256 hash is stored, so a lost token
  can't be recovered — create a new key instead.
- Revocation is immediate: validity is checked against the database on every request, so
  there's no window where a revoked key still works.
- Keys can optionally expire (`expiresInDays`); otherwise they last until revoked.
- **An API key cannot manage API keys.** `/api/keys` requires an interactive login (cookie or
  bearer token). If a leaked key could mint more keys, revoking it wouldn't end the compromise.

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

Authenticate with either an OAuth bearer token or an API key (see
[Authentication](#authentication)). Clients that support OAuth can discover the
authorization server from the `WWW-Authenticate` header on an unauthenticated request
(RFC 9728); simpler clients should send `Authorization: ApiKey <token>`.

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

## CI/CD and versioning

The project uses **trunk-based development** on `main` with automated semantic
versioning via [GitVersion](https://gitversion.net/) (config: `GitVersion.yml`).
Version numbers are tracked with git tags (`vMAJOR.MINOR.PATCH`).

### Workflows

- **`.github/workflows/ci.yml`** (pull requests into `main`): computes the
  would-be version, restores/builds/publishes the app, and does a Docker build
  (no push) to catch regressions before merge.
- **`.github/workflows/release.yml`** (push to `main`, i.e. each merged PR):
  1. GitVersion computes the next version from the latest tag plus commits.
  2. Creates and pushes a `vX.Y.Z` git tag.
  3. Builds a Docker image tagged with the version number and pushes it to GHCR
     (`ghcr.io/OWNER/REPO:X.Y.Z` and `:latest`).
  4. Creates a GitHub Release for the tag with generated notes.

The pushed tag is the version source for the next release, so versions increment
monotonically across merges.

### Bumping the version

Each merge bumps the **patch** version by default. To bump further, include a
directive in the merge/squash commit message:

- `+semver: minor` (or `+semver: feature`) bumps the minor version
- `+semver: major` (or `+semver: breaking`) bumps the major version
- `+semver: none` (or `+semver: skip`) skips the bump

### Pulling the image

```
docker pull ghcr.io/ryandanthony/mcp-private-library:latest
docker pull ghcr.io/ryandanthony/mcp-private-library:1.2.3
```

The runtime image includes `git` (required to clone submitted repositories) and
records the build version in the `APP_VERSION` env var and the
`org.opencontainers.image.version` label.
