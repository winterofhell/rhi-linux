using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class FinalPassRegressionTests
{
    [Fact]
    public async Task ManualReShadeMatchingOfficialHashIsUpToDateWithoutUpdate()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var official = temp.PeWithMarker("official/ReShade64.dll", "reshade.me");
        var hash = await ArtifactDownloader.Sha256Async(official);
        File.Copy(official, Path.Combine(game.DeploymentDirectory, "dxgi.dll"));

        var report = new ComponentStateReport(
            ComponentKind.ReShade,
            ComponentLifecycleState.InstalledUnmanaged,
            RuntimeHealth.Healthy,
            FileIntegrityStatus.Complete,
            ConfigurationHealth.Valid,
            OwnershipHealth.Unmanaged,
            UpdateAvailability.Unknown,
            CompatibilityStatus.Compatible,
            null,
            "Installed unmanaged",
            null,
            new ComponentStateEvidence(
                [Path.Combine(game.DeploymentDirectory, "dxgi.dll")],
                ["dxgi.dll"], [], [], "dxgi.dll", ComponentKind.ReShade, PeArchitecture.X64,
                null, null, null, "reshade.installedunmanaged"));
        var artifact = new ResolvedArtifact(
            ComponentKind.ReShade, "6.7.3", new("https://reshade.me/"), "6.7.3", "ReShade64.dll",
            PeArchitecture.X64, null, ArtifactSupportKind.General, ArtifactCacheState.DownloadRequired,
            ArtifactValidationState.Valid, hash, null, null);

        var updated = UpdateEvaluator.Apply(report, artifact);
        var card = new ComponentCardViewModel(StackDetector.ToComponentStatus(updated), artifact);

        Assert.Equal(UpdateAvailability.UpToDate, updated.Update);
        Assert.Equal(ComponentLifecycleState.InstalledUnmanaged, updated.State);
        Assert.False(card.CanUpdate);
        Assert.False(card.CanRepair);
        Assert.Equal("Installed", card.State);
        Assert.Equal("Up to date", card.Explanation);
    }

    [Fact]
    public void ManualInstallWithoutHashMatchCanBeRemovedThroughRecovery()
    {
        var report = new ComponentStateReport(
            ComponentKind.OptiScaler,
            ComponentLifecycleState.InstalledUnmanaged,
            RuntimeHealth.Healthy,
            FileIntegrityStatus.Complete,
            ConfigurationHealth.Valid,
            OwnershipHealth.Unmanaged,
            UpdateAvailability.Unknown,
            CompatibilityStatus.Compatible,
            null,
            "Installed unmanaged",
            null,
            new ComponentStateEvidence(
                ["dxgi.dll"], ["dxgi.dll"], [], [], "dxgi.dll", ComponentKind.OptiScaler, PeArchitecture.X64,
                null, null, null, "optiscaler.installedunmanaged"));
        var artifact = new ResolvedArtifact(
            ComponentKind.OptiScaler, "v0.9.4", new("https://github.com/optiscaler/OptiScaler"), "v0.9.4",
            "OptiScaler.dll", PeArchitecture.X64, null, ArtifactSupportKind.General,
            ArtifactCacheState.DownloadRequired, ArtifactValidationState.Valid, new string('a', 64), null, null);

        var updated = UpdateEvaluator.Apply(report, artifact);
        var card = new ComponentCardViewModel(StackDetector.ToComponentStatus(updated));

        Assert.Equal(UpdateAvailability.ManualInstallationDetected, updated.Update);
        Assert.Equal(ComponentLifecycleState.InstalledUnmanaged, updated.State);
        Assert.False(card.CanUpdate);
        Assert.False(card.CanRepair);
        Assert.False(card.NoActionNeeded);
        Assert.True(card.CanRemove);
        Assert.Equal("Installed manually", card.State);
        Assert.Equal("Remove", card.ActionText);
        Assert.Contains("recovery plan", card.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOwnershipAndVersionUnknownNeverShowsUpdate()
    {
        var report = new ComponentStateReport(
            ComponentKind.ReShade,
            ComponentLifecycleState.InstalledMetadataIncomplete,
            RuntimeHealth.Healthy,
            FileIntegrityStatus.Complete,
            ConfigurationHealth.Valid,
            OwnershipHealth.Incomplete,
            UpdateAvailability.Unknown,
            CompatibilityStatus.Compatible,
            null,
            "Installed",
            null,
            new ComponentStateEvidence(
                [], ["dxgi.dll"], [], [], "dxgi.dll", ComponentKind.ReShade, PeArchitecture.X64,
                null, null, null, "reshade.incomplete"));
        var artifact = new ResolvedArtifact(
            ComponentKind.ReShade, "6.7.3", new("https://reshade.me/"), "6.7.3", "ReShade64.dll",
            PeArchitecture.X64, null, ArtifactSupportKind.General, ArtifactCacheState.DownloadRequired,
            ArtifactValidationState.Valid, new string('b', 64), null, null);

        var updated = UpdateEvaluator.Apply(report, artifact);
        Assert.Equal(UpdateAvailability.InstalledVersionUnknown, updated.Update);
        Assert.NotEqual(ComponentLifecycleState.UpdateAvailable, updated.State);
    }

    [Fact]
    public async Task SameBinaryDifferentFilenameIsUpToDate()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var official = temp.PeWithMarker("official/ReShade64.dll", "reshade.me");
        var hash = await ArtifactDownloader.Sha256Async(official);
        File.Copy(official, Path.Combine(game.DeploymentDirectory, "winmm.dll"));
        var report = Report(ComponentKind.ReShade, OwnershipHealth.Unmanaged,
            ComponentLifecycleState.InstalledUnmanaged, [Path.Combine(game.DeploymentDirectory, "winmm.dll")], null);
        var artifact = new ResolvedArtifact(
            ComponentKind.ReShade, "6.7.3", new("https://example.invalid/ReShade_new.dll"), "6.7.3", "ReShade_new.dll",
            PeArchitecture.X64, null, ArtifactSupportKind.General, ArtifactCacheState.DownloadRequired,
            ArtifactValidationState.Valid, hash, null, null);

        Assert.Equal(UpdateAvailability.UpToDate, UpdateEvaluator.Compare(report, artifact));
    }

    [Fact]
    public async Task ChangedUrlWithIdenticalHashIsUpToDate()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var dll = temp.PeWithMarker("game/dxgi.dll", "reshade.me");
        var hash = await ArtifactDownloader.Sha256Async(dll);
        var report = Report(ComponentKind.ReShade, OwnershipHealth.Managed,
            ComponentLifecycleState.InstalledHealthy, [dll], "6.7.3");
        var artifact = new ResolvedArtifact(
            ComponentKind.ReShade, "6.7.3", new("https://example.invalid/redirect/ReShade64.dll"), "6.7.3",
            "ReShade64.dll", PeArchitecture.X64, null, ArtifactSupportKind.General,
            ArtifactCacheState.DownloadRequired, ArtifactValidationState.Valid, hash, null, null);

        Assert.Equal(UpdateAvailability.UpToDate, UpdateEvaluator.Compare(report, artifact));
    }

    [Fact]
    public async Task RollingSnapshotIdentityDoesNotCompareAgainstShaLabel()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var addon = temp.Pe("game/renodx-fixture.addon64");
        var hash = await ArtifactDownloader.Sha256Async(addon);
        var report = Report(ComponentKind.RenoDx, OwnershipHealth.Managed,
            ComponentLifecycleState.InstalledHealthy, [addon], "snapshot");
        var artifact = new ResolvedArtifact(
            ComponentKind.RenoDx, "snapshot", new("https://github.com/example/releases/download/snapshot/a.addon64"),
            "snapshot", "a.addon64", PeArchitecture.X64, null, ArtifactSupportKind.ExactGameProfile,
            ArtifactCacheState.DownloadRequired, ArtifactValidationState.Valid, hash, null, null);

        Assert.Equal(UpdateAvailability.UpToDate, UpdateEvaluator.Compare(report, artifact));
    }

    [Fact]
    public void MutableIniChangeDoesNotCreateUpdate()
    {
        var report = Report(ComponentKind.OptiScaler, OwnershipHealth.Managed,
            ComponentLifecycleState.InstalledHealthy, ["OptiScaler.ini"], "v0.9.4");
        var artifact = new ResolvedArtifact(
            ComponentKind.OptiScaler, "v0.9.4", new("https://github.com/optiscaler/OptiScaler"), "v0.9.4",
            "OptiScaler.dll", PeArchitecture.X64, null, ArtifactSupportKind.General,
            ArtifactCacheState.DownloadRequired, ArtifactValidationState.Valid, new string('c', 64), null, null);

        Assert.NotEqual(UpdateAvailability.UpdateAvailable, UpdateEvaluator.Compare(report, artifact));
    }

    [Fact]
    public async Task OptionalOptiScalerFilesAbsentDoNotRequireRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            null, null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);

        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        manifest.Files.Add(new ManagedFile(
            "amd_fidelityfx_dx12.dll", ComponentKind.OptiScaler, new string('d', 64), "v0.9.4", null, null,
            Requirement: DeploymentFileRequirement.Conditional,
            FileClass: ManagedFileClass.ImmutableRuntimeBinary,
            BundleRelativePath: "amd_fidelityfx_dx12.dll"));
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        await File.WriteAllTextAsync(manifestPath,
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            x => x.Component == ComponentKind.OptiScaler);
        Assert.NotEqual(ComponentHealth.Broken, status.Health);
        Assert.NotEqual(ComponentHealth.PartiallyInstalled, status.Health);
        Assert.NotEqual(ComponentLifecycleState.RepairRequired, status.Lifecycle);
        Assert.False(new ComponentCardViewModel(status).CanRepair);
    }

    [Fact]
    public async Task GenuineRequiredOptiScalerProxyMissingRequiresRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            null, null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        File.Delete(Path.Combine(game.DeploymentDirectory, "dxgi.dll"));

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            x => x.Component == ComponentKind.OptiScaler);
        Assert.Equal(ComponentLifecycleState.RepairRequired, status.Lifecycle);
        Assert.True(new ComponentCardViewModel(status).CanRepair);
    }

    [Fact]
    public async Task CombinedReShadeOptiScalerInstallRemainsHealthyAfterRefresh()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.7.3"),
            null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);

        var first = await new ComponentDetector().DetectAsync(game);
        var second = await new ComponentDetector().DetectAsync(game);
        Assert.Equal(ComponentHealth.Installed, Assert.Single(first, x => x.Component == ComponentKind.ReShade).Health);
        Assert.Equal(ComponentHealth.Installed, Assert.Single(first, x => x.Component == ComponentKind.OptiScaler).Health);
        Assert.Equal(ComponentHealth.Installed, Assert.Single(second, x => x.Component == ComponentKind.ReShade).Health);
        Assert.Equal(ComponentHealth.Installed, Assert.Single(second, x => x.Component == ComponentKind.OptiScaler).Health);
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "ReShade64.dll")));
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "dxgi.dll")));
    }

    [Fact]
    public void HdrGuidanceComposesManagedFragmentsWithoutUnrelatedUserArgs()
    {
        var composed = SteamLaunchOptionService.Compose(
            "winmm.dll",
            includeHdr: true,
            existingLaunchOptions: "PROTON_USE_WINED3D=1 gamemoderun %command%",
            manageHdr: true);

        Assert.Contains(SteamLaunchOptionService.DxvkHdr, composed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(SteamLaunchOptionService.ProtonEnableWayland, composed.Text, StringComparison.Ordinal);
        Assert.Contains("winmm=n,b", composed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PROTON_USE_WINED3D=1", composed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("gamemoderun", composed.Text, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("%command%", composed.Text, StringComparison.Ordinal);
        Assert.Equal(
            $"{SteamLaunchOptionService.DxvkHdr} WINEDLLOVERRIDES=\"winmm=n,b\" %command%",
            composed.Text);
    }

    [Fact]
    public void MergeManagedOverridePreservesUnrelatedUserArguments()
    {
        var merged = SteamLaunchOptionService.MergeManagedOverride(
            "gamemoderun PROTON_USE_WINED3D=1 %command%",
            DeploymentPlanner.GenerateLaunchOption("dxgi.dll"));
        Assert.Contains("gamemoderun", merged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PROTON_USE_WINED3D=1", merged, StringComparison.Ordinal);
        Assert.Contains("WINEDLLOVERRIDES=\"dxgi=n,b\"", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedHdrRemovalPreservesPreExistingUserHdr()
    {
        var preexisting = SteamLaunchOptionService.Compose(
            "dxgi.dll",
            includeHdr: false,
            existingLaunchOptions: "PROTON_ENABLE_WAYLAND=1 DXVK_HDR=1 MANGOHUD=1 %command%",
            manageHdr: false,
            preserveUnrelatedArguments: true);
        Assert.Contains(preexisting.Fragments, fragment =>
            fragment.Text.Contains("PROTON_ENABLE_WAYLAND", StringComparison.OrdinalIgnoreCase) &&
            fragment.Ownership == LaunchOptionFragmentOwnership.PreExisting);

        var stripped = SteamLaunchOptionService.RemoveManagedHdrFragments(
            "PROTON_ENABLE_WAYLAND=1 DXVK_HDR=1 WINEDLLOVERRIDES=\"dxgi=n,b\" %command%",
            removeOnlyManaged: true);
        Assert.DoesNotContain("PROTON_ENABLE_WAYLAND", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DXVK_HDR", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WINEDLLOVERRIDES", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenoDxHdrGuidanceVisibleOnlyWhenInstalled()
    {
        var installed = new ComponentStatus(
            ComponentKind.RenoDx, ComponentHealth.Installed, "snapshot", ["renodx-fixture.addon64"],
            "Installed", InstallationVerification.Managed, Lifecycle: ComponentLifecycleState.InstalledHealthy,
            Ownership: OwnershipHealth.Managed, Update: UpdateAvailability.UpToDate);
        var missing = new ComponentStatus(
            ComponentKind.RenoDx, ComponentHealth.Available, null, [], "Not installed",
            Lifecycle: ComponentLifecycleState.NotInstalled);

        var withReno = SteamLaunchOptionService.GenerateHdrGuidance("dxgi.dll");
        Assert.DoesNotContain("PROTON_ENABLE_WAYLAND=1", withReno, StringComparison.Ordinal);
        Assert.Contains("DXVK_HDR=1", withReno, StringComparison.Ordinal);
        Assert.Contains("dxgi=n,b", withReno, StringComparison.Ordinal);
        Assert.Equal(ComponentHealth.Installed, installed.Health);
        Assert.Equal(ComponentLifecycleState.NotInstalled, missing.Lifecycle);
    }

    [Fact]
    public async Task RemoveEachComponentIndependentlyPreservesOthersAndUnknownNeighbors()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var neighbor = temp.File("game/user-preset.ini", "keep me");
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.7.3"),
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-fixture.addon64"), "renodx-fixture.addon64", "snapshot"),
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);

        var removeReno = await planner.BuildRemovePlanAsync(game, ComponentKind.RenoDx);
        Assert.True((await executor.ExecuteAsync(removeReno, false)).Succeeded);
        Assert.False(File.Exists(Path.Combine(game.DeploymentDirectory, "renodx-fixture.addon64")));
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "dxgi.dll")));
        Assert.True(File.Exists(neighbor));

        var removeOpti = await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler);
        Assert.Contains("dxgi=n,b", removeOpti.LaunchOption, StringComparison.Ordinal);
        Assert.True((await executor.ExecuteAsync(removeOpti, false)).Succeeded);
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "dxgi.dll")));
        Assert.True(File.Exists(neighbor));
    }

    [Fact]
    public async Task RemoveReShadeWarnsWhenRenoDxDependsOnIt()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, new(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.7.3"),
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-fixture.addon64"), "renodx-fixture.addon64", "snapshot"),
            null, null)), false)).Succeeded);

        var plan = await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade);
        Assert.True(plan.RequiresConfirmation);
        Assert.Contains(plan.Warnings, warning => warning.Contains("RenoDX requires ReShade", StringComparison.Ordinal));
        Assert.Contains(plan.FileDecisions, decision =>
            decision.Reason.Contains("ManagedBySelectedComponent", StringComparison.Ordinal) ||
            decision.Reason.Contains("SharedManagedDependency", StringComparison.Ordinal));
    }

    [Fact]
    public void ButtonRowStylesProvideSharedSpacing()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhiLinux.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var styles = File.ReadAllText(Path.Combine(directory!.FullName, "src", "RhiLinux.Gui", "Styles.axaml"));
        var window = string.Concat(
            File.ReadAllText(Path.Combine(directory.FullName, "src", "RhiLinux.Gui", "MainWindow.axaml")),
            File.ReadAllText(Path.Combine(directory.FullName, "src", "RhiLinux.Gui", "Views", "GameDetailsView.axaml")));
        Assert.Contains("WrapPanel.button-row", styles, StringComparison.Ordinal);
        Assert.Contains("button-row-item", styles, StringComparison.Ordinal);
        Assert.Contains("Margin\" Value=\"0,0,8,8\"", styles, StringComparison.Ordinal);
        Assert.Contains("Classes=\"button-row\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowHdrGuidance", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Enable HDR through Proton", window, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchOptionStatus", window, StringComparison.Ordinal);
        Assert.Equal(1, window.Split("Click=\"CopyLaunch_Click\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BundleClassificationMarksOptionalAndDocumentation()
    {
        Assert.False(OptiScalerBundleParser.RequiresRepairWhenMissing("amd_fidelityfx_dx12.dll"));
        Assert.False(OptiScalerBundleParser.RequiresRepairWhenMissing("readme.txt"));
        Assert.False(OptiScalerBundleParser.RequiresRepairWhenMissing("setup_linux.sh"));
        Assert.True(OptiScalerBundleParser.RequiresRepairWhenMissing("OptiScaler.dll"));
        Assert.True(OptiScalerBundleParser.RequiresRepairWhenMissing("dxgi.dll"));
        Assert.Equal(DeploymentFileRequirement.Optional,
            OptiScalerBundleParser.RequirementFor("libxess.dll"));
        Assert.Equal(DeploymentFileRequirement.Documentation,
            OptiScalerBundleParser.RequirementFor("Licenses/LICENSE.txt"));
    }

    private static ComponentStateReport Report(
        ComponentKind component,
        OwnershipHealth ownership,
        ComponentLifecycleState state,
        IReadOnlyList<string> files,
        string? version) =>
        new(component, state, RuntimeHealth.Healthy, FileIntegrityStatus.Complete, ConfigurationHealth.Valid,
            ownership, UpdateAvailability.Unknown, CompatibilityStatus.Compatible, version, "fixture", null,
            new ComponentStateEvidence(files, files.Select(Path.GetFileName).OfType<string>().ToArray(), [], [],
                files.FirstOrDefault(), component, PeArchitecture.X64, version, null, null, "fixture"));

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new(42, "Fixture", temp.Path, temp.Path, root, temp.Combine("pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]);
    }
}
