using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class StackStateMatrixTests
{
    public static TheoryData<string> StackCombinations =>
    [
        "reshade",
        "reshade+renodx",
        "optiscaler",
        "optiscaler+reshade",
        "optiscaler+renodx",
        "full"
    ];

    [Theory]
    [MemberData(nameof(StackCombinations))]
    public async Task InstalledStacksRemainHealthyAcrossRefreshAndRestart(string combination)
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallCombinationAsync(temp, game, combination);

        var first = await new ComponentDetector().DetectStackAsync(game);
        AssertHealthyCombination(first, combination);

        var refreshed = await new ComponentDetector().DetectStackAsync(game);
        AssertHealthyCombination(refreshed, combination);

        var restarted = await new ComponentDetector().DetectStackAsync(game, generation: 7);
        AssertHealthyCombination(restarted, combination);
        Assert.Equal(7, restarted.Generation);
    }

    [Theory]
    [MemberData(nameof(StackCombinations))]
    public async Task RemoveOneComponentPreservesRemainingStack(string combination)
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var (planner, executor) = await InstallCombinationAsync(temp, game, combination);
        if (combination is "reshade" or "optiscaler") return;

        var removeKind = combination.Contains("renodx", StringComparison.Ordinal)
            ? ComponentKind.RenoDx
            : ComponentKind.ReShade;
        Assert.True((await executor.ExecuteAsync(await planner.BuildRemovePlanAsync(game, removeKind), false)).Succeeded);

        var snapshot = await new ComponentDetector().DetectStackAsync(game);
        var removed = snapshot.Components.Single(x => x.Component == removeKind);
        Assert.True(removed.State is ComponentLifecycleState.NotInstalled or ComponentLifecycleState.Unsupported);
        if (combination.Contains("optiscaler", StringComparison.Ordinal))
            AssertInstalled(snapshot, ComponentKind.OptiScaler);
        if (combination.Contains("reshade", StringComparison.Ordinal) && removeKind != ComponentKind.ReShade)
            AssertInstalled(snapshot, ComponentKind.ReShade);
    }

    [Fact]
    public async Task WorkingOptiScalerIsNotMarkedRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallCombinationAsync(temp, game, "full");

        var opti = Assert.Single((await new ComponentDetector().DetectStackAsync(game)).Components,
            x => x.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentLifecycleState.InstalledHealthy, opti.State);
        Assert.Null(opti.Evidence.RepairReason);
    }

    [Fact]
    public async Task OptionalFileMissingDoesNotRequireRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallCombinationAsync(temp, game, "optiscaler");
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.Files.Add(new("plugins/OptiPatcher.asi", ComponentKind.OptiPatcher, new string('a', 64), "legacy", null, null));
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var opti = Assert.Single((await new ComponentDetector().DetectStackAsync(game)).Components,
            x => x.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentLifecycleState.InstalledWithWarnings, opti.State);
        Assert.NotEqual(ComponentLifecycleState.RepairRequired, opti.State);
    }

    [Fact]
    public async Task UserEditedIniDoesNotRequireRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallCombinationAsync(temp, game, "optiscaler+reshade");
        await File.AppendAllTextAsync(Path.Combine(game.DeploymentDirectory, "OptiScaler.ini"), "UserSetting=custom\n");

        var opti = Assert.Single((await new ComponentDetector().DetectStackAsync(game)).Components,
            x => x.Component == ComponentKind.OptiScaler);

        Assert.True(opti.State is ComponentLifecycleState.InstalledHealthy or
            ComponentLifecycleState.InstalledMetadataIncomplete);
        Assert.NotEqual(ComponentLifecycleState.RepairRequired, opti.State);
    }

    [Fact]
    public async Task IncompleteMetadataKeepsInstalledState()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallCombinationAsync(temp, game, "reshade");
        await File.AppendAllTextAsync(Path.Combine(game.DeploymentDirectory, "dxgi.dll"), " ReShade ");

        var reshade = Assert.Single((await new ComponentDetector().DetectStackAsync(game)).Components,
            x => x.Component == ComponentKind.ReShade);

        Assert.Equal(ComponentLifecycleState.InstalledMetadataIncomplete, reshade.State);
        Assert.Equal(RuntimeHealth.Healthy, reshade.Runtime);
    }

    [Fact]
    public void VersionNormalizationDoesNotCreateFalseUpdate()
    {
        var report = new ComponentStateReport(
            ComponentKind.OptiScaler,
            ComponentLifecycleState.InstalledHealthy,
            RuntimeHealth.Healthy,
            FileIntegrityStatus.Complete,
            ConfigurationHealth.Valid,
            OwnershipHealth.Managed,
            UpdateAvailability.Unknown,
            CompatibilityStatus.Compatible,
            "v0.9.4",
            "Installed",
            null,
            new ComponentStateEvidence([], [], [], [], "dxgi.dll", ComponentKind.OptiScaler, PeArchitecture.X64,
                "v0.9.4", null, null, "optiscaler.installedhealthy"));
        var artifact = new ResolvedArtifact(
            ComponentKind.OptiScaler, "0.9.4", new("https://example.test/"), "v0.9.4", "OptiScaler.dll",
            PeArchitecture.X64, null, ArtifactSupportKind.General, ArtifactCacheState.DownloadRequired,
            ArtifactValidationState.Valid, null, null, null);

        var updated = UpdateEvaluator.Apply(report, artifact);

        Assert.Equal(ComponentLifecycleState.InstalledHealthy, updated.State);
        Assert.Equal(UpdateAvailability.UpToDate, updated.Update);
    }

    [Fact]
    public async Task GameSwitchDiscardsStaleRepairFlags()
    {
        var first = GameCard(10, "First");
        var second = GameCard(20, "Second");
        var provider = new SequenceStatusProvider(
            Statuses(ComponentHealth.Broken),
            Statuses(ComponentHealth.Installed),
            Statuses(ComponentHealth.Installed));
        var viewModel = new MainViewModel(new FixedDiscovery([first, second]), provider, new MemoryStateStore(),
            new MemoryPreferencesStore(new UiPreferences()));
        await viewModel.SelectAsync(first);
        Assert.Equal(PrimaryActionKind.Repair, viewModel.PrimaryAction);

        await viewModel.SelectAsync(second);

        Assert.NotEqual(PrimaryActionKind.Repair, viewModel.PrimaryAction);
        Assert.All(viewModel.ComponentCards, card => Assert.False(card.CanRepair));
    }

    [Fact]
    public void RemoveButtonsAreVisibleForInstalledManagedComponents()
    {
        var installed = new ComponentCardViewModel(new ComponentStatus(
            ComponentKind.OptiScaler, ComponentHealth.Installed, "v1", ["dxgi.dll"], "Installed",
            InstallationVerification.Managed, Lifecycle: ComponentLifecycleState.InstalledHealthy,
            Ownership: OwnershipHealth.Managed));
        var update = new ComponentCardViewModel(new ComponentStatus(
            ComponentKind.ReShade, ComponentHealth.Outdated, "6.7.2", ["dxgi.dll"], "Update",
            InstallationVerification.Managed, Lifecycle: ComponentLifecycleState.UpdateAvailable,
            Ownership: OwnershipHealth.Managed));
        var repair = new ComponentCardViewModel(new ComponentStatus(
            ComponentKind.RenoDx, ComponentHealth.PartiallyInstalled, null, [], "Missing addon",
            InstallationVerification.RepairNeeded, Lifecycle: ComponentLifecycleState.RepairRequired,
            RepairReason: "The required RenoDX addon file is missing.", Ownership: OwnershipHealth.Incomplete));

        Assert.True(installed.CanRemove);
        Assert.Equal("Remove", installed.ActionText);
        Assert.True(update.CanRemove);
        Assert.True(update.ShowRemoveAction);
        Assert.True(repair.CanRemove);
        Assert.True(repair.ShowRemoveAction);
        Assert.Equal("The required RenoDX addon file is missing.", repair.Explanation);
    }

    [Fact]
    public async Task RemoveAllRestoresEmptyDetection()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var (planner, executor) = await InstallCombinationAsync(temp, game, "full");
        Assert.True((await executor.ExecuteAsync(await planner.BuildRemoveStackPlanAsync(game), false)).Succeeded);

        var snapshot = await new ComponentDetector().DetectStackAsync(game);
        Assert.Equal(StackLayoutKind.Empty, snapshot.Layout);
        Assert.All(snapshot.Components, component =>
            Assert.True(component.State is ComponentLifecycleState.NotInstalled or ComponentLifecycleState.Unsupported));
    }

    private static void AssertHealthyCombination(StackSnapshot snapshot, string combination)
    {
        if (combination.Contains("reshade", StringComparison.Ordinal) || combination == "full" ||
            combination.Contains("renodx", StringComparison.Ordinal))
            AssertInstalled(snapshot, ComponentKind.ReShade);
        if (combination.Contains("renodx", StringComparison.Ordinal) || combination == "full")
            AssertInstalled(snapshot, ComponentKind.RenoDx);
        if (combination.Contains("optiscaler", StringComparison.Ordinal) || combination == "full")
            AssertInstalled(snapshot, ComponentKind.OptiScaler);
        Assert.DoesNotContain(snapshot.Components, component =>
            component.State == ComponentLifecycleState.RepairRequired);
        Assert.DoesNotContain(snapshot.Components, component =>
            component.State == ComponentLifecycleState.UpdateAvailable);
    }

    private static void AssertInstalled(StackSnapshot snapshot, ComponentKind component)
    {
        var report = Assert.Single(snapshot.Components, x => x.Component == component);
        Assert.True(report.State is ComponentLifecycleState.InstalledHealthy or
            ComponentLifecycleState.InstalledWithWarnings or ComponentLifecycleState.InstalledMetadataIncomplete,
            $"{component} state was {report.State}: {report.Explanation}");
    }

    private static async Task<(DeploymentPlanner Planner, DeploymentExecutor Executor)> InstallCombinationAsync(
        TestDirectory temp,
        SteamGame game,
        string combination)
    {
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var includeReShade = combination is "reshade" or "reshade+renodx" or "optiscaler+reshade" or "optiscaler+renodx" or "full";
        var includeReno = combination is "reshade+renodx" or "optiscaler+renodx" or "full";
        var includeOpti = combination is "optiscaler" or "optiscaler+reshade" or "optiscaler+renodx" or "full";
        if (combination is "optiscaler+renodx") includeReShade = true;

        ComponentArtifact? reshade = includeReShade
            ? new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade64.dll", "ReShade reshade.me"), "ReShade64.dll", "6.7.3")
            : null;
        ComponentArtifact? reno = includeReno
            ? new(ComponentKind.RenoDx, temp.Pe("stage/renodx-fixture.addon64"), "renodx-fixture.addon64", "1")
            : null;
        ComponentArtifact? opti = includeOpti
            ? new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4")
            : null;
        var support = includeOpti
            ? new[]
            {
                new ComponentArtifact(ComponentKind.OptiScaler,
                    temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                    "OptiScaler.ini", "v0.9.4")
            }
            : null;
        var artifacts = new RecommendedStackArtifacts(reshade, reno, opti, support);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        return (planner, executor);
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new SteamGame(42, "Fixture", temp.Path, temp.Path, root, temp.Combine("pfx"), executable, root,
            DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]);
    }

    private static InstalledGame GameCard(uint appId, string name) =>
        InstalledGame.FromSteamGame(new SteamGame(appId, name, "/steam", "/steam", $"/games/{appId}", $"/pfx/{appId}",
            $"/games/{appId}/Game.exe", $"/games/{appId}", DetectionConfidence.High, "fixture",
            GameEngine.Unknown, [new($"/games/{appId}/Game.exe", 80, DetectionConfidence.High, PeArchitecture.X64, 1, [])]));

    private static IReadOnlyList<ComponentStatus> Statuses(ComponentHealth health) =>
    [
        new(ComponentKind.ReShade, health, null, health == ComponentHealth.Installed ? ["dxgi.dll"] : [], $"{health}",
            health == ComponentHealth.Broken ? InstallationVerification.RepairNeeded : InstallationVerification.None,
            Lifecycle: health switch
            {
                ComponentHealth.Broken => ComponentLifecycleState.RepairRequired,
                ComponentHealth.Installed => ComponentLifecycleState.InstalledHealthy,
                _ => ComponentLifecycleState.NotInstalled
            }),
        new(ComponentKind.RenoDx, ComponentHealth.Unavailable, null, [], "unavailable",
            Lifecycle: ComponentLifecycleState.NotInstalled),
        new(ComponentKind.OptiScaler, ComponentHealth.Available, null, [], "available",
            Lifecycle: ComponentLifecycleState.NotInstalled)
    ];

    private sealed class FixedDiscovery(IReadOnlyList<InstalledGame> games) : IGameDiscovery
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

    private sealed class SequenceStatusProvider(params IReadOnlyList<ComponentStatus>[] scans) : IComponentStatusProvider
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
