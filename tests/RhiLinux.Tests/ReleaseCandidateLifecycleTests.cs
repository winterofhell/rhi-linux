using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class ReleaseCandidateLifecycleTests
{
    [Fact]
    public async Task CompleteStackInstallUpdateRepairRemoveAndReinstallCyclePreservesGameFiles()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var gameFidelityFx = temp.PeWithMarker("game/amd_fidelityfx_dx12.dll", "AMD FidelityFX SDK game file");
        var gameFidelityFxHash = await ArtifactDownloader.Sha256Async(gameFidelityFx);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();

        var installPlan = await planner.BuildRecommendedStackPlanAsync(game, Artifacts(temp, "v0.9.3", "first"));
        Assert.Contains(installPlan.FileDecisions, decision =>
            decision.DestinationPath == gameFidelityFx &&
            decision.Action == DeploymentFileAction.PreserveExisting);
        Assert.True((await executor.ExecuteAsync(installPlan, false)).Succeeded);
        await AssertInstalledAsync(game, "v0.9.3");
        Assert.Equal(gameFidelityFxHash, await ArtifactDownloader.Sha256Async(gameFidelityFx));

        var updatePlan = await planner.BuildRecommendedStackPlanAsync(game, Artifacts(temp, "v0.9.4", "second"));
        Assert.Contains("update", updatePlan.Action, StringComparison.OrdinalIgnoreCase);
        Assert.True((await executor.ExecuteAsync(updatePlan, false)).Succeeded);
        await AssertInstalledAsync(game, "v0.9.4");

        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        var optiProxy = manifest.Files.Single(file => file.Component == ComponentKind.OptiScaler &&
            DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase));
        File.Delete(Path.Combine(game.GameRoot, optiProxy.RelativePath));
        var repairPlan = await planner.BuildRecommendedStackPlanAsync(game, Artifacts(temp, "v0.9.4", "second"));
        Assert.True(repairPlan.RequiresRepair);
        Assert.Contains("repair", repairPlan.Action, StringComparison.OrdinalIgnoreCase);
        Assert.True((await executor.ExecuteAsync(repairPlan, false)).Succeeded);
        await AssertInstalledAsync(game, "v0.9.4");

        Assert.True((await executor.ExecuteAsync(await planner.BuildRemoveStackPlanAsync(game), false)).Succeeded);
        Assert.Empty((await ComponentDetector.LoadManifestAsync(game.GameRoot)).Files);
        Assert.Equal(gameFidelityFxHash, await ArtifactDownloader.Sha256Async(gameFidelityFx));

        Assert.True((await executor.ExecuteAsync(
            await planner.BuildRecommendedStackPlanAsync(game, Artifacts(temp, "v0.9.4", "second")), false)).Succeeded);
        await AssertInstalledAsync(game, "v0.9.4");
        Assert.Equal(gameFidelityFxHash, await ArtifactDownloader.Sha256Async(gameFidelityFx));
    }

    private static RecommendedStackArtifacts Artifacts(TestDirectory temp, string version, string marker)
    {
        var reshade = temp.PeWithMarker($"stage/{version}/ReShade64.dll", $"reshade.me {marker}");
        var reno = temp.PeWithMarker($"stage/{version}/renodx-fixture.addon64", $"RenoDX {marker}");
        var opti = temp.PeWithMarker($"stage/{version}/OptiScaler.dll", $"OptiScaler {marker}");
        var ini = temp.File($"stage/{version}/OptiScaler.ini",
            "[Plugins]\nLoadReshade=auto\nLoadAsiPlugins=auto\n[Quality]\nSharpness=0.5\n");
        var fidelityFx = temp.PeWithMarker($"stage/{version}/amd_fidelityfx_dx12.dll", $"OptiScaler FidelityFX {marker}");
        return new(
            new(ComponentKind.ReShade, reshade, "ReShade64.dll", version),
            new(ComponentKind.RenoDx, reno, "renodx-fixture.addon64", version),
            new(ComponentKind.OptiScaler, opti, "OptiScaler.dll", version),
            [
                new(ComponentKind.OptiScaler, ini, "OptiScaler.ini", version,
                    RelativePath: "OptiScaler.ini",
                    Requirement: DeploymentFileRequirement.Required,
                    RequirementReason: "The selected release requires its matching configuration."),
                new(ComponentKind.OptiScaler, fidelityFx, "amd_fidelityfx_dx12.dll", version,
                    RelativePath: "amd_fidelityfx_dx12.dll",
                    Requirement: DeploymentFileRequirement.Conditional,
                    RequirementReason: "Release-matched DirectX 12 FidelityFX backend.",
                    Feature: "fidelityfx-dx12", CanOmitOnCollision: true)
            ]);
    }

    private static async Task AssertInstalledAsync(SteamGame game, string expectedVersion)
    {
        var statuses = await new ComponentDetector().DetectAsync(game);
        Assert.All(statuses, status => Assert.Equal(ComponentHealth.Installed, status.Health));
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        Assert.Contains(manifest.Files, file => file.Component == ComponentKind.ReShade && file.Version == expectedVersion);
        Assert.Contains(manifest.Files, file => file.Component == ComponentKind.RenoDx && file.Version == expectedVersion);
        Assert.Contains(manifest.Files, file => file.Component == ComponentKind.OptiScaler && file.Version == expectedVersion);
        Assert.DoesNotContain(manifest.Files, file => file.Component == ComponentKind.OptiPatcher);
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new(424242, "Lifecycle Fixture", temp.Path, temp.Path, root,
            temp.Combine("compatdata", "424242", "pfx"), executable, root,
            DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64,
                new FileInfo(executable).Length, ["fixture"])]);
    }
}
