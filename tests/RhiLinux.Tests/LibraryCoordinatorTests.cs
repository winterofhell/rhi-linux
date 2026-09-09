using RhiLinux.Core;
using RhiLinux.Sources;

namespace RhiLinux.Tests;

public sealed class LibraryCoordinatorTests
{
    [Fact]
    public async Task WatchEventRefreshesOnlyOwningProviderAndPublishesImmutableSnapshot()
    {
        using var temp = new TestDirectory();
        var heroicPath = temp.File("heroic/legendaryConfig/legendary/installed.json", "{\"version\":1}");
        var legendaryPath = temp.File("legendary/installed.json", "{\"version\":1}");
        var heroic = new CoordinatorProvider("heroic", temp.Combine("heroic"), heroicPath, GameLauncher.Heroic);
        var legendary = new CoordinatorProvider("legendary", temp.Combine("legendary"), legendaryPath, GameLauncher.Legendary);
        var providers = new IGameSourceProvider[] { heroic, legendary };
        var service = new MultiSourceLibraryService(providers);
        await using var coordinator = new LibraryCoordinator(
            service,
            providers,
            SourceRootDiscovery.CreateContext(temp.Path,
                enabledProviders: new HashSet<string>(["heroic", "legendary"], StringComparer.Ordinal)),
            temp.Combine("library.db"),
            coalesceDelay: TimeSpan.FromMilliseconds(10),
            watcherDebounce: TimeSpan.FromMilliseconds(30));

        var first = await coordinator.RefreshAsync(LibraryRefreshScope.Full("fixture full refresh"));
        await File.WriteAllTextAsync(heroicPath, "{\"version\":2}");
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (coordinator.Current.Generation == first.Snapshot.Generation && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var current = coordinator.Current;
        Assert.True(current.Generation > first.Snapshot.Generation);
        Assert.Equal(["heroic"], current.Metrics.ProvidersExecuted);
        Assert.Equal(2, heroic.ScanCount);
        Assert.Equal(1, legendary.ScanCount);
        Assert.Equal(2, current.Games.Count);
        Assert.NotSame(first.Snapshot.Games, current.Games);
    }

    [Fact]
    public async Task ConcurrentRequestsCoalesceProviderScopesIntoOneGeneration()
    {
        using var temp = new TestDirectory();
        var firstPath = temp.File("heroic/legendaryConfig/legendary/installed.json", "{}");
        var secondPath = temp.File("legendary/installed.json", "{}");
        var firstProvider = new CoordinatorProvider("heroic", temp.Combine("heroic"), firstPath, GameLauncher.Heroic);
        var secondProvider = new CoordinatorProvider("legendary", temp.Combine("legendary"), secondPath, GameLauncher.Legendary);
        var providers = new IGameSourceProvider[] { firstProvider, secondProvider };
        await using var coordinator = new LibraryCoordinator(
            new MultiSourceLibraryService(providers),
            providers,
            SourceRootDiscovery.CreateContext(temp.Path,
                enabledProviders: new HashSet<string>(["heroic", "legendary"], StringComparer.Ordinal)),
            temp.Combine("library.db"),
            coalesceDelay: TimeSpan.FromMilliseconds(50));

        var heroicRefresh = coordinator.RefreshAsync(new(
            new HashSet<string>(["heroic"], StringComparer.Ordinal), Reason: "heroic"));
        var legendaryRefresh = coordinator.RefreshAsync(new(
            new HashSet<string>(["legendary"], StringComparer.Ordinal), Reason: "legendary"));
        var results = await Task.WhenAll(heroicRefresh, legendaryRefresh);

        Assert.Equal(results[0].Snapshot.Generation, results[1].Snapshot.Generation);
        Assert.Equal(["heroic", "legendary"], results[0].Metrics.ProvidersExecuted.Order().ToArray());
        Assert.Equal(1, firstProvider.ScanCount);
        Assert.Equal(1, secondProvider.ScanCount);
    }

    [Fact]
    public async Task CancelledObsoleteRefreshDoesNotPublishAStaleSnapshot()
    {
        using var temp = new TestDirectory();
        var provider = new BlockingCoordinatorProvider(temp);
        await using var coordinator = new LibraryCoordinator(
            new MultiSourceLibraryService([provider]),
            [provider],
            SourceRootDiscovery.CreateContext(temp.Path,
                enabledProviders: new HashSet<string>([provider.Id], StringComparer.Ordinal)),
            temp.Combine("library.db"),
            coalesceDelay: TimeSpan.FromMilliseconds(10));
        var publications = 0;
        coordinator.Changed += (_, _) => publications++;
        using var cancellation = new CancellationTokenSource();

        var obsolete = coordinator.RefreshAsync(LibraryRefreshScope.Full("obsolete"), cancellation.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
        Assert.Equal(0, coordinator.Current.Generation);
        Assert.Equal(0, publications);

        var current = await coordinator.RefreshAsync(LibraryRefreshScope.Full("current"));
        Assert.True(current.Snapshot.Generation > 0);
        Assert.Equal(1, publications);
    }

    [Fact]
    public async Task ProviderToggleChangesSnapshotWithoutDiscardingCanonicalRecords()
    {
        using var temp = new TestDirectory();
        var heroicPath = temp.File("heroic/installed.json", "{}");
        var legendaryPath = temp.File("legendary/installed.json", "{}");
        var heroic = new CoordinatorProvider("heroic", temp.Combine("heroic"), heroicPath, GameLauncher.Heroic);
        var legendary = new CoordinatorProvider("legendary", temp.Combine("legendary"), legendaryPath, GameLauncher.Legendary);
        var providers = new IGameSourceProvider[] { heroic, legendary };
        var context = SourceRootDiscovery.CreateContext(temp.Path,
            enabledProviders: new HashSet<string>(["heroic", "legendary"], StringComparer.Ordinal));
        await using var coordinator = new LibraryCoordinator(
            new MultiSourceLibraryService(providers),
            providers,
            context,
            temp.Combine("library.db"),
            coalesceDelay: TimeSpan.FromMilliseconds(10));
        await coordinator.RefreshAsync(LibraryRefreshScope.Full("all enabled"));

        var heroicOnly = new HashSet<string>(["heroic"], StringComparer.Ordinal);
        coordinator.Configure(context with { EnabledProviders = heroicOnly }, heroicOnly);
        var disabled = await coordinator.RefreshAsync(LibraryRefreshScope.Full("legendary disabled"));

        Assert.Equal("heroic", Assert.Single(disabled.Snapshot.Games).Sources[0].ProviderId);
        Assert.False(Assert.Single(disabled.Snapshot.Providers, item => item.ProviderId == "legendary").Enabled);

        var all = new HashSet<string>(["heroic", "legendary"], StringComparer.Ordinal);
        coordinator.Configure(context with { EnabledProviders = all }, all);
        var restored = await coordinator.RefreshAsync(LibraryRefreshScope.Full("legendary enabled"));
        Assert.Equal(2, restored.Snapshot.Games.Count);
    }

    private sealed class CoordinatorProvider(
        string id,
        string rootPath,
        string metadataPath,
        GameLauncher launcher) : IGameSourceProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public int ScanCount { get; private set; }

        public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
            GameSourceDiscoveryContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameSourceRoot>>
            ([new(id, rootPath, rootPath, rootPath, SourceRootKind.Explicit, true, true, false, null)]);

        public Task<GameSourceScanResult> ScanAsync(
            GameSourceRoot root,
            GameSourceScanContext context,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            var fingerprint = SourceRootDiscovery.SourceFingerprint(metadataPath);
            var key = $"{id}:{root.CanonicalPath}";
            if (!context.IsTargeted && context.PreviousFingerprints.GetValueOrDefault(key) == fingerprint)
                return Task.FromResult(new GameSourceScanResult(
                    id, root, [], [], [metadataPath], [], [], fingerprint, TimeSpan.Zero, true, false));
            var record = new SourceGameRecord(
                id, GameStore.Epic, launcher, id, id, Path.Combine(rootPath, "game"), null, null, null,
                GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine, metadataPath, null,
                File.GetLastWriteTimeUtc(metadataPath), new Dictionary<string, string>(), []);
            return Task.FromResult(new GameSourceScanResult(
                id, root, [record], [], [metadataPath], [], [], fingerprint, TimeSpan.Zero, false, true));
        }
    }

    private sealed class BlockingCoordinatorProvider : IGameSourceProvider
    {
        private readonly string rootPath;
        private readonly string metadataPath;
        private int calls;

        public BlockingCoordinatorProvider(TestDirectory temp)
        {
            rootPath = temp.Directory("blocking");
            metadataPath = temp.File("blocking/game.json", "{}");
        }

        public string Id => "heroic";
        public string DisplayName => "Heroic";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
            GameSourceDiscoveryContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameSourceRoot>>
            ([new(Id, rootPath, rootPath, rootPath, SourceRootKind.Explicit, true, true, false, null)]);

        public async Task<GameSourceScanResult> ScanAsync(
            GameSourceRoot root,
            GameSourceScanContext context,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var record = new SourceGameRecord(
                Id, GameStore.Epic, GameLauncher.Heroic, "fixture", "Fixture", rootPath,
                null, null, null, GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine,
                metadataPath, null, File.GetLastWriteTimeUtc(metadataPath),
                new Dictionary<string, string>(), []);
            return new(
                Id, root, [record], [], [metadataPath], [], [], "fixture", TimeSpan.Zero, false, true);
        }
    }
}
