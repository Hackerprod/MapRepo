// MapRepo TypeScript semantic analyzer — persistent daemon.
// Started once per repository and kept alive across file-watcher saves: the C# host writes one
// NDJSON request per line to stdin and reads one NDJSON response per line from stdout. Each
// request rebuilds the TS Program via `oldProgram` structural reuse (the same technique
// tsc --incremental/--watch use internally), so unchanged files are neither re-parsed nor
// re-bound — only the requested changed files are actually re-analyzed.
//
// Usage: node ts-semantic.mjs --root <repoRoot> --ts <path/to/typescript.js> --repo <repositoryId>
// Request  (one JSON object per line): { "files": ["/abs/path/a.ts", ...] | null, "excludePatterns": ["verify-build", ...] }
//   files: null means a full pass. excludePatterns are extra case-insensitive substrings (beyond
//   the built-in EXCLUDED set below) to skip — mirrors MapRepo.Core.PathExclusions on the C# side.
// Response (one JSON object per line):
//   full:        { "symbols": [...], "relationships": [...], "diagnostics": [...] }
//   incremental: { "filePaths": [...], "symbols": [...], "relationships": [...], "diagnostics": [...] }
//   error:       { "error": "message" }
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import * as path from 'node:path';
import * as fs from 'node:fs';
import * as readline from 'node:readline';

const args = {};
for (let i = 2; i < process.argv.length; i += 2) args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
const root = path.resolve(args.root);
const repositoryId = args.repo;
const require = createRequire(import.meta.url);
const ts = require(path.resolve(args.ts));

const MODULE_ID = 'typescript-syntax'; // stable module id: semantic and syntax engines own the same rows
const hash = v => createHash('sha256').update(v, 'utf8').digest('hex').slice(0, 24);
const normalizedPath = value => value.replace(/\\/g, '/');
const int32 = value => { const b = Buffer.allocUnsafe(4); b.writeInt32LE(value); return b; };
const stringBytes = value => { const b = Buffer.from(value ?? '', 'utf8'); return [int32(b.length), b]; };
const canonicalHash = (kind, strings, integers = []) => {
  const chunks = [Buffer.from([1]), ...stringBytes(kind)];
  for (const value of strings) chunks.push(...stringBytes(value));
  for (const value of integers) chunks.push(int32(value));
  return createHash('sha256').update(Buffer.concat(chunks)).digest('hex').slice(0, 24);
};
const declarationId = (lang, project, kind, structuralIdentity, file) =>
  canonicalHash('declaration', [lang, MODULE_ID, project, kind, structuralIdentity, normalizedPath(file)]);
const relationshipId = (sourceId, targetId, kind, file, line, column) =>
  canonicalHash('relationship', [MODULE_ID, sourceId, targetId, kind, normalizedPath(file)], [line, column]);
const rel = p => path.relative(root, p).replace(/\\/g, '/');
const EXCLUDED = new Set(['node_modules', 'dist', 'build', 'coverage', '.git', '.tmp', 'bin', 'obj']);
// "packages" and "Data" are deliberately NOT in this set: they are common names for legitimate,
// hand-authored source folders in a user's own repository (mirrors MapRepo.Core.PathExclusions).
let extraExcludePatterns = []; // refreshed per request from request.excludePatterns

function underRoot(p) {
  const r = path.relative(root, p);
  if (!r || r.startsWith('..') || path.isAbsolute(r)) return false;
  if (r.split(/[\\/]/).some(seg => EXCLUDED.has(seg))) return false;
  const lower = r.toLowerCase();
  return !extraExcludePatterns.some(pattern => pattern && lower.includes(pattern.toLowerCase()));
}

function collectFiles(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) { if (underRoot(full)) collectFiles(full, out); }
    else if (/\.(ts|tsx|js|jsx|mjs|cjs)$/i.test(entry.name) && !entry.name.endsWith('.d.ts') && underRoot(full))
      out.push(full);
  }
}

