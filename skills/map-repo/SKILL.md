---
name: map-repo
description: Token-efficient code navigation through the MapRepo MCP server. Use whenever exploring, searching, or tracing code in a repository that is (or can be) indexed by map-repo — instead of reading whole files or grepping blindly.
---

# MapRepo — Token-Efficient Code Navigation

MapRepo is a resident MCP server (`http://127.0.0.1:5087/mcp`) that keeps per-repository semantic indexes (C# via Roslyn, TypeScript via syntax analysis) in isolated SQLite databases. Its entire purpose is to answer code-structure questions in bounded, evidence-backed responses so you never pay tokens for whole-file reads or false-positive grep hits.

## When to use it

| Situation | Use |
|---|---|
| "Who calls X?" / "What does X call?" (C#) | `find_callers` / `find_callees` — semantic, no false positives |
| Orienting in an unknown repository | `repo_overview` — counts, languages, hub symbols, top files (~1k tokens) |
| "Where is X defined?" | `search_symbols` — exact `filePath:line` evidence |
| Understanding a file without reading it | `file_outline` — every declaration, ordered by line |
| Reading code | `get_source` with an exact line range — never read whole files |
| Architecture / dependency questions | `get_graph` with `edgeKinds` filter |

**Do not use it for:** one-off text/regex hunts (grep is cheaper) or unregistered repos you will query once (indexing costs minutes). TypeScript edges are checker-resolved (`confidence: "semantic"`) when the semantic engine is active; edges marked `confidence: "syntax"` are name-matched — verify those with `get_source`.

## The token contract

Follow this escalation order; each step is strictly cheaper than the alternative it replaces:

1. `list_repositories` — discover what is already indexed. Never re-register a repo that is listed.
2. `repo_overview` — one call replaces an entire exploratory session (listing directories, opening files). Hub symbols tell you where the architecture lives.
3. `search_symbols` — get the `symbolId` and exact location. Filter with `kind` and `pathContains`. Textual noise is off by default; only pass `includeTextual: true` when hunting protocol strings.
4. `get_symbol` / `find_callers` / `find_callees` / `get_graph` — structure around a symbol. Keep `depth` ≤ 2 and `limit` ≤ 80 unless you have a reason. Pass `edgeKinds: ["calls"]` on `get_graph` when you only care about call flow.
5. `get_source` — fetch only the lines you need (symbol's `startLine`–`endLine` plus a few context lines). This replaces `Read` of the whole file.
6. `batch` — when the next 2–3 calls are already known (search → get_symbol → get_source), send them as one request: `{"calls": [{"tool": ..., "arguments": ...}, ...]}` (max 10; per-call failures don't abort the rest).

## Response format notes

- Edges are aggregated: one edge per (source, target, kind, file) with a `lines` array of call sites (capped at 8; `count` present when more exist).
- Constant fields (repository id, language, module, confidence) are omitted from tool responses — do not expect them per record.
- `truncated: true` on a graph means the node budget was hit; raise `limit` or narrow `edgeKinds` instead of re-calling blindly.

## Registering repositories

```
open_repository { rootPath, id?, solutionPath?, enabledModules?, includeTextualEvidence?, reindex? }
```

- Registration is persistent: the server restores every repo (watcher included) after a restart, reusing the stored index — do not reindex on session start.
- The file watcher applies incremental reindexing on save (~300 ms per changed C# file). Assume the index is fresh.
- `close_repository` stops the watcher but keeps data; `remove_repository` with `deleteData: true` deletes the repo's own database directory.

## If the server is down

`dotnet run --project MapRepo.Server` from the map-repo-server checkout (binds `http://0.0.0.0:5087`). Health check: `GET /health`. Web UI (3D graph): `http://127.0.0.1:5087/`.
