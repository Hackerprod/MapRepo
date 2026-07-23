# MapRepo.NativeStore 0.2.0

Motor embebido especializado para índices de inteligencia de código. Persiste símbolos, relaciones y revisiones por archivo; publica generaciones copy-on-write; y consulta un pack binario compacto mediante memoria administrada o `MemoryMappedFile`.

No utiliza SQLite, LiteDB, EF Core, SQL ni procesos externos.

## Alcance deliberado

Adecuado para:

- índices regenerables desde repositorios;
- un escritor lógico y múltiples lectores;
- reemplazo completo, por módulo o por archivos;
- búsqueda de símbolos y substring;
- recorridos de grafo entrantes/salientes;
- respuestas MCP compactas y presupuestadas.

No pretende ofrecer:

- SQL o consultas arbitrarias;
- múltiples escritores concurrentes por repositorio;
- transacciones de negocio;
- red o replicación;
- constraints configurables;
- almacenamiento de datos que no puedan reconstruirse.

## Uso

```csharp
using MapRepo.Core;
using MapRepo.NativeStore;

await using var store = new NativeRepositoryStore(new NativeStoreOptions
{
    RootDirectory = ".maprepo/native-store-v2",
    MemoryMode = NativeMemoryMode.MemoryMapped,
    MaxResidentRepositories = 2,
    MaxResidentManagedBytes = 256L * 1024 * 1024,
    DecodedStringCacheBytes = 16L * 1024 * 1024,
    MaterializedRecordCacheEntries = 2_048
});

await store.InitializeAsync();
await store.ReplaceAsync(snapshot);

var search = await store.SearchAsync("repo-id", "SOCacheSubscribed", 20);
var graph = await store.GraphAsync("repo-id", search.Items[0].Symbol.Id, depth: 2, limit: 100);
var status = await store.StatusAsync("repo-id");
```

### Commit incremental atómico

```csharp
await store.ApplyMutationAsync(new RepositoryMutation(
    RepositoryId: "repo-id",
    Generation: "watcher-1842",
    IndexedAt: DateTimeOffset.UtcNow,
    Diagnostics: [],
    ReplaceAll: false,
    PreserveExistingDiagnostics: true,
    ModuleReplacements: [],
    FileDeltas:
    [
        new FileModuleDelta(
            "csharp-roslyn",
            ["src/A.cs", "src/B.cs"],
            symbols,
            relationships)
    ]));
```

Todos los registros de un `FileModuleDelta` deben pertenecer al módulo y a uno de los archivos declarados. La librería rechaza inconsistencias antes del commit; no descarta registros silenciosamente.

## Modos de memoria

### `MemoryMapped` — recomendado

- el pack inmutable permanece en disco;
- solo la cabecera, índices auxiliares pequeños y cachés limitadas viven en el heap;
- strings y records se decodifican bajo demanda;
- páginas limpias pueden ser expulsadas por el sistema operativo;
- ofrece el perfil adecuado para múltiples repositorios grandes.

### `CompactManaged`

- carga el pack compacto en un único `byte[]`;
- conserva IDs enteros, tablas UTF-8, postings y CSR;
- evita el antiguo grafo de objetos, pero el tamaño completo del pack cuenta como heap;
- sirve para diagnóstico, comparación y entornos donde memory mapping no es deseable.

Ambos modos leen el mismo formato y ejecutan la misma suite funcional/hard-force.

## Ruta de datos

```text
<root>/<slug>__<sha256-corto>/
├── superblock.a
├── superblock.b
├── writer.lock
├── manifests/
│   └── manifest-<sequence>-<guid>.mrm
├── segments/
│   └── seg-<sequence>-<file-hash>-<guid>.mrs
├── snapshots/
│   └── snapshot-<sequence>-<guid>.mrp
└── tmp/
```

Los segmentos son la representación autoritativa de revisiones por `(moduleId, filePath)`. El snapshot `.mrp` es una proyección consolidada y regenerable para consulta rápida.

## Pack compacto v2

Magic de formato: `MRPACK02`.

El pack usa cabecera fija de 4096 bytes, directorio de secciones, CRC32C por sección y checksum raíz. Sus secciones contienen:

```text
StringOffsets / StringBytes
Files
Symbols / SymbolLookup
Relationships / RelationshipLookup
Lexemes / LexemePostings
Trigrams / TrigramPostings
FileTrigrams / FileTrigramPostings
OutlineSymbols
OutgoingOffsets / OutgoingEdges
IncomingOffsets / IncomingEdges
Overview
```

Características:

- strings deduplicadas en UTF-8;
- ordinals `int` en vez de IDs string repetidos;
- filas fijas para símbolos y relaciones;
- lexicón ordenado para prefijos, sin materializar todos los prefijos posibles;
- postings ordenados para intersección trigram;
- grafo CSR entrante y saliente;
- outlines preordenados por ubicación;
- overview persistido y cacheado por generación.

## Búsqueda

El plan combina señales deterministas:

1. nombre exacto;
2. nombre cualificado exacto;
3. rango lexicográfico de prefijo;
4. intersección trigram con verificación final;
5. postings de tokens y scoring BM25-like;
6. filtros por tipo, ruta y evidencia textual.