// Cheap on every request (globs/tsconfig parsing, not file content); lets add/removed files show
// up without a full re-parse of everything else, since `program` reuse below is per-file.
function resolveRootNames() {
  const diagnostics = [];
  let fileNames, options;
  const configPath = ts.findConfigFile(root, ts.sys.fileExists, 'tsconfig.json');
  if (configPath && underRoot(configPath)) {
    const parsed = ts.getParsedCommandLineOfConfigFile(configPath, { noEmit: true, skipLibCheck: true },
      { ...ts.sys, onUnRecoverableConfigFileDiagnostic: d => diagnostics.push(ts.flattenDiagnosticMessageText(d.messageText, ' ')) });
    fileNames = (parsed ? parsed.fileNames : []).filter(underRoot);
    options = parsed ? { ...parsed.options, noEmit: true, skipLibCheck: true } : {};
    diagnostics.push(`tsconfig: ${rel(configPath)}`);
  }
  if (!fileNames || fileNames.length === 0) {
    fileNames = [];
    collectFiles(root, fileNames);
    options = {
      allowJs: true, checkJs: false, noEmit: true, skipLibCheck: true,
      target: ts.ScriptTarget.ESNext, module: ts.ModuleKind.ESNext,
      moduleResolution: ts.ModuleResolutionKind.Bundler ?? ts.ModuleResolutionKind.NodeJs,
      jsx: ts.JsxEmit.Preserve, allowImportingTsExtensions: true
    };
  }
  return { fileNames, options, diagnostics };
}

const DECL_KINDS = new Map([
  [ts.SyntaxKind.ClassDeclaration, 'class'], [ts.SyntaxKind.InterfaceDeclaration, 'interface'],
  [ts.SyntaxKind.EnumDeclaration, 'enum'], [ts.SyntaxKind.TypeAliasDeclaration, 'type'],
  [ts.SyntaxKind.FunctionDeclaration, 'function'], [ts.SyntaxKind.MethodDeclaration, 'method'],
  [ts.SyntaxKind.MethodSignature, 'method'], [ts.SyntaxKind.PropertyDeclaration, 'property'],
  [ts.SyntaxKind.PropertySignature, 'property'], [ts.SyntaxKind.Constructor, 'constructor'],
  [ts.SyntaxKind.GetAccessor, 'getter'], [ts.SyntaxKind.SetAccessor, 'setter'],
  [ts.SyntaxKind.EnumMember, 'enum-member'], [ts.SyntaxKind.ModuleDeclaration, 'namespace'],
  [ts.SyntaxKind.VariableDeclaration, 'variable']
]);
// Declaration kinds that can actually own a "calls"/"constructs"/"references" edge as its source.
// Everything else in DECL_KINDS (variable, property, class, interface, ...) is a container a call
// can textually sit inside but never a thing that itself "calls" something.
const CALLABLE_DECL_KINDS = new Set(['function', 'method', 'constructor', 'getter', 'setter']);

function declName(node) {
  if (node.name) {
    if (ts.isIdentifier(node.name) || ts.isStringLiteral(node.name) || ts.isPrivateIdentifier(node.name)) return node.name.text;
    return node.name.getText();
  }
  if (node.kind === ts.SyntaxKind.Constructor) return 'constructor';
  return null;
}

function qualifiedName(node) {
  const parts = [];
  for (let n = node; n; n = n.parent) {
    if (DECL_KINDS.has(n.kind)) { const nm = declName(n); if (nm) parts.unshift(nm); }
  }
  return parts.join('.');
}

function enclosingDecl(node) {
  for (let n = node.parent; n; n = n.parent) if (DECL_KINDS.has(n.kind) && declName(n)) return n;
  return null;
}

function structuralIdentityFor(node, sourceFile = node.getSourceFile()) {
  const kind = DECL_KINDS.get(node.kind);
  const typeArity = node.typeParameters?.length ?? 0;
  const parameters = (node.parameters ?? []).map(parameter => {
    const modifiers = parameter.modifiers?.map(modifier => modifier.getText(sourceFile)).join('+') ?? '';
    const type = parameter.type?.getText(sourceFile) ?? '?';
    const rest = parameter.dotDotDotToken ? 'rest' : '';
    return `${modifiers}:${rest}:${type.replace(/\s+/g, '')}`;
  }).join(',');
  const returnType = node.type?.getText(sourceFile)?.replace(/\s+/g, '') ?? '';
  // qualifiedName() only walks ancestors whose kind is in DECL_KINDS — arrow functions, function
  // expressions, blocks and if-statements are not, so two local variables with the same name in two
  // different callbacks nested inside the same named enclosing scope (e.g. two `onMessage('X', ctx
  // => { const data = ... })` handlers in the same setup function) collapse to the same
  // qualifiedName and, for `variable`, would otherwise get the identical structural identity —
  // discovered via a real collision in a large protocol-handler file. A local variable has no
  // stable identity beyond its declaration site anyway, so disambiguate it with position.
  const positional = kind === 'variable' ? `:${node.getStart(sourceFile)}` : '';
  return `typescript-semantic:v2:${kind}:${qualifiedName(node)}:${typeArity}:${parameters}:${returnType}${positional}`;
}

