# Initial Requirements — MCP Private Library

## Summary

An MCP (Model Context Protocol) server that ingests GitHub repositories,
extracts their Markdown documentation, and makes that documentation
semantically searchable. A simple web UI lets a user submit a repo URL for
processing and check on the progress of that processing job.

## Goals

- Given a GitHub URL (HTTPS or SSH, for cloning), clone the repo and find
  all Markdown files in it (recursively, from the repo root down).
- Process those Markdown files efficiently into chunks/embeddings suitable
  for semantic search.
- Store the resulting embeddings (and source text/metadata) in Postgres.
- Expose the semantic search capability over MCP so an MCP client (e.g. an
  agent) can query the indexed docs.
- Provide a minimal web interface for submitting a URL and viewing job
  progress.

## Web Interface

Minimal, no-frills UI:

- A text box to enter a URL.
- A submit button to kick off processing.
- A way to check the progress of processing for a given URL/job
  (e.g. a status button/page showing queued / cloning / parsing /
  chunking / embedding / done / failed, plus counts like
  "N of M files processed").

## URL Handling

- On submit, the URL is examined/validated.
- If it's a GitHub URL usable for cloning (HTTPS like
  `https://github.com/org/repo.git` or SSH like
  `git@github.com:org/repo.git`), clone it (shallow clone likely fine).
- Walk the cloned repo tree and find all `*.md` (and possibly `*.mdx`?)
  files at any depth.
- Non-GitHub / non-cloneable URLs: out of scope for now (may revisit).

## Processing Pipeline

1. Clone repo (or pull if already cloned/known) to local/temp storage.
2. Enumerate all Markdown files under the repo root.
3. For each file:
   - Parse/clean Markdown (strip front-matter, code fences handling TBD).
   - Chunk content into reasonably sized, semantically coherent pieces
     (e.g. by heading section, with size limits/overlap).
4. Generate embeddings for each chunk in an efficient (batched,
   parallelized where safe, resumable) manner.
5. Persist chunks + embeddings + metadata (source file path, repo, heading
   path, commit/sha, timestamps) to Postgres.
6. Track job/document processing status so the UI can report progress.

## Semantic Search

- Backed by Postgres (pgvector extension for vector similarity search).
- Search queries go through the same embedding model used for ingestion.
- Exposed as an MCP tool (e.g. `search_docs`) so MCP clients can query the
  indexed library.

## Tech Stack

- **Language/runtime:** C#
- **MCP:** official/community C# MCP server library (need to confirm
  exact package — e.g. `ModelContextProtocol` NuGet package from the
  MCP C# SDK).
- **Data access:** Dapper (micro-ORM) against Postgres.
- **Database:** PostgreSQL with `pgvector` extension for embedding storage
  and similarity search.
- **Embeddings:** OpenRouter for semantic encoding (embeddings API) — to
  be confirmed which embedding model(s) OpenRouter exposes/supports, and
  whether embeddings should instead go through a dedicated
  embeddings-specific provider if OpenRouter's embedding support is
  limited.
- **Web UI:** minimal — likely a small ASP.NET Core app (Razor Pages or
  a couple of API endpoints + a tiny static HTML/JS page) hosted alongside
  or in front of the MCP server.

## Open Questions

- Confirm OpenRouter actually offers an embeddings endpoint suitable for
  this use case (vs. calling an embedding provider directly, e.g. OpenAI,
  Voyage, Cohere) — "OpenRouter for semantic encoding (??)" flagged as
  needing verification.
- Which embedding model/dimension to standardize on (affects pgvector
  column sizing).
- Job queue mechanism: in-process background worker vs. a real queue
  (e.g. Postgres-backed job table with polling, or something like
  Hangfire).
- Re-processing/update strategy when a previously indexed repo changes
  (re-clone + diff vs. full re-index).
- Auth/access control for the web UI and MCP server (private library —
  presumably not public-facing without auth).
- Where cloned repos are stored (temp dir, cleaned up after processing?)
  and any size/time limits on clones.
- Chunking strategy specifics (by heading, by token count, overlap size).

## Non-Goals (for now)

- Non-GitHub sources (GitLab, Bitbucket, arbitrary git URLs, local
  filesystem paths) — may be added later.
- Non-Markdown file types (source code, PDFs, etc.) — may be added later.
- Multi-tenant / multi-user auth model — assume single private deployment
  for now.
