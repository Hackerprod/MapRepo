# MapRepo Server

**A resident MCP server that gives coding agents token-efficient, semantically precise navigation over your repositories.**

MapRepo runs permanently on your machine, indexes the repositories you register into isolated per-project SQLite databases, keeps those indexes fresh with incremental file watching, and exposes 16 [Model Context Protocol](https://modelcontextprotocol.io) tools plus an interactive 3D web atlas. Agents ask *"who calls this?"*, *"what's in this file?"*, *"show me lines 178–190"* — and receive bounded, evidence-backed answers instead of paying tokens for whole-file reads and false-positive greps.

![3D Code Atlas](docs/atlas.png)

## Highlights

- **Semantic C# analysis** — Roslyn/`MSBuildWorkspace` resolves declarations, calls, references, inheritance and object construction with exact `file:line:column` evidence. A call to `Advance()` finds the 61 real callers, not the 170 textual matches.
- **Incremental indexing** — one live `MSBuildWorkspace` per repository; saving a file re-analyzes only the changed documents and replaces only their rows. Measured: **~300 ms** per changed file versus a 1–2 minute full solution reload.
- **Strict project isolation** — every repository owns its directory (`data-v4/<slug>__<hash>/index.db`, WAL mode). Nothing is shared; removing a repository deletes exactly its own data.
- **Restart-proof** — the registry (`data-v4/catalog.json`) restores every repository, watcher included, after a server restart, reusing stored indexes without reindexing.
- **Token-optimized wire format** — compact DTOs, call-site aggregation (one edge per caller with a `lines` array), SQL-level edge-kind filtering and dangling-edge pruning cut typical graph responses by ~73%.
- **Semantic TypeScript, incrementally** — when Node.js and a `typescript` library are available (repo `node_modules`, global npm, or the server's `tools/` folder), the TS module runs the real TypeScript type checker for resolved calls, references, inheritance and imports; it falls back to the built-in syntax scanner otherwise. Selectable per repository (`tsEngine`: auto | semantic | syntax). One Node process per repository stays resident (NDJSON over stdin/stdout) and reuses its parsed `Program` across saves — only changed files are re-parsed/re-bound. Measured: ~750 ms for a full pass, 60–360 ms per incrementally changed file against the same warm process (versus relaunching Node and re-parsing the whole repository on every save).
- **FTS5 search** — symbol search is backed by a SQLite FTS5 index (BM25-ranked prefix matching) with a LIKE fallback, so it stays fast on monorepo-scale indexes.
- **Language modules as plugins** — implement one interface, drop a DLL in `MapRepo.Server/modules/`, and hybrid repositories work with no server changes.
- **3D Code Atlas** — dependency-free force-directed 3D visualization: orbit around any selected symbol, pan, zoom, animated call pulses, per-file outlines, on-demand source snippets.

## Quick start

```powershell
dotnet run --project .\MapRepo.Server
```

The server binds `http://0.0.0.0:5087` (all interfaces; override with `--urls`). Open `http://127.0.0.1:5087/` for the atlas.

Register a repository (UI **+ Add**, REST, or the `open_repository` MCP tool):

```powershell
Invoke-RestMethod http://127.0.0.1:5087/api/repos/open -Method Post -ContentType 'application/json' -Body (@{
  id = 'my-repo'
  rootPath = 'D:\src\my-repo'
  solutionPath = 'D:\src\my-repo\MyRepo.sln'   # optional; auto-detected otherwise
} | ConvertTo-Json)
```

Indexing runs in the background; `GET /api/repos/{id}/status` reports progress. From then on the watcher keeps the index fresh on every save.

## Always-on autostart (Windows)

`dotnet run` dies with the terminal. To keep MapRepo resident on the machine, `scripts/manage-service.ps1` publishes the server and registers it under one of two configurable modes:

| Mode | Starts | Needs admin | Survives logoff |
| --- | --- | --- | --- |
| `Logon` | when you log in | no | no — stops at logoff |
| `Service` | at boot, before login | yes | yes |

```powershell
# No admin required — starts automatically at your next login:
.\scripts\manage-service.ps1 -Action Install -Mode Logon

# Run as Administrator — a real Windows Service, starts at boot:
.\scripts\manage-service.ps1 -Action Install -Mode Service

.\scripts\manage-service.ps1 -Action Status                 # registration + live health check
.\scripts\manage-service.ps1 -Action Start   -Mode Logon    # or -Mode Service
.\scripts\manage-service.ps1 -Action Stop
.\scripts\manage-service.ps1 -Action Uninstall -Mode Logon  # or -Mode Service
```

`-Port` (default 5087) and `-PublishDir` (default `<repo>\publish`) are configurable on every call; keep `-PublishDir` consistent between Install/Uninstall/Status so the script finds the right executable. The published copy manages its **own** `data-v4/` under `-PublishDir` — independent from a `dotnet run` dev checkout — so registering repositories through the installed instance doesn't touch your dev index and vice versa.

Registering a Scheduled Task for your own account normally needs no elevation; if `Register-ScheduledTask` reports Access Denied anyway, a Group Policy on the machine is restricting Task Scheduler — use `-Mode Service` instead (admin-only, always works). The script fails loudly (non-zero exit, no "installed" message) on any registration error rather than reporting false success.

## MCP integration

```json
{
  "mcpServers": {
    "map-repo": { "type": "http", "url": "http://127.0.0.1:5087/mcp" }
  }
}
```

Claude Code: `claude mcp add --scope user --transport http map-repo http://127.0.0.1:5087/mcp`

Streamable HTTP, legacy SSE (`/sse` + `/message`) and plain JSON-RPC POST are all supported.

### Tools

| Tool | Purpose |
| --- | --- |
| `open_repository` | Register/open + index + watch. `reindex` forces a rebuild; `includeTextualEvidence` opts into string-literal mining. |
| `list_repositories` | Registered repositories with status — the discovery entry point. |
| `repository_status` | Generation, counts, diagnostics, watcher/indexing state. |
| `reindex_repository` | Force a full rebuild. |
| `close_repository` | Stop the watcher, release memory; data and registration kept. |
| `remove_repository` | Unregister; `deleteData` also removes the repository's database directory. |
| `repo_overview` | Cached orientation map: counts by kind/language/project, edge kinds, top files, hub symbols. |
| `search_symbols` | Name/path search with `kind` / `pathContains` filters and exact source evidence. |
| `get_symbol` | One symbol with incoming/outgoing edges and neighbor records. |
| `file_outline` | Every declaration in a file, ordered by line — cheaper than reading the file. |
| `list_files` | Indexed files with declaration counts. |
| `get_source` | Exact line range (max 400 lines); path-traversal safe. |
| `find_callers` / `find_callees` | Semantic call edges around a symbol (SQL-filtered to `calls`). |
| `find_references` | Reference edges around a symbol. |
| `get_graph` | Bounded relationship graph; `edgeKinds` restricts traversal (e.g. `["calls"]`). |
| `batch` | Up to 10 tool calls in one request; per-call errors don't abort the rest. |

### Token contract

Recommended agent flow — each step bounded and cheaper than the alternative it replaces:

```
list_repositories → repo_overview → search_symbols → get_symbol / find_* / get_graph → get_source (exact lines only)
```

Chain the steps you already know into one round-trip with `batch`:

```json
{ "calls": [
  { "tool": "search_symbols", "arguments": { "repositoryId": "app", "query": "PaymentService" } },
  { "tool": "repo_overview",  "arguments": { "repositoryId": "app" } }
] }
```

The repository corpus never enters the model. See [`skills/map-repo/SKILL.md`](skills/map-repo/SKILL.md) for the full usage guide, including when plain grep is still the better tool.

## Architecture

```
MapRepo.Core             IR contract: SymbolRecord, RelationshipRecord, module interfaces
MapRepo.Modules.CSharp   Roslyn/MSBuildWorkspace semantic analysis, incremental deltas
MapRepo.Modules.TypeScript  Syntax-level TS/TSX/JS declarations, imports, calls
MapRepo.Server           ASP.NET Core host: sessions, watcher, SQLite stores, MCP + REST + web atlas
```

Every module emits the same intermediate representation; the session manager merges records into the repository's private database. Modules replace only their own rows, so a failing or partial module can never corrupt another module's data. Incremental-capable modules (`IIncrementalAnalyzer`) go further and replace only the changed files' rows.

### Watcher behavior

Ignores `.git`, `bin`, `obj`, `node_modules`, `dist`, `build`, `coverage`; debounces 750 ms; handles create/change/rename/delete. C# changes take the incremental path (new files trigger one full module run that re-caches the workspace). String-literal mining (`textual-evidence`) is off by default — it inflated indexes ~30% with noise; enable per repository only when you need to search embedded protocol strings.

### TypeScript engine resolution

`GET /api/engines` reports availability. The semantic engine probes, in order: the repository's own `node_modules/typescript`, `MAPREPO_TS_LIB`, the server's `MapRepo.Server/tools/node_modules/typescript` (install once with `npm install --prefix MapRepo.Server/tools typescript@5`), and the global npm root. TypeScript 5.x is required (the v7 native preview does not expose the JS compiler API).

### Adding a language module

Reference `MapRepo.Core`, implement `IRepositoryLanguageModule` (optionally `IIncrementalAnalyzer` + `IRepositoryLifecycle`), emit the common IR, build as `MapRepo.Module*.dll` and copy to `MapRepo.Server/modules/`. The registry discovers it at startup. The internal parser is your choice — Tree-sitter, tsserver, compiler APIs — the MCP contract never changes.

## Web atlas

- **Orbit** (left drag) around the selected node — click any node to make it the pivot; double-click re-roots the graph on it.
- **Pan** (right/middle/Shift drag, arrow keys), **zoom** (wheel, `+`/`-`), **reset** (`0`).
- Search with kind filter chips, per-file outlines, overview dashboard with clickable hubs.
- Detail panel: signature, incoming/outgoing edges, exact source snippet on demand.
- Deep link: `/#repo=<id>&q=<query>` auto-loads the first match.

## Security notes

The server has **no authentication** and binds all interfaces by default so LAN agents can reach it. Anyone who can reach the port can register paths readable by your user and fetch indexed source. On untrusted networks, bind loopback (`--urls http://127.0.0.1:5087`) or firewall the port. `get_source` refuses paths that escape the registered repository root.

## License

MIT