function symbolIdFor(node, sourceFile) {
  const file = rel(sourceFile.fileName);
  const kind = DECL_KINDS.get(node.kind);
  return declarationId(language(file), '(semantic)', kind, structuralIdentityFor(node, sourceFile), file);
}

function moduleIdentityFor(file) {
  return `typescript-semantic:v1:module:${normalizedPath(file)}`;
}

function moduleIdFor(file) {
  return declarationId(language(file), '(semantic)', 'module', moduleIdentityFor(file), file);
}

function language(fileName) {
  return /\.tsx$/i.test(fileName) ? 'tsx' : /\.(ts)$/i.test(fileName) ? 'typescript' : 'javascript';
}

function pos(sourceFile, p) {
  const lc = sourceFile.getLineAndCharacterOfPosition(p);
  return { line: lc.line + 1, column: lc.character + 1 };
}

// Structural-reuse compiler host: returns the SAME SourceFile object for a path across requests
// unless that path was explicitly invalidated (changed), so `ts.createProgram(..., oldProgram)`
// can skip re-lexing/re-binding every file that didn't change — the actual perf win.
function createReusableHost(options) {
  const base = ts.createCompilerHost(options, true);
  const cache = new Map(); // absolute path -> ts.SourceFile
  const host = { ...base };
  host.getSourceFile = (fileName, languageVersionOrOptions, onError, shouldCreateNewSourceFile) => {
    const resolved = path.resolve(fileName);
    if (!shouldCreateNewSourceFile && cache.has(resolved)) return cache.get(resolved);
    const sf = base.getSourceFile(fileName, languageVersionOrOptions, onError, shouldCreateNewSourceFile);
    if (sf) cache.set(resolved, sf);
    return sf;
  };
  return { host, invalidate: fileName => cache.delete(path.resolve(fileName)) };
}

const { host, invalidate } = createReusableHost({});
let program; // reused across requests via oldProgram
let checker;