Una búsqueda negativa puede terminar al detectar un trigram inexistente, sin recorrer todos los símbolos.

## Grafo

Solo relaciones cuyos extremos existen en la generación activa ingresan a las adyacencias. Las relaciones no resueltas continúan contabilizadas y verificables, pero no crean nodos fantasma.

```text
OutgoingOffsets[symbolOrdinal..symbolOrdinal+1] -> edge ordinals
IncomingOffsets[symbolOrdinal..symbolOrdinal+1] -> edge ordinals
```

Las consultas de grafo no reconstruyen joins ni ejecutan una consulta por nodo.

## Proyecciones y tokens

`IProjectedRepositoryStore` expone:

- `Orientation`;
- `Compact`;
- `Full`;
- `GraphOnly`.

Cada resultado informa `Returned`, `HasMore` y `EstimatedTokens`. El estimador no sustituye al tokenizer exacto del modelo, pero centraliza el presupuesto y evita que cada tool improvise límites distintos.

## Concurrencia

- un `SemaphoreSlim` serializa escritores dentro del proceso;
- `writer.lock` con apertura exclusiva coordina procesos cooperativos;
- cada lector adquiere una referencia al snapshot completo de una generación;
- un writer construye archivos nuevos sin modificar la generación visible;
- el snapshot anterior se retira solo cuando finaliza su último lector;
- la mutación multi-módulo del watcher se publica como una sola generación.

## Commit y recuperación

Orden conceptual:

```text
validar mutación
→ escribir/flush segmentos nuevos
→ construir/flush pack nuevo
→ escribir/flush manifest nuevo
→ publicar superblock alterno
→ abrir y publicar snapshot en memoria
→ limpiar huérfanos best-effort
```

En apertura:

1. valida ambos superblocks;
2. prueba generaciones de mayor a menor;
3. valida manifest y referencias;
4. valida cabecera del pack y, con `VerifySnapshotPackChecksumsOnOpen=true`, todos sus CRC;
5. cae a la generación anterior si la última no es recuperable.

El perfil de servidor deja la verificación integral de cuerpo desactivada para no convertir cada apertura fría en un scan completo. La escritura ya calcula CRC por sección; `VerifyAsync` y el endpoint de verificación realizan el chequeo exhaustivo bajo demanda. Active `VerifyOnOpen` cuando prefiera recuperación inmediata frente a corrupción silenciosa del medio por encima de la latencia de apertura.

`VerifyAsync` realiza una validación completa y compara pack consolidado contra segmentos fuente.

## Cachés y expulsión

Las cachés están limitadas:

- strings decodificadas, por bytes;
- `SymbolRecord`/`RelationshipRecord` materializados, por entradas;
- handles pesados de repositorio, por cantidad, estimación de heap e inactividad.

`StatusAsync` tiene una ruta metadata-only: consultar estado no abre ni retiene el pack pesado cuando el repositorio no está residente.

Métricas:

```csharp
NativeStoreRuntimeStats stats = store.GetRuntimeStats();
```

Incluyen repos residentes, modo, estimación administrada, bytes del pack, cachés, heap GC, working set y private bytes.

## Integridad e identidad

Los analizadores usan `SemanticIdentity`, una codificación binaria versionada y length-delimited. Los display strings ya no definen el ID. NativeStore conserva validaciones contra:

- un ID de símbolo activo en archivos incompatibles;
- registros diferentes con el mismo ID;
- relaciones duplicadas incompatibles;
- registros incrementales fuera del archivo/módulo declarado;
- archivos persistentes truncados, sobredimensionados o con checksum incorrecto.

## Tests

El runner ejecuta cada contrato en ambos modos. Incluye:

- round-trip y consultas;
- conteo total vs relaciones resueltas;
- reemplazo incremental y validación estricta;
- colisiones de identidad;
- reemplazo por módulo;
- purgado;
- status metadata-only;
- LRU de repositorios;
- corrupción de superblock, manifest, segmento y pack;
- 15 puntos hard-force en procesos hijos;
- excepción posterior al commit;
- lectores durante escrituras;
- CRC32C;
- identidad estructural;
- presupuesto de proyección.

```bash
dotnet run -c Release --project MapRepo.NativeStore.Tests
```

## Benchmarks

```bash
dotnet run -c Release --project MapRepo.NativeStore.Benchmarks -- \
  --symbols 50000 --files 5000 --iterations 10 \
  --readers 8 --concurrent-writes 5 --mode both \
  --root benchmark-output
```

Reporta p50/p95/p99, bytes asignados por operación, heap, working set, private bytes, tamaño de pack y carga writer/readers.

## Límites actuales

- un solo escritor por repositorio;
- pack consolidado limitado a `Int32.MaxValue` por la vista contigua actual;
- cada generación reconstruye el pack consolidado O(N), aunque solo persista segmentos modificados;
- no existe todavía una jerarquía base-pack + deltas;
- flush de archivo no equivale a garantía universal de persistencia de directorio en todos los filesystems;
- el índice es regenerable y no debe almacenar la única copia de datos críticos.

Consulte `KNOWN-LIMITS.md` en la distribución integrada.
