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
| `open_repository` | Register/open + index + watch. `reindex` forces a rebuild; `includeTextualEvidence` opts into string-literal mining; `allowExternalSymbols` (C# only, default false) opts into indexing sibling projects outside `rootPath` pulled in via `ProjectReference` — otherwise their symbols/edges are dropped to keep each repository's index isolated. |
| `list_repositories` | Compact status by default (id, rootPath, symbols, relationships, indexing, diagnosticCount) — the discovery entry point. `includeDiagnostics: true` returns the full definition and diagnostic text per repository. |
| `repository_status` | Generation, counts, diagnostics, watcher/indexing state. |
| `reindex_repository` | Force a full rebuild. |
| `close_repository` | Stop the watcher, release memory; data and registration kept. |
| `remove_repository` | Unregister; `deleteData` also removes the repository's database directory. |
| `exclude_path` | Add a path substring to a repository's exclude list and purge already-indexed rows matching it immediately — no reindex needed. For scratch/generated folders (`.tmp`, project-specific build-verification directories) that keep polluting search results. |
| `repo_overview` | Cached-per-generation orientation map: counts by kind/language/project, edge kinds, top files, hub symbols. `includeGenerated: true` includes tool-generated files (designer/`.g.cs`/`.pb.cs`/`AssemblyInfo`/`obj`/`Generated`), excluded by default. |
| `search_symbols` | Name/path search with `kind` / `pathContains` filters and exact source evidence. `includeTextual: true` against a repository indexed with `includeTextualEvidence: false` returns a `diagnostic` field explaining why nothing textual came back. |
| `get_symbol` | One symbol with incoming/outgoing edges and neighbor records. |
| `file_outline` | Every declaration in a file, ordered by line — cheaper than reading the file. |
| `list_files` | Indexed files with declaration counts. |
| `get_source` | Exact line range (max 400 lines); path-traversal safe. Errors by default on `startLine > endLine` or `startLine` past EOF — pass `clamp: true` to auto-correct instead. `truncated` means the 400-line window cut off real content, not that you asked for more than the file has. |
| `find_callers` / `find_callees` | Semantic call edges around a symbol. `find_callers` defaults to `calls`; `find_callees` defaults to `calls`+`constructs` (so `new Foo()` sites count as callees); both accept an `edgeKinds` override. |
| `find_references` | Reference edges around a symbol. |
| `get_graph` | Bounded relationship graph; `edgeKinds` restricts traversal (e.g. `["calls"]`). |
| `batch` | Up to 10 tool calls in one request (~200KB combined response cap, `truncated`/`nextIndex` if hit); per-call errors don't abort the rest. |

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

Ignores `.git`, `.tmp`, `bin`, `obj`, `node_modules`, `dist`, `build`, `coverage` — one list (`MapRepo.Core.PathExclusions`) shared by the watcher and every language module, including the Node-based TypeScript semantic engine. Debounces 750 ms; handles create/change/rename/delete. C# changes take the incremental path (new files trigger one full module run that re-caches the workspace). String-literal mining (`textual-evidence`) is off by default — it inflated indexes ~30% with noise; enable per repository only when you need to search embedded protocol strings.

Project-specific scratch/generated folders that aren't in the universal list (a custom build-verification directory, for example) can be added per repository with `excludedPaths` on `open_repository`, or retroactively with the `exclude_path` tool — the latter purges already-indexed rows immediately, no reindex required.

`enabledModules` on `open_repository` must match a real module id (`csharp-roslyn`, `typescript-syntax` — see `GET /api/modules`); a filter that matches nothing fails loudly (surfaced via `repository_status`) instead of silently indexing zero symbols forever.

### TypeScript engine resolution

`GET /api/engines` reports availability. The semantic engine probes, in order: the repository's own `node_modules/typescript`, `MAPREPO_TS_LIB`, the server's `MapRepo.Server/tools/node_modules/typescript` (install once with `npm install --prefix MapRepo.Server/tools typescript@5`), and the global npm root. TypeScript 5.x is required (the v7 native preview does not expose the JS compiler API).

### Adding a language module

Reference `MapRepo.Core`, implement `IRepositoryLanguageModule` (optionally `IIncrementalAnalyzer` + `IRepositoryLifecycle`), emit the common IR, build as `MapRepo.Module*.dll` and copy to `MapRepo.Server/modules/`. The registry discovers it at startup. The internal parser is your choice — Tree-sitter, tsserver, compiler APIs — the MCP contract never changes.

If your module P/Invokes a native library (e.g. libclang via ClangSharp — see `MapRepo.Modules.Cpp`), two gotchas that only show up once it's actually deployed as a plugin, not while it builds standalone: `dotnet build` on a class library does **not** copy transitive native runtime assets to its own output even with `RuntimeIdentifier` set — only `dotnet publish -r <rid> --self-contained false` does, so that's the required build step for `modules/`, not a plain build. And once published, the native `.dll`s sit right next to your module `.dll`, but the default P/Invoke probe only checks the *host* executable's own directory, never a plugin's — load your native dependencies explicitly by full path (e.g. `NativeLibrary.Load`) in a static constructor so they're already resident in the process before any P/Invoke call needs them.

### Why hand-built modules instead of one universal parser

Each language module here is built against that language's own real compiler or language-service backend rather than one generic grammar shared across every language: the C# module runs on Roslyn/MSBuildWorkspace (the same platform the C# compiler itself uses), and the TypeScript module resolves to the TypeScript compiler's own language-service APIs (tsserver-equivalent) when available. A real compiler backend understands actual semantics — type resolution, overload matching, symbol identity that survives a rename — not just a syntax tree, which is what buys the low false-edge rate and precise call/reference resolution this project is built around.

The trade-off is coverage: a hand-built module per language is more accurate but slower to add than plugging in one universal parser that already covers dozens of languages out of the box. That's a deliberate choice, not an oversight — but it does mean MapRepo's language support grows one well-built module at a time.

This is exactly where contribution helps most. If you use a language MapRepo doesn't support yet, a new module is welcome — implement `IRepositoryLanguageModule` as above, backed by whatever fits that language: a real compiler/language service where one exists and semantic accuracy matters, or a lighter syntax-level parser (Tree-sitter and similar) where broad, fast coverage matters more than deep semantics. Deepening an existing module (more edge kinds, better incremental support) is just as welcome. PRs are genuinely wanted here.

## Web atlas

- **Orbit** (left drag) around the selected node — click any node to make it the pivot; double-click re-roots the graph on it.
- **Pan** (right/middle/Shift drag, arrow keys), **zoom** (wheel, `+`/`-`), **reset** (`0`).
- Search with kind filter chips, per-file outlines, overview dashboard with clickable hubs.
- Detail panel: signature, incoming/outgoing edges, exact source snippet on demand.
- Deep link: `/#repo=<id>&q=<query>` auto-loads the first match.

### Reading the detail panel's edge lists

Each row is `<kind> <symbol name>` — the kind badge names the relationship, the name is the *other*
symbol on that edge, not the selected one.

- **Incoming** = edges where the selected symbol is the target — other symbols point *at* it.
- **Outgoing** = edges where the selected symbol is the source — it points *at* other symbols.

| Kind | Meaning |
| --- | --- |
| `calls` | one method/function invokes another |
| `constructs` | `new X(...)` — one symbol instantiates a type |
| `contains` | declaration nesting — a type/namespace contains a member |
| `references` | reads/uses a symbol without calling or constructing it |
| `inherits` | class extends a base class |
| `implements` | class/struct implements an interface |
| `imports` | one module imports another (TypeScript) |

Example: selecting a method and seeing **Incoming: `contains` → `Crc32`** and **`calls` →
`HashFinal`** means the method is declared inside class `Crc32`, and a method named `HashFinal`
calls it.

## Security notes

The server has **no authentication** and binds all interfaces by default so LAN agents can reach it. Anyone who can reach the port can register paths readable by your user and fetch indexed source. On untrusted networks, bind loopback (`--urls http://127.0.0.1:5087`) or firewall the port. `get_source` refuses paths that escape the registered repository root.

## License

MIT
