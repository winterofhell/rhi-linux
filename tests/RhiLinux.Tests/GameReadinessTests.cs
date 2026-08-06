using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class GameReadinessTests
{
    [Fact]
    public async Task ReadySteamGameProducesReadyOrConfigurationState()
    {
        using var fixture = GameFixture.Create();
        var service = CreateService();
        var result = await service.EvaluateAsync(fixture.Game, PrefetchAvailable());

        Assert.True(result.State is GameReadinessState.Ready or GameReadinessState.ReadyWithWarnings
            or GameReadinessState.NeedsConfiguration);
        Assert.Equal(fixture.Game.InstallId, result.InstallId);
        Assert.False(string.IsNullOrWhiteSpace(result.NextAction));
        Assert.NotNull(result.RecommendedSetup);
    }

    [Fact]
    public async Task MissingExecutableNeedsUserSelection()
    {
        using var fixture = GameFixture.Create();
        var game = fixture.Game with { Executable = null, Confidence = DetectionConfidence.None };
        var result = await CreateService().EvaluateAsync(game, PrefetchAvailable());
        Assert.Equal(GameReadinessState.NeedsUserSelection, result.State);
        Assert.Contains(result.Issues, issue => issue.Code == ReadinessIssueCodes.ExecutableMissing);
    }

    [Fact]
    public async Task NativeLinuxGameIsUnsupported()
    {
        using var fixture = GameFixture.Create();
        var game = fixture.Game with
        {
            Platform = GameBinaryPlatform.Linux,
            IsActionable = false,
            UnsupportedReason = "Native Linux"
        };
        var result = await CreateService().EvaluateAsync(game, PrefetchAvailable());
        Assert.Equal(GameReadinessState.Unsupported, result.State);
        Assert.Contains(result.Issues, issue => issue.Code == ReadinessIssueCodes.NativeGameUnsupported);
    }

    [Fact]
    public async Task AntiCheatCanBlockWhenConfigured()
    {
        using var fixture = GameFixture.Create();
        var game = fixture.Game with { RequiresConfirmation = true };
        var result = await CreateService().EvaluateAsync(game, PrefetchAvailable() with
        {
            WarnBeforeAntiCheatDeployments = true
        });
        Assert.Contains(result.Issues, issue => issue.Code == ReadinessIssueCodes.AntiCheatDetected);
        Assert.True(result.State is GameReadinessState.Unsupported or GameReadinessState.NeedsConfiguration);
    }

    [Fact]
    public async Task ForeignProxyBlocksRecommendation()
    {
        using var fixture = GameFixture.Create();
        var result = await CreateService().EvaluateAsync(fixture.Game, new(
            ForceRefresh: true,
            PrefetchedComponents:
            [
                Status(ComponentKind.ReShade, ComponentHealth.ForeignInstallation),
                Status(ComponentKind.RenoDx, ComponentHealth.Unavailable),
                Status(ComponentKind.OptiScaler, ComponentHealth.Unavailable)
            ]));
        Assert.Contains(result.Issues, issue => issue.Code == ReadinessIssueCodes.ForeignProxyConflict);
        Assert.Equal("foreign-block", result.RecommendedSetup?.PrimaryOptionId);
    }

    [Fact]
    public async Task CacheHitReturnsSameFingerprint()
    {
        using var fixture = GameFixture.Create();
        var service = CreateService();
        var options = PrefetchAvailable();
        var first = await service.EvaluateAsync(fixture.Game, options);
        var second = await service.EvaluateAsync(fixture.Game, options with { ForceRefresh = false });
        Assert.Equal(first.EvaluationFingerprint, second.EvaluationFingerprint);
        Assert.True(second.FromCache);
    }

    [Fact]
    public async Task InvalidateForcesReevaluation()
    {
        using var fixture = GameFixture.Create();
        var service = CreateService();
        await service.EvaluateAsync(fixture.Game, PrefetchAvailable());
        service.Invalidate(fixture.Game.InstallId);
        var second = await service.EvaluateAsync(fixture.Game, PrefetchAvailable() with { ForceRefresh = false });
        Assert.False(second.FromCache);
    }

    [Fact]
    public void RecommendedSetupOrdersDeterministicallyForReshadeOnly()
    {
        using var fixture = GameFixture.Create();
        var setup = new RecommendedSetupService().Build(
            fixture.Game,
            [
                Status(ComponentKind.ReShade, ComponentHealth.Installed, "6.0.0"),
                Status(ComponentKind.RenoDx, ComponentHealth.Available),
                Status(ComponentKind.OptiScaler, ComponentHealth.Available)
            ],
            null);
        Assert.Contains(setup.Options, option => option.Id == "add-renodx" && option.IsSupported);
        Assert.Contains(setup.Options, option => option.Id == "add-optiscaler" && option.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(setup.PrimaryOptionId));
    }

    [Theory]
    [InlineData(GameLauncher.Steam, GameStore.Steam)]
    [InlineData(GameLauncher.Heroic, GameStore.Epic)]
    [InlineData(GameLauncher.Lutris, GameStore.Gog)]
    [InlineData(GameLauncher.Bottles, GameStore.Other)]
    [InlineData(GameLauncher.Legendary, GameStore.Epic)]
    [InlineData(GameLauncher.Minigalaxy, GameStore.Gog)]
    public async Task LaunchConfigurationCoversImplementedLaunchers(GameLauncher launcher, GameStore store)
    {
        using var fixture = GameFixture.Create();
        var provider = new LaunchConfigurationService();
        var result = await provider.GetAsync(
            fixture.Game with { PrimaryLauncher = launcher, Store = store },
            "dxgi.dll",
            "WINEDLLOVERRIDES=\"dxgi=n,b\" %command%");
        Assert.Equal(launcher, result.Launcher);
        Assert.False(string.IsNullOrWhiteSpace(result.CopyValue));
        Assert.Contains("WINEDLLOVERRIDES", result.CopyValue, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(result.PlacementInstructions));
        if (launcher is not GameLauncher.Steam)
            Assert.DoesNotContain("%command%", result.CopyValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeploymentPlanSummaryUsesRealOperations()
    {
        var plan = new DeploymentPlan
        {
            Id = "plan",
            InstallId = "steam:1",
            GameRoot = "/fixture/game",
            DeploymentDirectory = "/fixture/game/bin",
            Action = "install",
            LaunchOption = "WINEDLLOVERRIDES=\"dxgi=n,b\" %command%",
            Operations =
            [
                new(DeploymentOperationType.Backup, "/fixture/game/bin/dxgi.dll"),
                new(DeploymentOperationType.Copy, "/fixture/game/bin/dxgi.dll", BackupPath: "/fixture/backup/dxgi.dll"),
                new(DeploymentOperationType.Move, "/fixture/game/bin/ReShade64.dll"),
                new(DeploymentOperationType.WriteIniValue, "/fixture/game/bin/OptiScaler.ini", Value: "true")
            ]
        };
        var summary = DeploymentPlanSummary.From(plan);
        Assert.True(summary.OperationsCount >= 4);
        Assert.True(summary.ManualLauncherStepRequired);
        Assert.Contains(summary.Highlights, item => item.Contains("Back up", StringComparison.Ordinal));
        Assert.Contains(summary.Highlights, item => item.Contains("manual copy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GuiExposesReadinessAfterSelection()
    {
        using var fixture = GameFixture.Create();
        var viewModel = new MainViewModel(
            new FixedDiscovery([fixture.Game]),
            new FixedStatusProvider(),
            new MemoryState(),
            new MemoryPrefs());
        await viewModel.InitializeAsync();
        await viewModel.RefreshReadinessAsync(true);
        Assert.True(viewModel.Readiness.HasResult);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Readiness.StateLabel));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Readiness.NextAction));
    }

    [Fact]
    public async Task HeroicLaunchConfigurationIsManualCopy()
    {
        using var fixture = GameFixture.Create();
        var result = await new LaunchConfigurationService().GetAsync(
            fixture.Game with { PrimaryLauncher = GameLauncher.Heroic, Store = GameStore.Epic },
            "dxgi.dll",
            "WINEDLLOVERRIDES=\"dxgi=n,b\" %command%");
        Assert.True(result.ManualCopyRequired);
        Assert.False(result.AutomaticEditingSupported);
        Assert.DoesNotContain("%command%", result.CopyValue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Heroic", result.PlacementInstructions, StringComparison.OrdinalIgnoreCase);
    }

    private static GameReadinessService CreateService() =>
        new(recommendations: new RecommendedSetupService(), launchConfiguration: new LaunchConfigurationService());

    private static GameReadinessEvaluationOptions PrefetchAvailable() => new(
        ForceRefresh: true,
        PrefetchedComponents:
        [
            Status(ComponentKind.ReShade, ComponentHealth.Available),
            Status(ComponentKind.RenoDx, ComponentHealth.Available),
            Status(ComponentKind.OptiScaler, ComponentHealth.Available)
        ]);

    private static ComponentStatus Status(ComponentKind component, ComponentHealth health, string? version = null) =>
        new(component, health, version, [], "fixture");

    private sealed class GameFixture : IDisposable
    {
        private readonly string root;
        public InstalledGame Game { get; }

        private GameFixture(string root, InstalledGame game)
        {
            this.root = root;
            Game = game;
        }

        public static GameFixture Create()
        {
            var root = Directory.CreateTempSubdirectory("rhi-readiness-").FullName;
            var exe = Path.Combine(root, "game.exe");
            File.WriteAllBytes(exe, [0x4D, 0x5A, 0x90, 0x00]);
            var steam = InstalledGame.FromSteamGame(new SteamGame(
                10, "Ready Game", Path.Combine(root, "steam"), Path.Combine(root, "library"), root,
                Path.Combine(root, "pfx"), exe, root, DetectionConfidence.High, "fixture", GameEngine.Unreal, []));
            return new GameFixture(root, steam);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    private sealed class FixedDiscovery(IReadOnlyList<InstalledGame> games) : IGameDiscovery
    {
        public Task<ScanResult> ScanAsync(IReadOnlyDictionary<string, GameOverride> overrides, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanResult(games, [], [], []));
    }

    private sealed class FixedStatusProvider : IComponentStatusProvider
    {
        public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ComponentStatus>>(
            [
                Status(ComponentKind.ReShade, ComponentHealth.Available),
                Status(ComponentKind.RenoDx, ComponentHealth.Available),
                Status(ComponentKind.OptiScaler, ComponentHealth.Available)
            ]);
    }

    private sealed class MemoryState : IStateStore
    {
        private ApplicationState state = new();
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default)
        {
            this.state = state;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryPrefs : IUiPreferencesStore
    {
        private UiPreferences preferences = new();
        public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(preferences);
        public Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
        {
            this.preferences = preferences;
            return Task.CompletedTask;
        }
    }
}
