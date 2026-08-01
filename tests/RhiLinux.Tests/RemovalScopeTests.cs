using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class RemovalScopeTests
{
    [Fact]
    public async Task CyberpunkLayoutCopyRemovesReShadeWithEditedFakeNvApiIni()
    {
        using var temp = new TestDirectory();
        var root = temp.Directory("Cyberpunk 2077");
        var deployment = temp.Directory("Cyberpunk 2077", "bin", "x64");
        var executable = temp.Pe("Cyberpunk 2077/bin/x64/Cyberpunk2077.exe");
        var game = new SteamGame(1091500, "Cyberpunk 2077", temp.Path, temp.Path, root,
            temp.Combine("compatdata", "1091500", "pfx"), executable, deployment, DetectionConfidence.High,
            "fixture Cyberpunk layout", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]);
        var (planner, executor) = await InstallStackAsync(temp, game);
        var fakeNvApi = Path.Combine(deployment, "fakenvapi.ini");
        await File.AppendAllTextAsync(fakeNvApi, "[User]\nAdapter=custom\n");

        var plan = await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade);
        var result = await executor.ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("Adapter=custom", await File.ReadAllTextAsync(fakeNvApi), StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Operations, operation => operation.Target.Equals(fakeNvApi, StringComparison.Ordinal));
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        var optiProxy = Assert.Single(manifest.Files, file => file.Component == ComponentKind.OptiScaler &&
            DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, optiProxy.RelativePath)));
        Assert.False(File.Exists(Path.Combine(deployment, "ReShade64.dll")));
    }

    [Fact]
    public async Task RemovingReShadeIgnoresModifiedFakeNvApiConfiguration()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var (planner, executor) = await InstallStackAsync(temp, game);
        var fakeNvApi = Path.Combine(game.GameRoot, "fakenvapi.ini");
        await File.AppendAllTextAsync(fakeNvApi, "[User]\nAdapter=custom\n");

        var result = await executor.ExecuteAsync(
            await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade), false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("Adapter=custom", await File.ReadAllTextAsync(fakeNvApi), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "renodx-fixture.addon64")));
        Assert.Equal(ComponentHealth.Installed, Assert.Single(await new ComponentDetector().DetectAsync(game),
            status => status.Component == ComponentKind.OptiScaler).Health);
    }

    [Fact]
    public async Task RemovingReShadePatchesOnlyOwnedOptiScalerKeysAndPreservesUserSettings()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var (planner, executor) = await InstallStackAsync(temp, game);
        var iniPath = Path.Combine(game.GameRoot, "OptiScaler.ini");
        await File.AppendAllTextAsync(iniPath, "[User]\nSharpness=0.73\n");

        var result = await executor.ExecuteAsync(
            await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade), false);

        Assert.True(result.Succeeded, result.Error);
        var text = await File.ReadAllTextAsync(iniPath);
        Assert.Contains("Sharpness=0.73", text, StringComparison.Ordinal);
        var ini = IniDocument.Parse(text);
        Assert.False(string.Equals("true", ini.Get("Plugins", "LoadReshade"), StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    [Fact]
    public async Task UnrelatedManagedRuntimeHashDriftDoesNotBlockReShadeRemoval()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var (planner, executor) = await InstallStackAsync(temp, game);
        await File.AppendAllTextAsync(Path.Combine(game.GameRoot, "amd_fidelityfx_dx12.dll"), "changed support runtime");

        var result = await executor.ExecuteAsync(
            await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade), false);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "amd_fidelityfx_dx12.dll")));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    [Fact]
    public async Task CorruptImmutableActiveProxyStillRequiresRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        await InstallStackAsync(temp, game);
        await File.WriteAllBytesAsync(Path.Combine(game.GameRoot, "dxgi.dll"), new byte[1024]);

        var status = Assert.Single(await new ComponentDetector().DetectAsync(game),
            item => item.Component == ComponentKind.OptiScaler);

        Assert.Equal(ComponentHealth.PartiallyInstalled, status.Health);
        Assert.Equal(InstallationVerification.RepairNeeded, status.Verification);
    }

    [Fact]
    public async Task RemovingOptiScalerPreservesUserEditedConfigurationInRecovery()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var (planner, executor) = await InstallStackAsync(temp, game);
        var iniPath = Path.Combine(game.GameRoot, "OptiScaler.ini");
        await File.AppendAllTextAsync(iniPath, "[User]\nPreset=custom\n");
        var plan = await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler);

        var result = await executor.ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        var recovery = Path.Combine(game.GameRoot, ".rhi-linux", "recovery", plan.Id,
            "OptiScaler.ini.changed");
        Assert.Contains("Preset=custom", await File.ReadAllTextAsync(recovery), StringComparison.Ordinal);
        Assert.False(File.Exists(iniPath));
    }

    [Fact]
    public async Task RemoveRescanAndReinstallReShadeKeepsOptiScalerHealthy()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var (planner, executor) = await InstallStackAsync(temp, game);
        Assert.True((await executor.ExecuteAsync(
            await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade), false)).Succeeded);
        Assert.Equal(ComponentHealth.Installed, Assert.Single(await new ComponentDetector().DetectAsync(game),
            status => status.Component == ComponentKind.OptiScaler).Health);

        var reshade = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("reinstall/ReShade64.dll", "ReShade reshade.me"), "ReShade64.dll", "6.7.3");
        var reinstall = await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, reshade), false);

        Assert.True(reinstall.Succeeded, reinstall.Error);
        var statuses = await new ComponentDetector().DetectAsync(game);
        Assert.Equal(ComponentHealth.Installed,
            Assert.Single(statuses, status => status.Component == ComponentKind.ReShade).Health);
        Assert.Equal(ComponentHealth.Installed,
            Assert.Single(statuses, status => status.Component == ComponentKind.OptiScaler).Health);
    }

    private static async Task<(DeploymentPlanner Planner, DeploymentExecutor Executor)> InstallStackAsync(
        TestDirectory temp,
        SteamGame game)
    {
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade64.dll", "ReShade reshade.me"),
                "ReShade64.dll", "6.7.3"),
            new(ComponentKind.RenoDx, temp.PeWithMarker("stage/renodx-fixture.addon64", "RenoDX"),
                "renodx-fixture.addon64", "snapshot"),
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"),
                "OptiScaler.dll", "v0.9.4"),
            [
                new(ComponentKind.OptiScaler,
                    temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                    "OptiScaler.ini", "v0.9.4"),
                new(ComponentKind.OptiScaler, temp.File("stage/fakenvapi.ini", "[Adapter]\nMode=auto\n"),
                    "fakenvapi.ini", "v0.9.4", RelativePath: "fakenvapi.ini",
                    Requirement: DeploymentFileRequirement.Conditional),
                new(ComponentKind.OptiScaler,
                    temp.PeWithMarker("stage/amd_fidelityfx_dx12.dll", "FidelityFX"),
                    "amd_fidelityfx_dx12.dll", "v0.9.4", RelativePath: "amd_fidelityfx_dx12.dll",
                    Requirement: DeploymentFileRequirement.Conditional)
            ]);
        var result = await executor.ExecuteAsync(
            await planner.BuildRecommendedStackPlanAsync(game, artifacts), false);
        Assert.True(result.Succeeded, result.Error);
        return (planner, executor);
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new(42, "Fixture", temp.Path, temp.Path, root, temp.Combine("compatdata", "42", "pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]);
    }
}
