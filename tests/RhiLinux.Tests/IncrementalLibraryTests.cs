using RhiLinux.Core;
using RhiLinux.Sources;
using RhiLinux.Steam;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit.Abstractions;

namespace RhiLinux.Tests;

public sealed class IncrementalLibraryTests
{
    private readonly ITestOutputHelper output;

    public IncrementalLibraryTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task UnchangedThousandGameLibraryDoesNoParseReconcileOrAnalysisWork()
    {
        using var temp = new TestDirectory();
        var provider = new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 1000);
        var service = new MultiSourceLibraryService([provider], new CountingAnalyzer());
        var request = Request(temp, new HashSet<string>(["heroic"], StringComparer.Ordinal), analyze: false);

        var coldMeasurement = System.Diagnostics.Stopwatch.StartNew();
        var first = await service.ScanAsync(request);
        coldMeasurement.Stop();
        var cachedStartup = await MeasureAsync(7, async () =>
        {
            await using var index = new SqliteLibraryIndex(temp.Combine("library.db"));
            await index.OpenAsync();
            var cached = await index.LoadCachedGamesAsync();
            Assert.Equal(1000, cached.Count);
        });
        MultiSourceScanResult? second = null;
        var unchanged = await MeasureAsync(7, async () => second = await service.ScanAsync(request));
        output.WriteLine($"1000-game cold discovery: {coldMeasurement.Elapsed.TotalMilliseconds:0.###} ms");
        output.WriteLine($"1000-game cached startup: {cachedStartup}");
        output.WriteLine($"1000-game unchanged refresh: {unchanged}");

