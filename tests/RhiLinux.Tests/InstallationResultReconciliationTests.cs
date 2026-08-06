using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class InstallationResultReconciliationTests
{
    [Fact]
    public async Task VerifiedSuccessWithStaleDetectorDoesNotReportOperationFailed()
    {
        var game = Game(10, "First");
        var provider = new PerScanComponentStatusProvider(
            ComponentStatuses((ComponentKind.ReShade, ComponentHealth.Available),
                (ComponentKind.RenoDx, ComponentHealth.Unavailable),
                (ComponentKind.OptiScaler, ComponentHealth.Available)),
            ComponentStatuses((ComponentKind.ReShade, ComponentHealth.Available),
                (ComponentKind.RenoDx, ComponentHealth.Unavailable),
                (ComponentKind.OptiScaler, ComponentHealth.Available)),
            ComponentStatuses((ComponentKind.ReShade, ComponentHealth.Installed),
                (ComponentKind.RenoDx, ComponentHealth.Unavailable),
                (ComponentKind.OptiScaler, ComponentHealth.Available)));
        var viewModel = new MainViewModel(new FakeDiscovery([game]), provider, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor());
        await viewModel.SelectAsync(game);
        var plan = Plan(game, "install ReShade", new ComponentStateExpectation(ComponentKind.ReShade, true));
        plan.SelectionGeneration = 1;

        var result = await viewModel.ExecuteAsync(plan, false);

        Assert.True(result.IsSuccessfulOutcome);
        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.True(result.State is OperationLifecycleState.Succeeded or OperationLifecycleState.SucceededWithWarning);
        Assert.DoesNotContain("Operation failed", viewModel.GlobalStatus ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContradictoryDetectorStateStillFails()
    {
        var game = Game(11, "Second");
        var provider = new PerScanComponentStatusProvider(
            ComponentStatuses((ComponentKind.ReShade, ComponentHealth.Available),
                (ComponentKind.RenoDx, ComponentHealth.Unavailable),
                (ComponentKind.OptiScaler, ComponentHealth.Available)),
            ComponentStatuses((ComponentKind.ReShade, ComponentHealth.Broken),
                (ComponentKind.RenoDx, ComponentHealth.Unavailable),
                (ComponentKind.OptiScaler, ComponentHealth.Available)));
        var viewModel = new MainViewModel(new FakeDiscovery([game]), provider, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()), executor: new SuccessfulExecutor());
        await viewModel.SelectAsync(game);
        var plan = Plan(game, "install ReShade", new ComponentStateExpectation(ComponentKind.ReShade, true));
        plan.SelectionGeneration = 1;

        var result = await viewModel.ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.False(result.IsSuccessfulOutcome);
        Assert.Contains("every requested component state", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TolerantIniRepairUpdatesManagedKeysWithoutDiscardingUserLines()
    {
        var originalBytes = """
            [Plugins]
            LoadReshade=false
            LoadAsiPlugins=false
            UserKeep=yes
            [Broken
            Legacy=1
            """u8.ToArray();
        var document = IniDocument.Parse(originalBytes);
        Assert.True(document.HasRecoverableIssues);
        Assert.True(document.CanSafelyEditManagedKeys([("Plugins", "LoadReshade"), ("Plugins", "LoadAsiPlugins")]));
        document.Set("Plugins", "LoadReshade", "true");
        document.Set("Plugins", "LoadAsiPlugins", "true");
        var text = document.ToString();
        Assert.Equal("true", document.Get("Plugins", "LoadReshade"));
        Assert.Equal("true", document.Get("Plugins", "LoadAsiPlugins"));
        Assert.Contains("UserKeep=yes", text, StringComparison.Ordinal);
        Assert.Contains("[Broken", text, StringComparison.Ordinal);
        Assert.Contains("Legacy=1", text, StringComparison.Ordinal);
    }

    private static InstalledGame Game(uint appId, string name) =>
        InstalledGame.FromSteamGame(new SteamGame(appId, name, "/steam", "/steam", $"/games/{appId}", $"/pfx/{appId}",
            $"/games/{appId}/Game.exe", $"/games/{appId}", DetectionConfidence.High, "fixture",
            GameEngine.Unknown, [new($"/games/{appId}/Game.exe", 80, DetectionConfidence.High, PeArchitecture.X64, 1, [])]));

    private static IReadOnlyList<ComponentStatus> ComponentStatuses(
        params (ComponentKind Component, ComponentHealth Health)[] values) =>
        values.Select(value => new ComponentStatus(value.Component, value.Health, null, [],
            $"{value.Component} {value.Health}")).ToArray();

    private static DeploymentPlan Plan(InstalledGame game, string action, params ComponentStateExpectation[] expectations) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory ?? game.GameRoot,
            Action = action,
            ExpectedComponentStates = expectations.ToList()
        };

    private sealed class FakeDiscovery(IReadOnlyList<InstalledGame> games) : IGameDiscovery
    {
        public Task<ScanResult> ScanAsync(IReadOnlyDictionary<string, GameOverride> overrides, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanResult(games, [], [], []));
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

    private sealed class SuccessfulExecutor : IDeploymentExecutor
    {
        public Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionResult(true, dryRun, false, ["ok"], State: OperationLifecycleState.Succeeded));
    }

    private sealed class PerScanComponentStatusProvider(params IReadOnlyList<ComponentStatus>[] scans) : IComponentStatusProvider
    {
        private int count;

        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken)
        {
            var result = scans[Math.Min(count, scans.Length - 1)];
            count++;
            return Task.FromResult(result);
        }
    }
}
