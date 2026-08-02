using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class CoexistenceLifecycleTests
{
    [Fact]
    public async Task StandaloneReShadeAndRenoUseActiveProxyWithoutCoexistenceName()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade64.dll", "reshade.me"), "ReShade64.dll", "6.7.3"),
            new(ComponentKind.RenoDx, temp.Pe("stage/game.addon64"), "game.addon64", "snapshot"), null, null);

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Copy && x.Target.EndsWith("dxgi.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Operations, x => x.Type == DeploymentOperationType.Copy && x.Target.EndsWith("ReShade64.dll", StringComparison.Ordinal));
        Assert.Contains("dxgi=n,b", DeploymentPlanner.GenerateLaunchOption("dxgi.dll"), StringComparison.Ordinal);

        Assert.True((await new DeploymentExecutor().ExecuteAsync(plan, false)).Succeeded);
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll")));
    }

    [Fact]
    public async Task StandaloneOptiScalerDryRunUsesProxyWithoutInventingReShade()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var template = temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadAsiPlugins=auto\nLoadReshade=auto\n");
        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, new(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.7.7"),
            [new(ComponentKind.OptiScaler, template, "OptiScaler.ini", "v0.7.7")]));

        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Copy && x.Target.EndsWith("dxgi.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Operations, x => x.Target.EndsWith("ReShade64.dll", StringComparison.Ordinal));
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.WriteIniValue &&
            x.Value == "Plugins:LoadReshade=false");
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.WriteIniValue &&
            x.Value == "Plugins:LoadAsiPlugins=false");
        Assert.True((await new DeploymentExecutor().ExecuteAsync(plan, true)).Succeeded);
    }

    [Fact]
    public async Task OptiScalerOnlyPlanPreservesUnownedReShade64DllItNeverUses()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var foreign = temp.PeWithMarker("game/ReShade64.dll", "foreign reshade");
        var originalHash = await ArtifactDownloader.Sha256Async(foreign);
        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, new(null, null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"),
                "OptiScaler.dll", "v0.9.4")));

        Assert.DoesNotContain(plan.Operations, operation =>
            operation.Target.Equals(foreign, StringComparison.Ordinal) ||
            operation.Source?.Equals(foreign, StringComparison.Ordinal) == true);
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(originalHash, await ArtifactDownloader.Sha256Async(foreign));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    [Fact]
    public async Task StandaloneReShadePlanPreservesUnownedReShade64DllWithoutAbsentVerification()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var foreign = temp.PeWithMarker("game/ReShade64.dll", "foreign reshade");
        var originalHash = await ArtifactDownloader.Sha256Async(foreign);
        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, new(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"),
                "ReShade64.dll", "6.7.3"), null, null));

        Assert.DoesNotContain(plan.Operations, operation =>
            operation.Target.Equals(foreign, StringComparison.Ordinal));
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(originalHash, await ArtifactDownloader.Sha256Async(foreign));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    [Fact]
    public async Task OptiScalerAfterManagedReShadeMovesOnlyManagedProxy()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, temp.Pe("stage/ReShade.dll"), "ReShade.dll", "6.7.3")), false)).Succeeded);

        var plan = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.7.7"));

        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Move &&
            x.Source!.EndsWith("dxgi.dll", StringComparison.Ordinal) && x.Target.EndsWith("ReShade64.dll", StringComparison.Ordinal));
        var result = await executor.ExecuteAsync(plan, false);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("true", IniDocument.Parse(File.ReadAllText(Path.Combine(game.GameRoot, "OptiScaler.ini")))
            .Get("Plugins", "LoadReshade"));
    }

    [Fact]
    public async Task ReShadeAndRenoAfterOptiScalerKeepOptiProxyAndConfigureChaining()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.7.7")), false)).Succeeded);
        var optiHash = await ArtifactDownloader.Sha256Async(Path.Combine(game.GameRoot, "dxgi.dll"));

        var plan = await planner.BuildRecommendedStackPlanAsync(game, new(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade64.dll", "reshade.me"), "ReShade64.dll", "6.7.3"),
            new(ComponentKind.RenoDx, temp.Pe("stage/reno.addon64"), "reno.addon64", "snapshot"), null, null));

        Assert.DoesNotContain(plan.Operations, x => x.Type is DeploymentOperationType.Copy or DeploymentOperationType.Move &&
            x.Target.EndsWith("dxgi.dll", StringComparison.Ordinal));
        Assert.True((await executor.ExecuteAsync(plan, false)).Succeeded);
        Assert.Equal(optiHash, await ArtifactDownloader.Sha256Async(Path.Combine(game.GameRoot, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll")));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "reno.addon64")));
    }

    [Fact]
    public async Task ChangedManagedReShadeProxyIsReplacedBeforeItMovesIntoChaining()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var installed = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("stage/installed/ReShade.dll", "reshade.me installed"),
            "ReShade64.dll", "6.7.2");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, installed), false)).Succeeded);
        var proxy = Path.Combine(game.GameRoot, "dxgi.dll");
        await File.AppendAllTextAsync(proxy, "changed after install");
        var changedHash = await ArtifactDownloader.Sha256Async(proxy);
        var replacement = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("stage/replacement/ReShade.dll", "reshade.me replacement"),
            "ReShade64.dll", "6.7.3");
        var opti = new ComponentArtifact(ComponentKind.OptiScaler,
            temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4");

        await Assert.ThrowsAsync<DeploymentConflictException>(() =>
            planner.BuildRecommendedStackPlanAsync(game, new(null, null, opti)));
        var plan = await planner.BuildRecommendedStackPlanAsync(game, new(replacement, null, opti));
        var replacementIndex = plan.Operations.FindIndex(operation =>
            operation.Type == DeploymentOperationType.Copy && operation.Target.Equals(proxy, StringComparison.Ordinal) &&
            operation.Component == ComponentKind.ReShade);
        var moveIndex = plan.Operations.FindIndex(operation => operation.Type == DeploymentOperationType.Move &&
            operation.Source?.Equals(proxy, StringComparison.Ordinal) == true);

        Assert.True(plan.RequiresRepair);
        Assert.True(replacementIndex >= 0 && replacementIndex < moveIndex);
        var result = await executor.ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        var coexist = Path.Combine(game.GameRoot, "ReShade64.dll");
        Assert.NotEqual(changedHash, await ArtifactDownloader.Sha256Async(coexist));
        Assert.Equal(await ArtifactDownloader.Sha256Async(replacement.Path),
            await ArtifactDownloader.Sha256Async(coexist));
    }

    [Fact]
    public async Task RemovingReShadeKeepsOptiScalerAndDisablesChaining()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var install = await planner.BuildRecommendedStackPlanAsync(game, new(
            new(ComponentKind.ReShade, temp.Pe("stage/ReShade64.dll"), "ReShade64.dll", "6.7.3"),
            new(ComponentKind.RenoDx, temp.Pe("stage/reno.addon64"), "reno.addon64", "snapshot"),
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.7.7")));
        Assert.True((await executor.ExecuteAsync(install, false)).Succeeded);

        Assert.True((await executor.ExecuteAsync(await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade), false)).Succeeded);

        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "reno.addon64")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "plugins", "OptiPatcher.asi")));
        Assert.Null(IniDocument.Parse(File.ReadAllText(Path.Combine(game.GameRoot, "OptiScaler.ini")))
            .Get("Plugins", "LoadReshade"));
    }

    [Fact]
    public async Task ManagedAndForeignLoneCoexistenceLayoutsAreNotStandaloneInstalled()
    {
        using var managedTemp = new TestDirectory();
        var managedGame = Game(managedTemp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(managedGame,
            new(ComponentKind.ReShade, managedTemp.Pe("stage/ReShade.dll"), "ReShade.dll", "6.7.3")), false)).Succeeded);
        File.Move(Path.Combine(managedGame.GameRoot, "dxgi.dll"), Path.Combine(managedGame.GameRoot, "ReShade64.dll"));
        var managedStatus = Assert.Single(await new ComponentDetector().DetectAsync(managedGame), x => x.Component == ComponentKind.ReShade);
        Assert.Equal(ComponentHealth.IncorrectlyConfigured, managedStatus.Health);

        using var foreignTemp = new TestDirectory();
        var foreignGame = Game(foreignTemp);
        foreignTemp.PeWithMarker("game/ReShade64.dll", "reshade.me");
        var foreignStatus = Assert.Single(await new ComponentDetector().DetectAsync(foreignGame), x => x.Component == ComponentKind.ReShade);
        Assert.Equal(ComponentHealth.ForeignInstallation, foreignStatus.Health);
        await Assert.ThrowsAsync<DeploymentConflictException>(() => planner.BuildRecommendedStackPlanAsync(foreignGame, new(
            new(ComponentKind.ReShade, foreignTemp.Pe("stage/ReShade.dll"), "ReShade.dll", "6.7.3"), null,
            new(ComponentKind.OptiScaler, foreignTemp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.7.7"), null)));
    }

    [Fact]
    public async Task RepairRestoresStandaloneReShadeFromRecoverableCoexistenceSlot()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var installedSource = temp.PeWithMarker("stage/installed/ReShade64.dll", "reshade.me installed");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, installedSource, "ReShade64.dll", "6.7.2")), false)).Succeeded);
        var activeProxy = Path.Combine(game.GameRoot, "dxgi.dll");
        var coexist = Path.Combine(game.GameRoot, "ReShade64.dll");
        File.Move(activeProxy, coexist);

        var broken = Assert.Single(await new ComponentDetector().DetectAsync(game),
            status => status.Component == ComponentKind.ReShade);
        Assert.Equal(ComponentHealth.IncorrectlyConfigured, broken.Health);

        var repairSource = temp.PeWithMarker("stage/repair/ReShade64.dll", "reshade.me repaired");
        var repair = await planner.BuildRecommendedStackPlanAsync(game, new(
            new(ComponentKind.ReShade, repairSource, "ReShade64.dll", "6.7.3"), null, null));

        Assert.True(repair.RequiresRepair);
        Assert.Contains(repair.Operations, operation =>
            operation.Type == DeploymentOperationType.DeleteVerifiedFile &&
            operation.Target.Equals(coexist, StringComparison.Ordinal));
        var result = await executor.ExecuteAsync(repair, false);
        Assert.True(result.Succeeded, result.Error);

        Assert.True(File.Exists(activeProxy));
        Assert.False(File.Exists(coexist));
        Assert.Equal(await ArtifactDownloader.Sha256Async(repairSource),
            await ArtifactDownloader.Sha256Async(activeProxy));
        var repaired = Assert.Single(await new ComponentDetector().DetectAsync(game),
            status => status.Component == ComponentKind.ReShade);
        Assert.Equal(ComponentHealth.Installed, repaired.Health);
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        var managedReShade = Assert.Single(manifest.Files, file =>
            file.Component == ComponentKind.ReShade &&
            file.RelativePath.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("6.7.3", managedReShade.Version);
    }

    [Fact]
    public async Task FailedFinalVerificationRollsBackReShadeRenameAndOptiCopy()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var reshadeSource = temp.Pe("stage/ReShade.dll");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, reshadeSource, "ReShade.dll", "6.7.3")), false)).Succeeded);
        var originalHash = await ArtifactDownloader.Sha256Async(Path.Combine(game.GameRoot, "dxgi.dll"));
        var plan = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.7.7"));
        plan.Operations.Insert(plan.Operations.Count - 1,
            new(DeploymentOperationType.VerifyFileState, Path.Combine(game.GameRoot, "impossible.dll"), Value: "exists"));

        var result = await executor.ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(originalHash, await ArtifactDownloader.Sha256Async(Path.Combine(game.GameRoot, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll")));
    }

    [Fact]
    public async Task ManifestWriteFailureRollsBackCompletedDeployment()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        File.WriteAllText(Path.Combine(game.GameRoot, ".rhi-linux"), "blocks metadata directory");
        var plan = await new DeploymentPlanner().BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, temp.Pe("stage/ReShade.dll"), "ReShade.dll", "6.7.3"));

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new(900001, "Fixture", temp.Path, temp.Path, root, temp.Combine("compatdata", "900001", "pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, new FileInfo(executable).Length, ["fixture"])]);
    }
}