function analyzeTargets(targetFiles) {
  // Fresh per request: dedup within THIS response only. Cross-request/-file dedup is the
  // database's job (primary key on symbol/edge id, which is stable across requests).
  const symbols = [];
  const relationships = [];
  const seenSymbols = new Set();
  const seenEdges = new Set();

  function addSymbol(node, sourceFile) {
    const name = declName(node);
    if (!name) return null;
    const id = symbolIdFor(node, sourceFile);
    if (seenSymbols.has(id)) return id;
    seenSymbols.add(id);
    const file = rel(sourceFile.fileName);
    const start = pos(sourceFile, node.getStart(sourceFile));
    const end = pos(sourceFile, node.getEnd());
    const signature = node.getText(sourceFile).split('\n')[0].trim().slice(0, 200);
    symbols.push({
      id, repositoryId, project: null, filePath: file, name, qualifiedName: qualifiedName(node),
      kind: DECL_KINDS.get(node.kind), startLine: start.line, startColumn: start.column,
      endLine: end.line, endColumn: end.column, signature, language: language(file), moduleId: MODULE_ID,
      structuralIdentity: structuralIdentityFor(node, sourceFile)
    });
    return id;
  }

  function addEdge(sourceId, targetId, kind, sourceFile, node, confidence) {
    if (!sourceId || !targetId) return;
    const file = rel(sourceFile.fileName);
    const p = pos(sourceFile, node.getStart(sourceFile));
    const id = relationshipId(sourceId, targetId, kind, file, p.line, p.column);
    if (seenEdges.has(id)) return;
    seenEdges.add(id);
    relationships.push({
      id, repositoryId, sourceId, targetId, kind, filePath: file,
      line: p.line, column: p.column, confidence, language: language(file), moduleId: MODULE_ID,
      structuralIdentity: `relationship:v1:${sourceId}:${targetId}:${kind}:${normalizedPath(file)}:${p.line}:${p.column}`
    });
  }

  // Resolve an expression to the id of a declaration inside this repository, via the type checker.
  // Works for symbols in files outside `targetFiles` too — the whole program is still in memory,
  // we just aren't re-emitting rows for files that didn't change.
  function resolveTargetId(expr) {
    let sym = checker.getSymbolAtLocation(expr);
    if (!sym) return null;
    if (sym.flags & ts.SymbolFlags.Alias) { try { sym = checker.getAliasedSymbol(sym); } catch { return null; } }
    const decls = sym.declarations || [];
    for (const d of decls) {
      const sf = d.getSourceFile();
      if (sf.isDeclarationFile || !underRoot(sf.fileName)) continue;
      let target = d;
      if (!DECL_KINDS.has(target.kind)) {
        const up = enclosingDecl(target);
        if (!up) continue;
        target = up;
      }
      if (!declName(target)) continue;
      return symbolIdFor(target, sf);
    }
    return null;
  }

  for (const sf of targetFiles) {
    const file = rel(sf.fileName);
    const moduleSymbolId = moduleIdFor(file);
    if (!seenSymbols.has(moduleSymbolId)) {
      seenSymbols.add(moduleSymbolId);
      symbols.push({
        id: moduleSymbolId, repositoryId, project: null, filePath: file, name: path.basename(file),
        qualifiedName: file, kind: 'module', startLine: 1, startColumn: 1,
        endLine: sf.getLineAndCharacterOfPosition(sf.getEnd()).line + 1, endColumn: 1,
        signature: file, language: language(file), moduleId: MODULE_ID,
        structuralIdentity: moduleIdentityFor(file)
      });
    }
    // The edge SOURCE for calls/constructs/references must be a callable owner, not just the
    // nearest named declaration: a call inside `const summary = compute(...)` sits directly under
    // a VariableDeclaration node, so stopping at the first DECL_KINDS ancestor (enclosingDecl)
    // reports the local variable "summary" as the caller instead of the enclosing function/method.
    // Walk past non-callable containers (variable, property, class, ...) until a real callable is
    // found, falling back to the module symbol for genuinely top-level module-scope calls.
    const enclosingIdOf = node => {
      for (let n = node.parent; n; n = n.parent) {
        if (DECL_KINDS.has(n.kind) && declName(n) && CALLABLE_DECL_KINDS.has(DECL_KINDS.get(n.kind)))
          return symbolIdFor(n, sf);
      }
      return moduleSymbolId;
    };

    const visit = node => {
      if (DECL_KINDS.has(node.kind)) {
        const id = addSymbol(node, sf);
        if (id) {
          const parent = enclosingDecl(node);
          addEdge(parent ? symbolIdFor(parent, sf) : moduleSymbolId, id, 'contains', sf, node, 'semantic');
        }
        if ((ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node)) && node.heritageClauses) {
          for (const clause of node.heritageClauses) {
            const kind = clause.token === ts.SyntaxKind.ExtendsKeyword && ts.isClassDeclaration(node) ? 'inherits'
              : clause.token === ts.SyntaxKind.ExtendsKeyword ? 'inherits' : 'implements';
            for (const typeNode of clause.types) {
              const targetId = resolveTargetId(typeNode.expression);
              if (targetId && id) addEdge(id, targetId, kind, sf, typeNode, 'semantic');
            }
          }
        }
      }
      else if (ts.isCallExpression(node)) {
        const callee = ts.isPropertyAccessExpression(node.expression) ? node.expression.name : node.expression;
        const targetId = resolveTargetId(callee);
        if (targetId) addEdge(enclosingIdOf(node), targetId, 'calls', sf, node, 'semantic');
      }
      else if (ts.isNewExpression(node)) {
        const targetId = resolveTargetId(node.expression);
        if (targetId) addEdge(enclosingIdOf(node), targetId, 'constructs', sf, node, 'semantic');
      }
      else if (ts.isImportDeclaration(node) && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
        const resolved = ts.resolveModuleName(node.moduleSpecifier.text, sf.fileName, program.getCompilerOptions(), ts.sys);
        const resolvedFile = resolved?.resolvedModule?.resolvedFileName;
        if (resolvedFile && underRoot(resolvedFile))
          addEdge(moduleSymbolId, moduleIdFor(rel(resolvedFile)), 'imports', sf, node, 'semantic');
      }
      else if (ts.isIdentifier(node) && !ts.isPropertyAccessExpression(node.parent) && node.parent
        && !DECL_KINDS.has(node.parent.kind) && !ts.isImportSpecifier(node.parent) && !ts.isImportClause(node.parent)) {
        const sym = checker.getSymbolAtLocation(node);
        if (sym && sym.declarations?.length) {
          const d = sym.declarations[0];
          if (d.kind !== ts.SyntaxKind.Parameter && !d.getSourceFile().isDeclarationFile && underRoot(d.getSourceFile().fileName)) {
            const targetId = resolveTargetId(node);
            const sourceId = enclosingIdOf(node);
            if (targetId && targetId !== sourceId) addEdge(sourceId, targetId, 'references', sf, node, 'semantic');
          }
        }
      }
      // A property accessed as a value — `Msg.GCToClientTeamsInfo` read outside a call/new — never
      // reaches the branch above: its identifier's parent IS the PropertyAccessExpression, which
      // that branch deliberately excludes (it's already handled here, or by isCallExpression/
      // isNewExpression above when it's a callee/constructor target). Without this, any property
      // only ever consumed as `Namespace.Member` (the common shape for generated lookup tables)
      // could never show up in find_references at all.
      else if (ts.isPropertyAccessExpression(node)
        && !(ts.isCallExpression(node.parent) && node.parent.expression === node)
        && !(ts.isNewExpression(node.parent) && node.parent.expression === node)) {
        const targetId = resolveTargetId(node.name);
        const sourceId = enclosingIdOf(node);
        if (targetId && targetId !== sourceId) addEdge(sourceId, targetId, 'references', sf, node.name, 'semantic');
      }
      ts.forEachChild(node, visit);
    };
    visit(sf);
  }

  return { symbols, relationships };
}

