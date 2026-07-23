using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MapRepo.Core;
using MapRepo.NativeStore;
using MapRepo.NativeStore.Identity;
using MapRepo.NativeStore.Internal.Kernel;
using MapRepo.NativeStore.Internal.Packing;
using MapRepo.NativeStore.Projection;

namespace MapRepo.NativeStore.Tests;

internal static class Program
{
    private static NativeMemoryMode _memoryMode = NativeMemoryMode.MemoryMapped;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--crash-worker")
            return await RunCrashWorkerAsync(args);

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("full round-trip and native queries", FullRoundTripAsync),
            ("total and resolved relationship counts remain distinct", RelationshipCountParityAsync),
            ("incremental file revision preserves other files", IncrementalRevisionAsync),
            ("incremental deltas reject records outside their declared files", InvalidIncrementalDeltaAsync),
            ("duplicate symbol identity rejected before commit", DuplicateIdentityAsync),
            ("conflicting same-file identity rejected before commit", ConflictingSameFileIdentityAsync),
            ("duplicate relationship identity rejected before commit", DuplicateRelationshipIdentityAsync),
            ("path purge removes symbols and dangling graph edges", PurgePathAsync),
            ("module-scoped replacement preserves other modules", ModuleScopedReplacementAsync),
            ("metadata-only status does not retain a heavy handle", MetadataOnlyStatusAsync),
            ("resident repository LRU obeys its hard count", ResidentRepositoryLruAsync),
            ("corrupt latest superblock falls back one generation", CorruptSuperblockFallbackAsync),
            ("corrupt latest manifest falls back one generation", CorruptManifestFallbackAsync),
            ("corrupt latest segment falls back one generation", CorruptSegmentFallbackAsync),
            ("corrupt latest snapshot pack falls back one generation", CorruptSnapshotFallbackAsync),
            ("deterministic hard-force crash matrix", HardForceMatrixAsync),
            ("post-commit exception reloads the durable snapshot", PostCommitExceptionReloadAsync),
            ("concurrent readers observe complete snapshots", ConcurrentReadersAsync),
            ("CRC32C matches the standard check vector", Crc32CVectorAsync),
            ("semantic identity is structural and deterministic", TypedIdentityAsync),
            ("projection budget reports hasMore", ProjectionBudgetAsync)
        };

        var failures = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        foreach (var mode in new[] { NativeMemoryMode.CompactManaged, NativeMemoryMode.MemoryMapped })
        {
            _memoryMode = mode;
            Console.WriteLine($"\n=== {mode} ===");
            foreach (var test in tests)
            {
                var started = Stopwatch.StartNew();
                var qualifiedName = $"{mode}: {test.Name}";
                try
                {
                    await test.Run();
                    Console.WriteLine($"PASS  {qualifiedName} ({started.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    failures.Add(qualifiedName);
                    Console.WriteLine($"FAIL  {qualifiedName}\n{ex}");
                }
            }
        }

        var total = tests.Length * 2;
        Console.WriteLine($"\n{total - failures.Count}/{total} passed in {stopwatch.Elapsed}.");
        if (failures.Count == 0) return 0;
        Console.WriteLine("Failed: " + string.Join(", ", failures));
        return 1;
    }

    private static async Task FullRoundTripAsync()
    {
        using var temp = new TemporaryDirectory();
        var repositoryId = "round-trip";
        var symbols = new[]
        {
            Symbol(repositoryId, "s-build", "src/IndexBuilder.cs", "BuildIndex", "MapRepo.IndexBuilder.BuildIndex(string)"),
            Symbol(repositoryId, "s-cache", "src/Cache.cs", "SOCacheSubscribed", "MapRepo.Cache.SOCacheSubscribed"),
            Symbol(repositoryId, "s-text", "src/Cache.cs", "LobbyInvite", "LobbyInvite", "textual-evidence")
        };
        var edges = new[] { Edge(repositoryId, "e1", "s-build", "s-cache", "calls", "src/IndexBuilder.cs") };
        await using (var store = CreateStore(temp.Path))
        {
            await store.ReplaceAsync(Snapshot(repositoryId, "g1", symbols, edges));
            var status = await store.StatusAsync(repositoryId);
            Equal("g1", status.Generation);
            Equal(3, status.Symbols);
            Equal(1, status.Relationships);

            var exact = await store.SearchAsync(repositoryId, "BuildIndex", 10);
            Equal(1, exact.Items.Count);
            Equal("s-build", exact.Items[0].Symbol.Id);
            Equal(100.0, exact.Items[0].Score);

            var substring = await store.SearchAsync(repositoryId, "scribed", 10);
            True(substring.Items.Any(item => item.Symbol.Id == "s-cache"), "Trigram substring search missed SOCacheSubscribed.");
            var negative = await store.SearchAsync(repositoryId, "definitely-not-present-7f2c", 10);
            Equal(0, negative.Items.Count);

            var graph = await store.GraphAsync(repositoryId, "s-build", 2, 20);
            True(graph.Nodes.Any(node => node.Id == "s-cache"));
            Equal(1, graph.Edges.Count);
            True(!graph.Truncated, "A graph that fits exactly must not be marked truncated.");
            var boundedGraph = await store.GraphAsync(repositoryId, "s-build", 2, 1);
            Equal(1, boundedGraph.Nodes.Count);
            True(boundedGraph.Truncated, "A hidden neighboring node must mark the graph truncated.");
            var isolatedGraph = await store.GraphAsync(repositoryId, "s-text", 2, 1);
            True(!isolatedGraph.Truncated, "count == limit is not evidence of truncation.");

            var files = await store.FilesAsync(repositoryId, "cache", 20);
            Equal(1, files.Count);
            Equal("src/Cache.cs", files[0].FilePath);
            Equal(1, files[0].Symbols); // textual evidence is excluded from file counts.

            var outline = await store.OutlineAsync(repositoryId, "src/Cache.cs", 20);
            Equal(1, outline.Symbols.Count);
            Equal("s-cache", outline.Symbols[0].Id);
        }

        await using var reopened = CreateStore(temp.Path);
        var reopenedStatus = await reopened.StatusAsync(repositoryId);
        Equal("g1", reopenedStatus.Generation);
        Equal(3, reopenedStatus.Symbols);
        Equal(1, (await reopened.SearchAsync(repositoryId, "BuildIndex", 10)).Items.Count);
        var verification = await reopened.VerifyAsync(repositoryId);
        True(verification.IsValid);
        Equal("g1", verification.Generation);
        Equal(3, verification.Symbols);
        Equal(1, verification.Relationships);
    }

    private static async Task RelationshipCountParityAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "relationship-counts";
        await using var store = CreateStore(temp.Path);
        await store.ReplaceAsync(Snapshot(repo, "g1",
        [
            Symbol(repo, "s-a", "src/A.cs", "A", "Demo.A"),
            Symbol(repo, "s-b", "src/B.cs", "B", "Demo.B")
        ],
        [
            Edge(repo, "e-resolved", "s-a", "s-b", "calls", "src/A.cs"),
            Edge(repo, "e-unresolved", "s-a", "external-missing", "references", "src/A.cs")
        ]));

        var status = await store.StatusAsync(repo);
        Equal(2, status.Relationships, "Status must preserve all stored relationships, including unresolved endpoints.");
        var overview = await store.OverviewAsync(repo);
        Equal(2, overview.Relationships);
        Equal(2, overview.EdgeKinds.Sum(static value => value.Count));
        var graph = await store.GraphAsync(repo, "s-a", 2, 20);
        Equal(1, graph.Edges.Count, "Only resolved relationships belong to traversable adjacency.");
        var verification = await store.VerifyAsync(repo);
        Equal(2, verification.Relationships);
        Equal(1, verification.ResolvedRelationships);
    }

    private static async Task IncrementalRevisionAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "incremental";
        await using var store = CreateStore(temp.Path);
        await store.ReplaceAsync(Snapshot(repo, "g1",
        [
            Symbol(repo, "s-a", "src/A.cs", "OldName", "Demo.A.OldName"),
            Symbol(repo, "s-b", "src/B.cs", "StableName", "Demo.B.StableName")
        ], []));

        await store.ReplaceFilesAsync(repo, "csharp-roslyn", ["src/A.cs"],
            [Symbol(repo, "s-a", "src/A.cs", "NewName", "Demo.A.NewName")],
            [], "g2", DateTimeOffset.UtcNow);

        Equal(0, (await store.SearchAsync(repo, "OldName", 10)).Items.Count);
        Equal("s-a", (await store.SearchAsync(repo, "NewName", 10)).Items.Single().Symbol.Id);
        Equal("s-b", (await store.SearchAsync(repo, "StableName", 10)).Items.Single().Symbol.Id);
        var status = await store.StatusAsync(repo);
        Equal("g2", status.Generation);
        Equal(2, status.Symbols);
    }

    private static async Task InvalidIncrementalDeltaAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "invalid-incremental";
        await using var store = CreateStore(temp.Path);
        await store.ReplaceAsync(Snapshot(repo, "g1",
            [Symbol(repo, "s-a", "src/A.cs", "Stable", "Demo.Stable")], []));

        await ThrowsAsync<InvalidDataException>(() => store.ReplaceFilesAsync(
            repo,
            "csharp-roslyn",
            ["src/A.cs"],
            [Symbol(repo, "s-b", "src/B.cs", "Outside", "Demo.Outside")],
            [],
            "g2",
            DateTimeOffset.UtcNow));

        var wrongModule = Symbol(repo, "s-ts", "src/A.cs", "WrongModule", "Demo.WrongModule") with
        {
            Language = "typescript",
            ModuleId = "typescript-semantic"
        };
        await ThrowsAsync<InvalidDataException>(() => store.ReplaceFilesAsync(
            repo,
            "csharp-roslyn",
            ["src/A.cs"],
            [wrongModule],
            [],
            "g2",
            DateTimeOffset.UtcNow));

        var status = await store.StatusAsync(repo);
        Equal("g1", status.Generation, "A rejected delta changed the visible generation.");
        Equal(1, status.Symbols);
        Equal("s-a", (await store.SearchAsync(repo, "Stable", 10)).Items.Single().Symbol.Id);
    }

    private static async Task DuplicateIdentityAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "duplicate";
        await using var store = CreateStore(temp.Path);
        await ThrowsAsync<DuplicateSymbolIdentityException>(() => store.ReplaceAsync(Snapshot(repo, "bad",
        [
            Symbol(repo, "same-id", "src/A.cs", "A", "Demo.A"),
            Symbol(repo, "same-id", "src/B.cs", "B", "Demo.B")
        ], [])));
        var status = await store.StatusAsync(repo);
        True(status.Generation is null, "Rejected generation was published.");
        Equal(0, status.Symbols);
    }

    private static async Task ConflictingSameFileIdentityAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "same-file-conflict";
        await using var store = CreateStore(temp.Path);
        await ThrowsAsync<ConflictingRecordIdentityException>(() => store.ReplaceAsync(Snapshot(repo, "bad",
        [
            Symbol(repo, "same-id", "src/A.cs", "First", "Demo.First"),
            Symbol(repo, "same-id", "src/A.cs", "Second", "Demo.Second")
        ], [])));
        True((await store.StatusAsync(repo)).Generation is null);
    }

    private static async Task DuplicateRelationshipIdentityAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "duplicate-edge";
        await using var store = CreateStore(temp.Path);
        var symbols = new[]
        {
            Symbol(repo, "s-a", "src/A.cs", "A", "Demo.A"),
            Symbol(repo, "s-b", "src/B.cs", "B", "Demo.B")
        };
        var edges = new[]
        {
            Edge(repo, "same-edge", "s-a", "s-b", "calls", "src/A.cs"),
            Edge(repo, "same-edge", "s-b", "s-a", "calls", "src/B.cs")
        };
        await ThrowsAsync<DuplicateRelationshipIdentityException>(() =>
            store.ReplaceAsync(Snapshot(repo, "bad", symbols, edges)));
        True((await store.StatusAsync(repo)).Generation is null);
    }

    private static async Task PurgePathAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "purge";
        await using var store = CreateStore(temp.Path);
        await store.ReplaceAsync(Snapshot(repo, "g1",
        [
            Symbol(repo, "s-a", "src/generated/A.g.cs", "Generated", "Demo.Generated"),
            Symbol(repo, "s-b", "src/B.cs", "HandWritten", "Demo.HandWritten")
        ], [Edge(repo, "e1", "s-b", "s-a", "calls", "src/B.cs")]));
        Equal(1, await store.PurgePathAsync(repo, "/generated/"));
        Equal(0, (await store.SearchAsync(repo, "Generated", 10)).Items.Count);
        var detail = await store.SymbolAsync(repo, "s-b", 20);
        NotNull(detail);
        Equal(0, detail!.Outgoing.Count);
        Equal(1, (await store.StatusAsync(repo)).Symbols);
    }


    private static async Task ModuleScopedReplacementAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "module-scope";
        await using var store = CreateStore(temp.Path);
        var csharp = Symbol(repo, "csharp-a", "src/A.cs", "CSharpOld", "Demo.CSharpOld");
        var typescript = Symbol(repo, "ts-a", "web/A.ts", "TypeScriptStable", "Demo.TypeScriptStable") with
        {
            Language = "typescript",
            ModuleId = "typescript-semantic"
        };
        await store.ReplaceAsync(Snapshot(repo, "g1", [csharp, typescript], []));

        var replacement = Symbol(repo, "csharp-b", "src/B.cs", "CSharpNew", "Demo.CSharpNew");
        var unrelatedIncoming = Symbol(repo, "ts-should-be-ignored", "web/Incoming.ts", "TypeScriptIncoming", "Demo.TypeScriptIncoming") with
        {
            Language = "typescript",
            ModuleId = "typescript-semantic"
        };
        await store.ReplaceAsync(Snapshot(repo, "g2", [replacement, unrelatedIncoming], []), ["csharp-roslyn"]);

        Equal(0, (await store.SearchAsync(repo, "CSharpOld", 10)).Items.Count);
        Equal("csharp-b", (await store.SearchAsync(repo, "CSharpNew", 10)).Items.Single().Symbol.Id);
        Equal("ts-a", (await store.SearchAsync(repo, "TypeScriptStable", 10)).Items.Single().Symbol.Id);
        Equal(0, (await store.SearchAsync(repo, "TypeScriptIncoming", 10)).Items.Count);
        Equal(2, (await store.StatusAsync(repo)).Symbols);
    }

    private static async Task MetadataOnlyStatusAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "metadata-only";
        await using (var writer = CreateStore(temp.Path))
            await writer.ReplaceAsync(Snapshot(repo, "g1", [Symbol(repo, "s-a", "src/A.cs", "A", "Demo.A")], []));

        await using var reader = CreateStore(temp.Path);
        Equal(0, reader.GetRuntimeStats().ResidentRepositories);
        var status = await reader.StatusAsync(repo);
        Equal(1, status.Symbols);
        Equal(0, reader.GetRuntimeStats().ResidentRepositories,
            "Metadata-only status must not materialize or retain the query pack.");
        _ = await reader.SearchAsync(repo, "A", 5);
        var stats = reader.GetRuntimeStats();
        Equal(1, stats.ResidentRepositories);
        Equal(_memoryMode == NativeMemoryMode.MemoryMapped ? 1 : 0, stats.MemoryMappedRepositories);
        Equal(_memoryMode == NativeMemoryMode.CompactManaged ? 1 : 0, stats.CompactManagedRepositories);
    }

    private static async Task ResidentRepositoryLruAsync()
    {
        using var temp = new TemporaryDirectory();
        await using var store = CreateStore(temp.Path, maxResidentRepositories: 1);
        foreach (var repo in new[] { "lru-a", "lru-b" })
        {
            await store.ReplaceAsync(Snapshot(repo, "g1", [Symbol(repo, $"s-{repo}", "src/A.cs", repo, $"Demo.{repo}")], []));
            _ = await store.SearchAsync(repo, repo, 5);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (store.GetRuntimeStats().ResidentRepositories > 1 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        True(store.GetRuntimeStats().ResidentRepositories <= 1, "LRU retained more heavy repositories than configured.");
        Equal(1, (await store.StatusAsync("lru-a")).Symbols);
        Equal(1, (await store.StatusAsync("lru-b")).Symbols);
    }

    private static async Task CorruptSuperblockFallbackAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "superblock-fallback";
        await CreateTwoGenerationsAsync(temp.Path, repo);
        var repoPath = StorePath(temp.Path, repo);
        CorruptByte(Path.Combine(repoPath, "superblock.a"), 20); // sequence 2 is written to slot A.

        await using var reopened = CreateStore(temp.Path, cleanup: false);
        var status = await reopened.StatusAsync(repo);
        Equal("g1", status.Generation);
        Equal(1, (await reopened.SearchAsync(repo, "OldName", 10)).Items.Count);
        True(status.Diagnostics.Any(value => value.Contains("invalid superblock.a", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task CorruptManifestFallbackAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "manifest-fallback";
        await CreateTwoGenerationsAsync(temp.Path, repo);
        var repoPath = StorePath(temp.Path, repo);
        var latest = Directory.EnumerateFiles(Path.Combine(repoPath, "manifests"), "manifest-*.mrm")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).First();
        CorruptByte(latest, 24);

        await using var reopened = CreateStore(temp.Path, cleanup: false);
        var status = await reopened.StatusAsync(repo);
        Equal("g1", status.Generation);
        Equal("OldName", (await reopened.SearchAsync(repo, "OldName", 10)).Items.Single().Symbol.Name);
        True(status.Diagnostics.Any(value => value.Contains("Recovered repository", StringComparison.Ordinal) || value.Contains("Rejected sequence", StringComparison.Ordinal)));
    }

    private static async Task CorruptSegmentFallbackAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "segment-fallback";
        await CreateTwoGenerationsAsync(temp.Path, repo);
        var repoPath = StorePath(temp.Path, repo);
        var latest = Directory.EnumerateFiles(Path.Combine(repoPath, "segments"), "seg-*.mrs")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).First();
        CorruptByte(latest, 32);

        await using var reopened = CreateStore(temp.Path, cleanup: false);
        Equal("g1", (await reopened.StatusAsync(repo)).Generation);
        Equal(1, (await reopened.SearchAsync(repo, "OldName", 10)).Items.Count);
    }

    private static async Task CorruptSnapshotFallbackAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "snapshot-fallback";
        await CreateTwoGenerationsAsync(temp.Path, repo);
        var repoPath = StorePath(temp.Path, repo);
        var latest = Directory.EnumerateFiles(Path.Combine(repoPath, "snapshots"), "snapshot-*.mrp")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).First();
        CorruptByte(latest, PackLayout.HeaderSize + 32);

        await using var reopened = CreateStore(temp.Path, cleanup: false, verifyOnOpen: true);
        var status = await reopened.StatusAsync(repo);
        // Status validates the pack header only; opening the heavy snapshot performs full checksums.
        Equal("g2", status.Generation);
        Equal(1, (await reopened.SearchAsync(repo, "OldName", 10)).Items.Count);
        Equal("g1", (await reopened.StatusAsync(repo)).Generation);
    }

    private static async Task HardForceMatrixAsync()
    {
        var points = new[]
        {
            StoreFaultPoint.AfterSegmentBytesWritten,
            StoreFaultPoint.AfterSegmentDurable,
            StoreFaultPoint.AfterSegmentPublished,
            StoreFaultPoint.AfterSnapshotBytesWritten,
            StoreFaultPoint.AfterSnapshotDurable,
            StoreFaultPoint.AfterSnapshotPublished,
            StoreFaultPoint.AfterManifestBytesWritten,
            StoreFaultPoint.AfterManifestDurable,
            StoreFaultPoint.AfterManifestPublished,
            StoreFaultPoint.BeforeSuperblockPublish,
            StoreFaultPoint.AfterSuperblockBytesWritten,
            StoreFaultPoint.AfterSuperblockDurable,
            StoreFaultPoint.AfterSuperblockPublished,
            StoreFaultPoint.BeforeInMemorySnapshotPublish,
            StoreFaultPoint.AfterInMemorySnapshotPublish
        };
        foreach (var point in points)
        {
            using var temp = new TemporaryDirectory();
            var repo = "crash-" + point;
            await using (var baseline = CreateStore(temp.Path, cleanup: false))
                await baseline.ReplaceAsync(Snapshot(repo, "g1", [Symbol(repo, "s-a", "src/A.cs", "OldName", "Demo.OldName")], []));

            var exitCode = await SpawnCrashWorkerAsync(temp.Path, repo, point);
            True(exitCode != 0, $"Crash worker unexpectedly exited successfully at {point}.");

            await using var reopened = CreateStore(temp.Path, cleanup: false);
            var status = await reopened.StatusAsync(repo);
            var shouldBeNew = point is StoreFaultPoint.AfterSuperblockPublished
                or StoreFaultPoint.BeforeInMemorySnapshotPublish
                or StoreFaultPoint.AfterInMemorySnapshotPublish;
            Equal(shouldBeNew ? "g2" : "g1", status.Generation, $"Unexpected visible generation after {point}.");
            var visibleName = shouldBeNew ? "NewName" : "OldName";
            Equal(1, (await reopened.SearchAsync(repo, visibleName, 10)).Items.Count);
            Equal(0, (await reopened.SearchAsync(repo, shouldBeNew ? "OldName" : "NewName", 10)).Items.Count);

            // A pre-commit crash may leave renamed but unreferenced files. A retry must not collide
            // with those orphans and must publish a clean next generation.
            await reopened.ReplaceFilesAsync(repo, "csharp-roslyn", ["src/A.cs"],
                [Symbol(repo, "s-a", "src/A.cs", "RetryName", "Demo.RetryName")],
                [], "g3", DateTimeOffset.UtcNow);
            Equal("g3", (await reopened.StatusAsync(repo)).Generation);
        }
    }

    private static async Task PostCommitExceptionReloadAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "post-commit-reload";
        await using (var baseline = CreateStore(temp.Path, cleanup: false))
            await baseline.ReplaceAsync(Snapshot(repo, "g1", [Symbol(repo, "s-a", "src/A.cs", "OldName", "Demo.OldName")], []));

        var injector = new DelegateStoreFaultInjector((point, _) =>
        {
            if (point == StoreFaultPoint.AfterSuperblockPublished)
                throw new InjectedStoreFaultException();
        });
        await using var store = CreateStore(temp.Path, injector, cleanup: false);
        await ThrowsAsync<InjectedStoreFaultException>(() => store.ReplaceFilesAsync(repo, "csharp-roslyn", ["src/A.cs"],
            [Symbol(repo, "s-a", "src/A.cs", "NewName", "Demo.NewName")], [], "g2", DateTimeOffset.UtcNow));
        Equal("g2", (await store.StatusAsync(repo)).Generation);
        Equal(1, (await store.SearchAsync(repo, "NewName", 10)).Items.Count);
    }

    private static async Task ConcurrentReadersAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "concurrency";
        await using var store = CreateStore(temp.Path);
        await store.ReplaceAsync(Snapshot(repo, "g0",
            Enumerable.Range(0, 50).Select(index => Symbol(repo, $"s-{index}", $"src/F{index}.cs", $"Symbol{index}", $"Demo.Symbol{index}")).ToArray(), []));
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                for (var iteration = 0; iteration < 120; iteration++)
                {
                    var status = await store.StatusAsync(repo);
                    True(status.Symbols == 50, "Reader observed a partial symbol set.");
                    var result = await store.SearchAsync(repo, "Symbol1", 20);
                    True(result.Items.Count > 0, "Reader observed a missing stable symbol.");
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToArray();

        for (var generation = 1; generation <= 20; generation++)
        {
            await store.ReplaceFilesAsync(repo, "csharp-roslyn", ["src/F0.cs"],
                [Symbol(repo, "s-0", "src/F0.cs", $"Changing{generation}", $"Demo.Changing{generation}")],
                [], $"g{generation}", DateTimeOffset.UtcNow);
        }
        await Task.WhenAll(readers);
        if (failures.TryDequeue(out var failure)) throw failure;
        Equal("g20", (await store.StatusAsync(repo)).Generation);
    }


    private static Task Crc32CVectorAsync()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("123456789");
        Equal(0xE3069283u, Crc32C.Compute(bytes));
        return Task.CompletedTask;
    }

    private static Task TypedIdentityAsync()
    {
        var first = SemanticIdentity.CreateDeclarationId(
            "csharp", "csharp-roslyn", "App", "Method", "Demo.Outer.Inner.Run`1(System.String:in)", "src/A.cs");
        var same = SemanticIdentity.CreateDeclarationId(
            "csharp", "csharp-roslyn", "App", "Method", "Demo.Outer.Inner.Run`1(System.String:in)", "src\\A.cs");
        var different = SemanticIdentity.CreateDeclarationId(
            "csharp", "csharp-roslyn", "App", "Method", "Demo.Other.Inner.Run`1(System.String:in)", "src/A.cs");
        Equal(first, same);
        True(first != different);

        var logical = new LogicalSymbolIdentity(
            "csharp", "App", ["Demo"], [new TypeIdentity("Outer", 0)], "Method", "Run", 1,
            [new TypedParameterIdentity("System.String", "in")], "System.Boolean");
        var declarationA = new DeclarationIdentity(logical, "src/A.cs", 100);
        var declarationB = new DeclarationIdentity(logical, "src/B.cs", 100);
        Equal(logical.ToCompactId(), (logical with { NamespacePath = ["Demo"] }).ToCompactId());
        Equal(logical.ToCompactId(), (logical with
        {
            Parameters = [new TypedParameterIdentity("System.String", "in", "renamed")]
        }).ToCompactId(), "Parameter names must not change logical overload identity.");
        True(declarationA.ToCompactId() != declarationB.ToCompactId(), "Physical declarations must retain file identity.");
        True(logical.ToCompactId() != (logical with { MetadataName = "RunElsewhere" }).ToCompactId());
        return Task.CompletedTask;
    }

    private static async Task ProjectionBudgetAsync()
    {
        using var temp = new TemporaryDirectory();
        const string repo = "projection";
        await using var store = CreateStore(temp.Path);
        await store.ReplaceAsync(Snapshot(repo, "g1",
            Enumerable.Range(0, 12).Select(index => Symbol(repo, $"s-{index}", $"src/F{index}.cs", $"RepositoryThing{index}", $"Demo.RepositoryThing{index}")).ToArray(), []));
        var projection = await store.ProjectSymbolsAsync(new SymbolProjectionRequest(
            repo,
            "RepositoryThing",
            NativeProjectionKind.Orientation,
            new ProjectionBudget(3, 10_000)));
        Equal(3, projection.Returned);
        True(projection.HasMore);
        True(projection.EstimatedTokens > 0);
    }

    private static async Task CreateTwoGenerationsAsync(string root, string repo)
    {
        await using var store = CreateStore(root, cleanup: false);
        await store.ReplaceAsync(Snapshot(repo, "g1", [Symbol(repo, "s-a", "src/A.cs", "OldName", "Demo.OldName")], []));
        await store.ReplaceFilesAsync(repo, "csharp-roslyn", ["src/A.cs"],
            [Symbol(repo, "s-a", "src/A.cs", "NewName", "Demo.NewName")], [], "g2", DateTimeOffset.UtcNow);
    }

    private static async Task<int> RunCrashWorkerAsync(string[] args)
    {
        if (args.Length != 5) return 64;
        var root = args[1];
        var repo = args[2];
        var point = Enum.Parse<StoreFaultPoint>(args[3], ignoreCase: false);
        _memoryMode = Enum.Parse<NativeMemoryMode>(args[4], ignoreCase: false);
        var injector = new DelegateStoreFaultInjector((current, _) =>
        {
            if (current == point) Environment.FailFast($"Injected hard-force crash at {point}.");
        });
        await using var store = CreateStore(root, injector, cleanup: false);
        await store.ReplaceFilesAsync(repo, "csharp-roslyn", ["src/A.cs"],
            [Symbol(repo, "s-a", "src/A.cs", "NewName", "Demo.NewName")], [], "g2", DateTimeOffset.UtcNow);
        return 0;
    }

    private static async Task<int> SpawnCrashWorkerAsync(string root, string repo, StoreFaultPoint point)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable.");
        var info = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--crash-worker");
        info.ArgumentList.Add(root);
        info.ArgumentList.Add(repo);
        info.ArgumentList.Add(point.ToString());
        info.ArgumentList.Add(_memoryMode.ToString());
        info.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start crash worker.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        _ = await standardOutput;
        _ = await standardError;
        return process.ExitCode;
    }

    private static NativeRepositoryStore CreateStore(
        string root,
        IStoreFaultInjector? injector = null,
        bool cleanup = true,
        bool verifyOnOpen = true,
        int maxResidentRepositories = 2) => new(new NativeStoreOptions
    {
        RootDirectory = root,
        MemoryMode = _memoryMode,
        FlushToDisk = true,
        WriteThrough = true,
        CleanupObsoleteFiles = cleanup,
        VerifySnapshotPackChecksumsOnOpen = verifyOnOpen,
        MaxResidentRepositories = maxResidentRepositories,
        MaxResidentManagedBytes = 128L * 1024 * 1024,
        DecodedStringCacheBytes = 2L * 1024 * 1024,
        MaterializedRecordCacheEntries = 256,
        FaultInjector = injector
    });

    private static string StorePath(string root, string repo)
    {
        var slug = new string(repo.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray());
        if (slug.Length > 48) slug = slug[..48];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repo))).ToLowerInvariant()[..10];
        return Path.Combine(Path.GetFullPath(root), $"{slug}__{hash}");
    }

    private static AnalysisSnapshot Snapshot(
        string repo,
        string generation,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<RelationshipRecord> relationships) =>
        new(repo, generation, symbols, relationships, [], DateTimeOffset.UtcNow);

    private static SymbolRecord Symbol(
        string repo,
        string id,
        string file,
        string name,
        string qualified,
        string kind = "Method") =>
        new(id, repo, "Tests", file, name, qualified, kind, 1, 1, 1, 10, $"void {name}()", "csharp", "csharp-roslyn");

    private static RelationshipRecord Edge(
        string repo,
        string id,
        string source,
        string target,
        string kind,
        string file) =>
        new(id, repo, source, target, kind, file, 1, 1, "semantic", "csharp", "csharp-roslyn");

    private static void CorruptByte(string path, int preferredOffset)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = Math.Clamp(preferredOffset, 0, bytes.Length - 5);
        bytes[offset] ^= 0x5a;
        File.WriteAllBytes(path, bytes);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new TestFailureException($"Expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailureException(message ?? $"Expected '{expected}', got '{actual}'.");
    }

    private static void True(bool condition, string? message = null)
    {
        if (!condition) throw new TestFailureException(message ?? "Expected condition to be true.");
    }

    private static void NotNull(object? value)
    {
        if (value is null) throw new TestFailureException("Expected a non-null value.");
    }

    private sealed class InjectedStoreFaultException : Exception { }

    private sealed class TestFailureException(string message) : Exception(message);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maprepo-native-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
