// MapRepo TypeScript semantic analyzer.
// Runs the TypeScript compiler API (type checker) over a repository and emits the MapRepo IR as JSON.
// Usage: node ts-semantic.mjs --root <repoRoot> --ts <path/to/typescript.js> --repo <repositoryId>
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import * as path from 'node:path';
import * as fs from 'node:fs';

const args = {};
for (let i = 2; i < process.argv.length; i += 2) args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
const root = path.resolve(args.root);
const repositoryId = args.repo;
const require = createRequire(import.meta.url);
const ts = require(path.resolve(args.ts));

const MODULE_ID = 'typescript-syntax'; // stable module id: semantic and syntax engines own the same rows
const hash = v => createHash('sha256').update(v, 'utf8').digest('hex').slice(0, 24);
const rel = p => path.relative(root, p).replace(/\\/g, '/');
const EXCLUDED = new Set(['node_modules', 'dist', 'build', 'coverage', '.git', 'bin', 'obj', 'packages']);

function underRoot(p) {
  const r = path.relative(root, p);
  return r && !r.startsWith('..') && !path.isAbsolute(r) && !r.split(/[\\/]/).some(seg => EXCLUDED.has(seg));
}

function collectFiles(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) { if (!EXCLUDED.has(entry.name)) collectFiles(path.join(dir, entry.name), out); }
    else if (/\.(ts|tsx|js|jsx|mjs|cjs)$/i.test(entry.name) && !entry.name.endsWith('.d.ts'))
      out.push(path.join(dir, entry.name));
  }
}

const diagnostics = [];
let fileNames, options;
const configPath = ts.findConfigFile(root, ts.sys.fileExists, 'tsconfig.json');
if (configPath && underRoot(configPath)) {
  const parsed = ts.getParsedCommandLineOfConfigFile(configPath, { noEmit: true, skipLibCheck: true },
    { ...ts.sys, onUnRecoverableConfigFileDiagnostic: d => diagnostics.push(ts.flattenDiagnosticMessageText(d.messageText, ' ')) });
  fileNames = parsed ? parsed.fileNames : [];
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

const program = ts.createProgram(fileNames, options);
const checker = program.getTypeChecker();
const symbols = [];
const relationships = [];
const seenSymbols = new Set();
const seenEdges = new Set();

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

function symbolIdFor(node, sourceFile) {
  const file = rel(sourceFile.fileName);
  return hash(`${file}|${DECL_KINDS.get(node.kind)}|${qualifiedName(node)}`);
}

function language(fileName) {
  return /\.tsx$/i.test(fileName) ? 'tsx' : /\.(ts)$/i.test(fileName) ? 'typescript' : 'javascript';
}

function pos(sourceFile, p) {
  const lc = sourceFile.getLineAndCharacterOfPosition(p);
  return { line: lc.line + 1, column: lc.character + 1 };
}

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
    endLine: end.line, endColumn: end.column, signature, language: language(file), moduleId: MODULE_ID
  });
  return id;
}

function addEdge(sourceId, targetId, kind, sourceFile, node, confidence) {
  if (!sourceId || !targetId || sourceId === targetId && kind !== 'calls') { if (!sourceId || !targetId) return; }
  const file = rel(sourceFile.fileName);
  const p = pos(sourceFile, node.getStart(sourceFile));
  const id = hash(`${sourceId}|${targetId}|${kind}|${file}|${p.line}|${p.column}`);
  if (seenEdges.has(id)) return;
  seenEdges.add(id);
  relationships.push({
    id, repositoryId, sourceId, targetId, kind, filePath: file,
    line: p.line, column: p.column, confidence, language: language(file), moduleId: MODULE_ID
  });
}

// Resolve an expression to the id of a declaration inside this repository, via the type checker.
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

const sourceFiles = program.getSourceFiles().filter(sf => !sf.isDeclarationFile && underRoot(sf.fileName));
for (const sf of sourceFiles) {
  const file = rel(sf.fileName);
  const moduleSymbolId = hash(`${file}|module`);
  if (!seenSymbols.has(moduleSymbolId)) {
    seenSymbols.add(moduleSymbolId);
    symbols.push({
      id: moduleSymbolId, repositoryId, project: null, filePath: file, name: path.basename(file),
      qualifiedName: file, kind: 'module', startLine: 1, startColumn: 1,
      endLine: sf.getLineAndCharacterOfPosition(sf.getEnd()).line + 1, endColumn: 1,
      signature: file, language: language(file), moduleId: MODULE_ID
    });
  }
  const enclosingIdOf = node => {
    const d = enclosingDecl(node);
    return d ? symbolIdFor(d, sf) : moduleSymbolId;
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
      const resolved = ts.resolveModuleName(node.moduleSpecifier.text, sf.fileName, options, ts.sys);
      const resolvedFile = resolved?.resolvedModule?.resolvedFileName;
      if (resolvedFile && underRoot(resolvedFile))
        addEdge(moduleSymbolId, hash(`${rel(resolvedFile)}|module`), 'imports', sf, node, 'semantic');
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
    ts.forEachChild(node, visit);
  };
  visit(sf);
}

const known = new Set(symbols.map(s => s.id));
const boundedEdges = relationships.filter(e => known.has(e.sourceId) && known.has(e.targetId));
diagnostics.push(`ts-semantic: ${sourceFiles.length} files, ${symbols.length} symbols, ${boundedEdges.length} edges`);
process.stdout.write(JSON.stringify({ symbols, relationships: boundedEdges, diagnostics }));
