using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class FullStackLifecycleRegressionTests
{
    [Fact]
    public async Task CyberpunkLikeSequentialStackStaysHealthyThroughRefreshRestartUpdateAndRepair()
    {
        using var temp = new TestDirectory();
        var game = CyberpunkLike(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var reshadeSource = temp.PeWithMarker("stage/ReShade.dll", "reshade.me");
        var reshadeHash = await ArtifactDownloader.Sha256Async(reshadeSource);
        var renoSource = temp.Pe("stage/renodx-cp2077.addon64");
        var optiSource = temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler");

        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, reshadeSource, "ReShade.dll", "6.7.3")), false)).Succeeded);
        AssertStandalone(await Detect(game), ComponentKind.ReShade);

        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.RenoDx, renoSource, "renodx-cp2077.addon64", "snapshot")), false)).Succeeded);
        var afterReno = await Detect(game);
        AssertInstalled(afterReno, ComponentKind.ReShade);
        AssertInstalled(afterReno, ComponentKind.RenoDx);
        Assert.Equal("dxgi.dll", afterReno.ActiveProxy);
        Assert.Equal(ComponentKind.ReShade, afterReno.ProxyOwner);

        var migrate = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, optiSource, "OptiScaler.dll", "v0.9.4"));
        Assert.Contains(migrate.Operations, operation =>
            operation.Type == DeploymentOperationType.Move &&
            operation.Source!.EndsWith("dxgi.dll", StringComparison.Ordinal) &&
            operation.Target.EndsWith("ReShade64.dll", StringComparison.Ordinal));
        Assert.Contains(migrate.ExpectedComponentStates, x => x.Component == ComponentKind.OptiScaler && x.Installed);
        Assert.Contains(migrate.ExpectedComponentStates, x => x.Component == ComponentKind.ReShade && x.Installed);
        Assert.Contains(migrate.ExpectedComponentStates, x => x.Component == ComponentKind.RenoDx && x.Installed);
        Assert.True((await executor.ExecuteAsync(migrate, false)).Succeeded);

        await AssertHealthyFullStackAsync(game, reshadeHash);

        var refreshed = await Detect(game);
        await AssertHealthyFullStackAsync(game, reshadeHash, refreshed);

        var restarted = await Detect(game with { });
        await AssertHealthyFullStackAsync(game, reshadeHash, restarted);

        var reshade = Assert.Single(restarted.Components, x => x.Component == ComponentKind.ReShade);
        var withoutHash = new ResolvedArtifact(
            ComponentKind.ReShade, "6.7.3", new("https://reshade.me/"), "6.7.3", "ReShade64.dll",
            PeArchitecture.X64, null, ArtifactSupportKind.General, ArtifactCacheState.DownloadRequired,
            ArtifactValidationState.Valid, null, null, null);
        var updated = UpdateEvaluator.Apply(reshade, withoutHash);
        Assert.Equal(UpdateAvailability.UpToDate, updated.Update);
        Assert.NotEqual(ComponentLifecycleState.UpdateAvailable, updated.State);
        Assert.False(new ComponentCardViewModel(StackDetector.ToComponentStatus(updated)).CanUpdate);

        var beforeRepair = await SnapshotHashesAsync(game.DeploymentDirectory);
        var repair = await planner.BuildRepairPlanAsync(game, ComponentKind.OptiScaler,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/repair/OptiScaler.dll", "OptiScaler"),
                "OptiScaler.dll", "v0.9.4"));
        Assert.False(repair.RequiresRepair);
        Assert.Empty(repair.Operations);
        Assert.True((await executor.ExecuteAsync(repair, false)).Succeeded);
        foreach (var (path, hash) in beforeRepair)
            Assert.Equal(hash, await ArtifactDownloader.Sha256Async(path));

        Assert.Equal("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%", restarted.LaunchOptionRequirement);
        Assert.DoesNotContain("gamemoderun", restarted.LaunchOptionRequirement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LD_PRELOAD", restarted.LaunchOptionRequirement, StringComparison.OrdinalIgnoreCase);
        var hdr = SteamLaunchOptionService.GenerateHdrGuidance(restarted.ActiveProxy);
        Assert.StartsWith("PROTON_ENABLE_WAYLAND=1 DXVK_HDR=1 WINEDLLOVERRIDES=", hdr, StringComparison.Ordinal);
        Assert.DoesNotContain("gamemoderun", hdr, StringComparison.OrdinalIgnoreCase);

        Assert.True((await executor.ExecuteAsync(await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler), false)).Succeeded);
        var restored = await Detect(game);
        Assert.Equal(ComponentKind.ReShade, restored.ProxyOwner);
        Assert.Equal("dxgi.dll", restored.ActiveProxy);
        Assert.False(File.Exists(Path.Combine(game.DeploymentDirectory, "ReShade64.dll")));
        AssertInstalled(restored, ComponentKind.ReShade);
        AssertInstalled(restored, ComponentKind.RenoDx);

        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, optiSource, "OptiScaler.dll", "v0.9.4")), false)).Succeeded);
        await AssertHealthyFullStackAsync(game, reshadeHash);
    }

    [Fact]
    public async Task GuiPostOperationVerificationSucceedsForMigratedFullStack()
    {
        using var temp = new TestDirectory();
        var game = CyberpunkLike(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var reshadeSource = temp.PeWithMarker("stage/ReShade.dll", "reshade.me");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, reshadeSource, "ReShade.dll", "6.7.3")), false)).Succeeded);
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-cp2077.addon64"), "renodx-cp2077.addon64", "snapshot")),
            false)).Succeeded);

        var viewModel = new MainViewModel(new FixedDiscovery([InstalledGame.FromSteamGame(game)]), new DetectorStatusProvider(), new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences { CheckForUpdatesAutomatically = false }),
            executor: executor);
        await viewModel.SelectAsync(InstalledGame.FromSteamGame(game));
        var plan = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"));
        var result = await viewModel.ExecuteAsync(plan, false);

        Assert.True(result.IsSuccessfulOutcome, result.Error);
        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.DoesNotContain("Operation failed", viewModel.GlobalStatus ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.All(viewModel.ComponentCards, card =>
        {
            Assert.False(card.CanRepair);
            Assert.False(card.CanUpdate);
            Assert.Contains("Installed", card.State, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%", viewModel.LaunchOption);
        Assert.DoesNotContain("gamemoderun", viewModel.LaunchOption ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.ShowHdrGuidance);
        Assert.StartsWith("PROTON_ENABLE_WAYLAND=1 DXVK_HDR=1", viewModel.HdrLaunchOption ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task AssertHealthyFullStackAsync(SteamGame game, string reshadeHash, StackSnapshot? snapshot = null)
    {
        snapshot ??= await Detect(game);
        Assert.Equal(StackLayoutKind.FullStack, snapshot.Layout);
        Assert.Equal("dxgi.dll", snapshot.ActiveProxy);
        Assert.Equal(ComponentKind.OptiScaler, snapshot.ProxyOwner);
        Assert.True(snapshot.ChainingConfigured);
        Assert.True(snapshot.IsHealthy);
        Assert.Empty(snapshot.ConcreteDefects ?? []);
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "ReShade64.dll")));
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "renodx-cp2077.addon64")));
        Assert.Equal(reshadeHash, await ArtifactDownloader.Sha256Async(Path.Combine(game.DeploymentDirectory, "ReShade64.dll")));
        Assert.Equal("true", IniDocument.Parse(await File.ReadAllTextAsync(Path.Combine(game.DeploymentDirectory, "OptiScaler.ini")))
            .Get("Plugins", "LoadReshade"));
        Assert.Equal("true", IniDocument.Parse(await File.ReadAllTextAsync(Path.Combine(game.DeploymentDirectory, "OptiScaler.ini")))
            .Get("Plugins", "LoadAsiPlugins"));

        foreach (var component in new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler })
        {
            var report = Assert.Single(snapshot.Components, x => x.Component == component);
            Assert.Equal(ComponentLifecycleState.InstalledHealthy, report.State);
            var card = new ComponentCardViewModel(StackDetector.ToComponentStatus(report));
            Assert.False(card.CanRepair);
            Assert.False(card.CanUpdate);
            Assert.Equal("Installed", card.State);
        }
    }

    private static void AssertStandalone(StackSnapshot snapshot, ComponentKind component)
    {
        AssertInstalled(snapshot, component);
        Assert.Equal(ComponentKind.ReShade, snapshot.ProxyOwner);
    }

    private static void AssertInstalled(StackSnapshot snapshot, ComponentKind component)
    {
        var report = Assert.Single(snapshot.Components, x => x.Component == component);
        Assert.Equal(ComponentLifecycleState.InstalledHealthy, report.State);
    }

    private static Task<StackSnapshot> Detect(SteamGame game) =>
        new ComponentDetector().DetectStackAsync(game);

    private static async Task<Dictionary<string, string>> SnapshotHashesAsync(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result[path] = await ArtifactDownloader.Sha256Async(path);
        return result;
    }

    private static SteamGame CyberpunkLike(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var deploy = temp.Directory("game/bin/x64");
        var executable = temp.Pe("game/bin/x64/Cyberpunk2077.exe");
        return new SteamGame(1091500, "Cyberpunk 2077", temp.Path, temp.Path, root,
            temp.Combine("compatdata", "1091500", "pfx"), executable, deploy, DetectionConfidence.High,
            "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, new FileInfo(executable).Length, ["fixture"])]);
    }

    private sealed class FixedDiscovery(IReadOnlyList<InstalledGame> games) : IGameDiscovery
    {
        public Task<ScanResult> ScanAsync(IReadOnlyDictionary<string, GameOverride> overrides, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanResult(games, [], [], []));
    }

    private sealed class DetectorStatusProvider : IComponentStatusProvider
    {
        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken) =>
            new ComponentDetector().DetectAsync(game, cancellationToken);
    }

    private sealed class MemoryStateStore : IStateStore
    {
        private ApplicationState state = new();
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
        public Task SaveAsync(ApplicationState value, CancellationToken cancellationToken = default)
        {
            state = value;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryPreferencesStore(UiPreferences preferences) : IUiPreferencesStore
    {
        public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(preferences);
        public Task SaveAsync(UiPreferences value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