/** Rebuilds `program`/`checker`, reusing every SourceFile whose path wasn't invalidated. */
function rebuildProgram(changedAbsolutePaths) {
  for (const p of changedAbsolutePaths) invalidate(p);
  const { fileNames, options, diagnostics } = resolveRootNames();
  program = ts.createProgram(fileNames, options, host, program);
  checker = program.getTypeChecker();
  return diagnostics;
}

function handleRequest(request) {
  extraExcludePatterns = Array.isArray(request?.excludePatterns) ? request.excludePatterns : [];
  const requestedFiles = Array.isArray(request?.files) ? request.files.map(f => path.resolve(f)) : null;
  const configDiagnostics = rebuildProgram(requestedFiles ?? []);
  const allSourceFiles = program.getSourceFiles().filter(sf => !sf.isDeclarationFile && underRoot(sf.fileName));

  if (requestedFiles === null) {
    // Full pass: every source file, snapshot semantics (caller replaces the whole module's rows).
    const { symbols, relationships } = analyzeTargets(allSourceFiles);
    const known = new Set(symbols.map(s => s.id));
    const boundedEdges = relationships.filter(e => known.has(e.sourceId) && known.has(e.targetId));
    const diagnostics = [...configDiagnostics, `ts-semantic (full): ${allSourceFiles.length} files, ${symbols.length} symbols, ${boundedEdges.length} edges`];
    return { symbols, relationships: boundedEdges, diagnostics };
  }

  // Incremental pass: only the requested files get re-walked. Edges may legitimately target
  // symbols outside this set (resolved above via the still-fully-loaded `program`/`checker`) —
  // the host-side store reconciles those against already-stored rows, same contract as the
  // Roslyn incremental path.
  const byPath = new Map(allSourceFiles.map(sf => [path.resolve(sf.fileName), sf]));
  const targets = requestedFiles.map(f => byPath.get(f)).filter(Boolean);
  const { symbols, relationships } = analyzeTargets(targets);
  const filePaths = requestedFiles.map(rel);
  const diagnostics = [...configDiagnostics, `ts-semantic (incremental): ${targets.length}/${requestedFiles.length} file(s) resolved, ${symbols.length} symbols, ${relationships.length} edges`];
  return { filePaths, symbols, relationships, diagnostics };
}

const rl = readline.createInterface({ input: process.stdin, terminal: false });
rl.on('line', line => {
  line = line.trim();
  if (!line) return;
  let response;
  try {
    const request = JSON.parse(line);
    response = handleRequest(request);
  } catch (ex) {
    response = { error: `${ex?.message ?? ex}` };
  }
  process.stdout.write(JSON.stringify(response) + '\n');
});
rl.on('close', () => process.exit(0));
