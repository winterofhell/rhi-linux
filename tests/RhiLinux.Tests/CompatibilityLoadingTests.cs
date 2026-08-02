using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class CompatibilityLoadingTests
{
    [Fact]
    public async Task UnrealFallbackBecomesReadyWithoutWaitingForUpdates()
    {
        using var temp = new TestDirectory();
        var game = UnrealGame(temp, 9001, "Ascend Fixture");
        var reports = new DeferredStackStatusProvider();
        var viewModel = new MainViewModel(
            new StaticDiscovery([game]),
            new StaticStatusProvider(),
            new MemoryStore(),
            new PrefStore(new UiPreferences { CheckForUpdatesAutomatically = true }),
            stackStatusProvider: reports);

        var select = viewModel.SelectAsync(game);
        await WaitUntilAsync(() => !viewModel.IsSelectionLoading && viewModel.SupportTitle == "Ready to install");
        Assert.Equal("Ready to install", viewModel.SupportTitle);
        Assert.Contains("generic Unreal", viewModel.SupportMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Install", viewModel.PrimaryActionText);
        Assert.True(viewModel.CanInstallRecommendedStack);
        Assert.True(viewModel.IsCheckingUpdates);
        Assert.Contains("Local detect", viewModel.TimingDiagnostics, StringComparison.Ordinal);
        Assert.Equal(SelectionPhase.CheckingUpdates, viewModel.SelectionPhase);

        await reports.WaitForRequestAsync(game.AppId);
        reports.CompleteReady(game);
        await select;
        await WaitUntilAsync(() => !viewModel.IsCheckingUpdates);
        Assert.Equal("Install", viewModel.PrimaryActionText);
    }

    [Fact]
    public async Task UnsupportedUnrealLegacyDoesNotStayChecking()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Binaries/Win64/Game.exe");
        var game = new SteamGame(9002, "Legacy Fixture", temp.Path, temp.Path, root, temp.Combine("pfx"),
            executable, root, DetectionConfidence.High, "legacy", GameEngine.UnrealLegacy,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["legacy"])]);
        var viewModel = new MainViewModel(
            new StaticDiscovery([game]),
            new StaticStatusProvider(ComponentHealth.Unsupported),
            new MemoryStore(),
            new PrefStore(new UiPreferences { CheckForUpdatesAutomatically = false }));

        await viewModel.SelectAsync(game);
        Assert.NotEqual("Checking…", viewModel.PrimaryActionText);
        Assert.False(viewModel.IsSelectionLoading);
        Assert.False(viewModel.IsCheckingUpdates);
    }

    [Fact]
    public async Task StalledUpdateCheckDoesNotBlockInstallOrGameSwitch()
    {
        using var temp = new TestDirectory();
        var game = UnrealGame(temp, 9003, "Timeout Fixture");
        var other = UnrealGame(temp, 9004, "Other Fixture");
        var reports = new DeferredStackStatusProvider();
        var viewModel = new MainViewModel(
            new StaticDiscovery([game, other]),
            new StaticStatusProvider(),
            new MemoryStore(),
            new PrefStore(new UiPreferences { CheckForUpdatesAutomatically = true }),
            stackStatusProvider: reports);

        await viewModel.SelectAsync(game);
        await reports.WaitForRequestAsync(game.AppId);
        Assert.Equal("Install", viewModel.PrimaryActionText);
        Assert.True(viewModel.CanInstallRecommendedStack);
        Assert.True(viewModel.IsCheckingUpdates);

        await viewModel.SelectAsync(other);
        await WaitUntilAsync(() => viewModel.SelectedGame?.AppId == other.AppId && !viewModel.IsSelectionLoading);
        Assert.Equal("Install", viewModel.PrimaryActionText);
        Assert.True(viewModel.CanInstallRecommendedStack);
        Assert.Equal("Ready to install", viewModel.SupportTitle);
        reports.CompleteReady(other);
        await WaitUntilAsync(() => !viewModel.IsCheckingUpdates || viewModel.SelectedGame?.AppId != other.AppId);
        Assert.Equal(9004u, viewModel.SelectedGame?.AppId);
        Assert.NotEqual(UpdateCheckState.Checking, viewModel.UpdateState);
    }

    [Fact]
    public async Task RapidGameSwitchCancelsPreviousCompatibilityWork()
    {
        using var temp = new TestDirectory();
        var first = UnrealGame(temp, 9101, "First Unreal");
        var second = UnrealGame(temp, 9102, "Second Unreal");
        var reports = new DeferredStackStatusProvider();
        var viewModel = new MainViewModel(
            new StaticDiscovery([first, second]),
            new StaticStatusProvider(),
            new MemoryStore(),
            new PrefStore(new UiPreferences { CheckForUpdatesAutomatically = true }),
            stackStatusProvider: reports);

        var firstSelection = viewModel.SelectAsync(first);
        await reports.WaitForRequestAsync(first.AppId);
        var secondSelection = viewModel.SelectAsync(second);
        await WaitUntilAsync(() => viewModel.SelectedGame?.AppId == second.AppId && !viewModel.IsSelectionLoading);
        Assert.Equal(9102u, viewModel.SelectedGame?.AppId);
        Assert.Equal("Ready to install", viewModel.SupportTitle);
        reports.CompleteReady(first);
        await firstSelection;
        await reports.WaitForRequestAsync(second.AppId);
        reports.CompleteReady(second);
        await secondSelection;
        Assert.Equal(9102u, viewModel.SelectedGame?.AppId);
    }

    [Fact]
    public async Task LocalArtifactResolveUsesSharedWikiMemoryCache()
    {
        using var temp = new TestDirectory();
        var game = UnrealGame(temp, 9201, "Local Fallback");
        var env = new Dictionary<string, string?>
        {
            ["XDG_CACHE_HOME"] = temp.Combine("cache"),
            ["XDG_CONFIG_HOME"] = temp.Combine("config"),
            ["XDG_DATA_HOME"] = temp.Combine("data")
        };
        var paths = new XdgPaths(temp.Combine("home"), env);
        Directory.CreateDirectory(Path.Combine(paths.AppCacheDirectory, "metadata", "renodx-wiki"));
        var rows = string.Join('\n', Enumerable.Range(0, 200).Select(i =>
            $"| Game {i} | [a](https://github.com/clshortfuse/renodx/releases/download/x/game{i}.addon64) | :white_check_mark: |"));
        await File.WriteAllTextAsync(
            Path.Combine(paths.AppCacheDirectory, "metadata", "renodx-wiki", "Mods.md"),
            $"# List\n\n| Name | Links | Status |\n| --- | --- | --- |\n{rows}\n");

        var resolver = new OfficialArtifactResolver(MetadataHttp.CreateClient(), paths);
        var cold = System.Diagnostics.Stopwatch.StartNew();
        var first = await resolver.ResolveAsync(game, allowNetwork: false);
        cold.Stop();
        var warm = System.Diagnostics.Stopwatch.StartNew();
        var second = await resolver.ResolveAsync(game, allowNetwork: false);
        warm.Stop();

        Assert.Equal(GameProfileSupport.EngineFallback, first.Profile.Profile.RenoDxSupport);
        Assert.Contains(first.Artifacts, item => item.Component == ComponentKind.RenoDx);
        Assert.Equal(GameProfileSupport.EngineFallback, second.Profile.Profile.RenoDxSupport);
        Assert.True(cold.ElapsedMilliseconds < 2000, $"Cold local resolve took {cold.ElapsedMilliseconds} ms");
        Assert.True(warm.ElapsedMilliseconds < cold.ElapsedMilliseconds || warm.ElapsedMilliseconds < 250,
            $"Warm local resolve took {warm.ElapsedMilliseconds} ms after cold {cold.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void StylesDefineThinRoundedScrollbars()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "RhiLinux.Gui", "Styles.axaml")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "RhiLinux.Gui", "Styles.axaml")),
            "/home/nixwinter/Projects/rhi-linux/src/RhiLinux.Gui/Styles.axaml"
        };
        var stylesPath = candidates.First(File.Exists);
        var styles = File.ReadAllText(stylesPath);
        Assert.Contains("ScrollBar:vertical", styles, StringComparison.Ordinal);
        Assert.Contains("Width\" Value=\"8\"", styles, StringComparison.Ordinal);
        Assert.Contains("CornerRadius\" Value=\"8\"", styles, StringComparison.Ordinal);
        Assert.Contains("PART_LineUpButton", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#FF000000\"", styles, StringComparison.Ordinal);
    }

    private static SteamGame UnrealGame(TestDirectory temp, uint appId, string name)
    {
        var root = temp.Directory($"game-{appId}");
        var fileName = $"{name.Replace(" ", string.Empty, StringComparison.Ordinal)}-Win64-Shipping.exe";
        var executable = temp.Pe($"game-{appId}/Binaries/Win64/{fileName}");
        return new SteamGame(appId, name, temp.Path, temp.Path, root, temp.Combine($"pfx-{appId}"),
            executable, Path.GetDirectoryName(executable)!, DetectionConfidence.High, "shipping", GameEngine.Unreal,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 2048, ["shipping"])]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class StaticDiscovery(IReadOnlyList<SteamGame> games) : IGameDiscovery
    {
        public Task<ScanResult> ScanAsync(IReadOnlyDictionary<uint, GameOverride> overrides, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanResult(games, [], [], []));
    }

    private sealed class StaticStatusProvider(ComponentHealth health = ComponentHealth.Available) : IComponentStatusProvider
    {
        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(SteamGame game, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ComponentStatus>>(
            [
                new(ComponentKind.ReShade, health, null, [], "fixture"),
                new(ComponentKind.RenoDx, health, null, [], "fixture"),
                new(ComponentKind.OptiScaler, health == ComponentHealth.Unsupported ? health : ComponentHealth.Experimental, null, [], "fixture")
            ]);
    }

    private sealed class DeferredStackStatusProvider : IStackStatusProvider
    {
        private readonly Dictionary<uint, TaskCompletionSource<StackStatusReport>> pending = [];
        private readonly Dictionary<uint, TaskCompletionSource> requested = [];

        public Task<StackStatusReport> GetAsync(SteamGame game, bool allowNetwork, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            var completion = new TaskCompletionSource<StackStatusReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[game.AppId] = completion;
            if (!requested.TryGetValue(game.AppId, out var asked))
                requested[game.AppId] = asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            asked.TrySetResult();
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public Task WaitForRequestAsync(uint appId)
        {
            if (!requested.TryGetValue(appId, out var asked))
                requested[appId] = asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return asked.Task;
        }

        public void CompleteReady(SteamGame game)
        {
            var profile = new GameProfile("engine-unreal-x64", null, "Unreal Engine 4/5 64-bit fallback", [], null, null,
                GameEngine.Unreal, "DirectX", ["dxgi.dll"], [], [],
                new RenoDxSource(new Uri("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-unrealengine.addon64"),
                    "renodx-unrealengine.addon64", "snapshot", PeArchitecture.X64, false),
                GameProfileSupport.EngineFallback, true, new Dictionary<string, string>(), [],
                ["This game uses the official generic Unreal Engine RenoDX addon."], null);
            var match = new GameProfileMatch(profile, "Supported Unreal engine fallback; no exact profile matched", false);
            var proxy = new ProxySelectionResult("dxgi.dll", DetectionConfidence.High, "fixture", [], [],
                DeploymentPlanner.GenerateLaunchOption("dxgi.dll"));
            var statuses = new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }
                .Select(component => new ComponentStatus(component, ComponentHealth.Available, null, [], "fixture")).ToArray();
            var artifacts = new GameArtifactResolution(match, [], [], true, MetadataCheckState.Online)
            {
                CanAcquireRenoSetup = true,
                CanAcquireOptiScaler = true
            };
            var eligibility = new OptiScalerEligibility(OptiScalerCompatibilityLevel.Experimental, true, false, "fixture");
            pending[game.AppId].TrySetResult(new(match, proxy, artifacts, statuses, eligibility, true, "fixture"));
        }
    }

    private sealed class MemoryStore : IStateStore
    {
        private ApplicationState state = new();
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default)
        {
            this.state = state;
            return Task.CompletedTask;
        }
    }

    private sealed class PrefStore(UiPreferences preferences) : IUiPreferencesStore
    {
        public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(preferences);
        public Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