        Assert.Equal(1000, first.Games.Count);
        Assert.NotNull(second);
        Assert.Equal(0, second.Metrics!.SourceDocumentsParsed);
        Assert.Equal(0, second.Metrics.InstallationsReconciled);
        Assert.Equal(0, second.Metrics.InstallationsAnalyzed);
        Assert.Equal(0, second.Metrics.SourceRecordsChanged);
        Assert.True(second.UsedCache);
    }

    private static async Task<string> MeasureAsync(int repetitions, Func<Task> operation)
    {
        var values = new double[repetitions];
        for (var index = 0; index < repetitions; index++)
        {
            var measurement = System.Diagnostics.Stopwatch.StartNew();
            await operation();
            measurement.Stop();
            values[index] = measurement.Elapsed.TotalMilliseconds;
        }
        Array.Sort(values);
        return $"median {values[values.Length / 2]:0.###} ms · min {values[0]:0.###} ms · max {values[^1]:0.###} ms · n={repetitions}";
    }

    [Theory]
    [InlineData("heroic")]
    [InlineData("lutris")]
    [InlineData("steam")]
    public async Task SingleDocumentChangeExecutesOnlyOwningProvider(string changedProvider)
    {
        using var temp = new TestDirectory();
        var providers = new[]
        {
            new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 1),
            new GeneratedProvider("lutris", GameLauncher.Lutris, GameStore.Gog, temp, 1),
            new GeneratedProvider("steam", GameLauncher.Steam, GameStore.Steam, temp, 1)
        };
        var service = new MultiSourceLibraryService(providers, new CountingAnalyzer());
        var enabled = providers.Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal);
        await service.ScanAsync(Request(temp, enabled, analyze: false));
        var changed = providers.Single(provider => provider.Id == changedProvider);
        var document = changed.ChangeFirstRecord();

        var measurement = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.ScanAsync(Request(
            temp,
            enabled,
            analyze: false,
            new(
                new HashSet<string>([changedProvider], StringComparer.Ordinal),
                new HashSet<string>([changed.Root.CanonicalPath], StringComparer.Ordinal),
                new HashSet<string>([document], StringComparer.Ordinal),
                false,
                $"{changedProvider} fixture changed")));
        measurement.Stop();
        output.WriteLine($"1 changed metadata document ({changedProvider}): {measurement.Elapsed.TotalMilliseconds:0.###} ms");

        Assert.Equal([changedProvider], result.Metrics!.ProvidersExecuted);
        Assert.Equal(1, result.Metrics.SourceDocumentsParsed);
        Assert.Equal(1, result.Metrics.SourceRecordsChanged);
        Assert.Equal(1, result.Metrics.InstallationsReconciled);
        Assert.Equal(0, result.Metrics.InstallationsAnalyzed);
        Assert.All(providers.Where(provider => provider != changed), provider => Assert.Equal(1, provider.ScanCount));
    }

    [Fact]
    public async Task PersistedAnalysisSurvivesServiceRestart()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("installed", "heroic-0");
        var provider = new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 1, root);
        var firstAnalyzer = new CountingAnalyzer();
        var request = Request(temp, new HashSet<string>(["heroic"], StringComparer.Ordinal), analyze: true);

        var first = await new MultiSourceLibraryService([provider], firstAnalyzer).ScanAsync(request);
        var secondAnalyzer = new CountingAnalyzer();
        var restartedProvider = new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 1, root);
        var second = await new MultiSourceLibraryService([restartedProvider], secondAnalyzer).ScanAsync(request);

        Assert.Equal(1, first.Metrics!.InstallationsAnalyzed);
        Assert.Equal(1, firstAnalyzer.Calls);
        Assert.Equal(0, second.Metrics!.InstallationsAnalyzed);
        Assert.Equal(0, secondAnalyzer.Calls);
        Assert.True(second.UsedCache);
    }

    [Fact]
    public async Task RemovedAndMovedRecordsReconcileOnlyAffectedInstalls()
    {
        using var temp = new TestDirectory();
        var provider = new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 4);
        var service = new MultiSourceLibraryService([provider], new CountingAnalyzer());
        var enabled = new HashSet<string>(["heroic"], StringComparer.Ordinal);
        await service.ScanAsync(Request(temp, enabled, analyze: false));

        var removedDocument = provider.RemoveFirstRecord();
        var removed = await service.ScanAsync(Request(temp, enabled, analyze: false,
            Target(provider, removedDocument, "removed")));
        var movedDocument = provider.MoveFirstRecord(temp.Combine("moved", "game"));
        var moved = await service.ScanAsync(Request(temp, enabled, analyze: false,
            Target(provider, movedDocument, "moved")));

        Assert.Equal(3, removed.Games.Count);
        Assert.Equal(1, removed.Metrics!.SourceRecordsRemoved);
        Assert.Equal(1, removed.Metrics.InstallationsReconciled);
        Assert.Equal(1, moved.Metrics!.SourceRecordsChanged);
        Assert.Equal(1, moved.Metrics.InstallationsReconciled);
        Assert.Contains(moved.Games, game => game.CanonicalInstallRoot == temp.Combine("moved", "game"));
    }

    [Fact]
    public async Task ProviderFailureAndUnavailableRootRetainLastSuccessfulRecords()
    {
        using var temp = new TestDirectory();
        var provider = new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 2);
        var service = new MultiSourceLibraryService([provider], new CountingAnalyzer());
        var enabled = new HashSet<string>(["heroic"], StringComparer.Ordinal);
        await service.ScanAsync(Request(temp, enabled, analyze: false));

        provider.ThrowOnScan = true;
        var failed = await service.ScanAsync(Request(temp, enabled, analyze: false,
            LibraryRefreshScope.Full("provider failure", force: true)));
        provider.ThrowOnScan = false;
        provider.RootAvailable = false;
        var unavailable = await service.ScanAsync(Request(temp, enabled, analyze: false,
            LibraryRefreshScope.Full("mount unavailable", force: true)));

        Assert.Equal(2, failed.Games.Count);
        Assert.Contains(failed.Diagnostics, item => item.Message.Contains("retained", StringComparison.Ordinal));
        Assert.Equal(2, unavailable.Games.Count);
        Assert.Contains(unavailable.ProviderResults, item => item.FromCache && !item.Root.Exists);
    }

    [Fact]
    public async Task VersionChangesInvalidateOnlyRequiredPipelineStage()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("installed", "heroic-0");
        var provider = new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 1, root);
        var enabled = new HashSet<string>(["heroic"], StringComparer.Ordinal);
        var request = Request(temp, enabled, analyze: true);
        await new MultiSourceLibraryService([provider], new CountingAnalyzer()).ScanAsync(request);

        await using (var index = new SqliteLibraryIndex(temp.Combine("library.db")))
        {
            await index.OpenAsync();
            var cached = Assert.Single(await index.LoadAnalysisAsync()).Value;
            await using var connection = new SqliteConnection($"Data Source={temp.Combine("library.db")};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE game_analysis SET analyzer_version=0, payload_json=$payload WHERE install_id=$install; " +
                "UPDATE schema_info SET reconciliation_version=1 WHERE id=1;";
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(cached with { AnalyzerVersion = 0 }));
            command.Parameters.AddWithValue("$install", cached.InstallId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var analyzer = new CountingAnalyzer();
        var refreshedProvider = new GeneratedProvider("heroic", GameLauncher.Heroic, GameStore.Epic, temp, 1, root);
        var refreshed = await new MultiSourceLibraryService([refreshedProvider], analyzer).ScanAsync(request);

        Assert.Equal(1, refreshed.Metrics!.InstallationsReconciled);
        Assert.Equal(1, refreshed.Metrics.InstallationsAnalyzed);
        Assert.Equal(1, analyzer.Calls);
    }

    private static LibraryRefreshScope Target(GeneratedProvider provider, string document, string reason) => new(
        new HashSet<string>([provider.Id], StringComparer.Ordinal),
        new HashSet<string>([provider.Root.CanonicalPath], StringComparer.Ordinal),
        new HashSet<string>([document], StringComparer.Ordinal),
        false,
        reason);

    private static MultiSourceScanRequest Request(
        TestDirectory temp,
        IReadOnlySet<string> enabled,
        bool analyze,
        LibraryRefreshScope? scope = null) => new(
        DiscoveryContext: SourceRootDiscovery.CreateContext(temp.Path, enabledProviders: enabled),
        EnabledProviders: enabled,
        AnalyzeChangedWindowsInstalls: analyze,
        LibraryDatabasePath: temp.Combine("library.db"),
        RefreshScope: scope);

    private sealed class GeneratedProvider : IGameSourceProvider
    {
        private readonly List<SourceGameRecord> records;
        private int version = 1;

        public GeneratedProvider(
            string id,
            GameLauncher launcher,
            GameStore store,
            TestDirectory temp,
            int count,
            string? fixedInstallRoot = null)
        {
            Id = id;
            DisplayName = id;
            var metadataRoot = temp.Directory("metadata", id);
            Root = new(id, metadataRoot, metadataRoot, metadataRoot, SourceRootKind.Explicit,
                true, true, false, null);
            records = Enumerable.Range(0, count).Select(index =>
            {
                var metadata = temp.File($"metadata/{id}/{index}.json", $"{{\"version\":{version}}}");
                var installRoot = fixedInstallRoot ?? temp.Combine("installed", $"{id}-{index}");
                return new SourceGameRecord(
                    id, store, launcher, index.ToString(), $"{id} game {index}", installRoot,
                    null, null, null, GameBinaryPlatform.Windows,
                    launcher == GameLauncher.Steam ? CompatibilityEnvironment.Proton : CompatibilityEnvironment.Wine,
                    metadata, null, DateTimeOffset.UnixEpoch,
                    new Dictionary<string, string>(), [], $"{id}-{index}-v{version}");
            }).ToList();
        }

        public string Id { get; }
        public string DisplayName { get; }
        public GameSourceRoot Root { get; }
        public int ScanCount { get; private set; }
        public bool ThrowOnScan { get; set; }
        public bool RootAvailable { get; set; } = true;

        public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
            GameSourceDiscoveryContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameSourceRoot>>([Root with
            {
                Exists = RootAvailable,
                Readable = RootAvailable,
                SkipReason = RootAvailable ? null : "Fixture mount unavailable."
            }]);

        public Task<GameSourceScanResult> ScanAsync(
            GameSourceRoot root,
            GameSourceScanContext context,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            if (ThrowOnScan) throw new IOException("Fixture provider failure.");
            var fingerprint = $"{Id}-v{version}";
            var key = $"{Id}:{root.CanonicalPath}";
            if (!context.ForceFullScan && !context.IsTargeted &&
                context.PreviousFingerprints.GetValueOrDefault(key) == fingerprint)
                return Task.FromResult(new GameSourceScanResult(
                    Id, root, [], [], records.Select(record => record.MetadataPath).ToArray(),
                    [], [], fingerprint, TimeSpan.Zero, true, false));

            var selected = context.IsTargeted
                ? records.Where(record => context.IncludesDocument(record.MetadataPath)).ToArray()
                : records.ToArray();
            return Task.FromResult(new GameSourceScanResult(
                Id, root, selected, [], selected.Select(record => record.MetadataPath).ToArray(),
                [], [], fingerprint, TimeSpan.Zero, false, true));
        }

        public string ChangeFirstRecord()
        {
            version++;
            var current = records[0];
            File.WriteAllText(current.MetadataPath, $"{{\"version\":{version}}}");
            records[0] = current with
            {
                Name = current.Name + " updated",
                MetadataModifiedAt = File.GetLastWriteTimeUtc(current.MetadataPath),
                SourceFingerprint = $"{Id}-0-v{version}"
            };
            return current.MetadataPath;
        }

        public string RemoveFirstRecord()
        {
            version++;
            var current = records[0];
            records.RemoveAt(0);
            File.Delete(current.MetadataPath);
            return current.MetadataPath;
        }

        public string MoveFirstRecord(string newRoot)
        {
            version++;
            var current = records[0];
            File.WriteAllText(current.MetadataPath, $"{{\"version\":{version}}}");
            records[0] = current with
            {
                InstallRoot = newRoot,
                MetadataModifiedAt = File.GetLastWriteTimeUtc(current.MetadataPath),
                SourceFingerprint = $"{Id}-moved-v{version}"
            };
            return current.MetadataPath;
        }
    }

    private sealed class CountingAnalyzer : IGameAnalyzer
    {
        private readonly GameAnalyzer analyzer = new();
        public int Calls { get; private set; }

        public Task<GameAnalysisResult> AnalyzeAsync(
            GameInstall install,
            GameAnalysisOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return analyzer.AnalyzeAsync(install, options, cancellationToken);
        }
    }
}
