using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class ReShadePresetTests
{
    [Fact]
    public async Task FreshStandaloneReShadeEnablesNoOrdinaryTechniques()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifact = new ComponentArtifact(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0");

        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, artifact), false)).Succeeded);

        var preset = Path.Combine(game.DeploymentDirectory, ReShadePresetService.CleanPresetFileName);
        var host = Path.Combine(game.DeploymentDirectory, ReShadePresetService.HostIniFileName);
        Assert.True(File.Exists(preset));
        Assert.True(File.Exists(host));
        var techniques = IniDocument.Parse(await File.ReadAllTextAsync(preset)).Get(string.Empty, "Techniques");
        Assert.True(ReShadePresetService.IsCleanTechniquesValue(techniques));
        Assert.Empty(ReShadePresetService.ParseTechniqueNames(techniques));
        foreach (var effect in ReShadePresetService.PreviouslyObservedDefaultEffects)
            Assert.DoesNotContain(effect, techniques ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var hostIni = IniDocument.Parse(await File.ReadAllTextAsync(host));
        Assert.Equal($".\\{ReShadePresetService.CleanPresetFileName}", hostIni.Get("GENERAL", "PresetPath"));
        Assert.Equal(".", hostIni.Get("ADDON", "AddonPath"));
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        Assert.Contains(manifest.Files, file => file.Purpose == ReShadePresetService.CleanPresetPurpose);
        Assert.Contains(manifest.Files, file => file.Purpose == ReShadePresetService.HostConfigPurpose);
    }

    [Fact]
    public async Task FreshReShadePlusRenoDxKeepsAddonPathAndCleanPreset()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0"),
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-unrealengine.addon64"), "renodx-unrealengine.addon64", "snapshot"),
            null);

        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "renodx-unrealengine.addon64")));
        Assert.Equal(".", IniDocument.Parse(await File.ReadAllTextAsync(Path.Combine(game.DeploymentDirectory, "ReShade.ini")))
            .Get("ADDON", "AddonPath"));
        Assert.Empty(ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(Path.Combine(game.DeploymentDirectory, "ReShadePreset.ini")))
                .Get(string.Empty, "Techniques")));
    }

    [Fact]
    public async Task OptiScalerStackUsesCleanPreset()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0"),
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-unrealengine.addon64"), "renodx-unrealengine.addon64", "snapshot"),
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);

        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        Assert.Empty(ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(Path.Combine(game.DeploymentDirectory, "ReShadePreset.ini")))
                .Get(string.Empty, "Techniques")));
        Assert.Equal(".", IniDocument.Parse(await File.ReadAllTextAsync(Path.Combine(game.DeploymentDirectory, "ReShade.ini")))
            .Get("ADDON", "AddonPath"));
    }

    [Fact]
    public async Task UserSelectedTechniquesSurviveUpdate()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var first = new ComponentArtifact(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, first), false)).Succeeded);

        var preset = Path.Combine(game.DeploymentDirectory, ReShadePresetService.CleanPresetFileName);
        await File.WriteAllTextAsync(preset, "Techniques=CAS,LumaSharpen\nTechniqueSorting=CAS,LumaSharpen\n");
        var second = new ComponentArtifact(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade2.dll", "reshade.me"), "ReShade.dll", "6.8.1");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, second), false)).Succeeded);

        var techniques = ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(preset)).Get(string.Empty, "Techniques"));
        Assert.Equal(["CAS", "LumaSharpen"], techniques);
    }

    [Fact]
    public async Task UserPresetSurvivesRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifact = new ComponentArtifact(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, artifact), false)).Succeeded);
        var preset = Path.Combine(game.DeploymentDirectory, ReShadePresetService.CleanPresetFileName);
        await File.WriteAllTextAsync(preset, "Techniques=Vibrance\n");

        Assert.True((await executor.ExecuteAsync(
            await planner.BuildRepairPlanAsync(game, ComponentKind.ReShade, artifact), false)).Succeeded);
        Assert.Equal(["Vibrance"], ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(preset)).Get(string.Empty, "Techniques")));
    }

    [Fact]
    public async Task MissingManagedPresetIsRecreated()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifact = new ComponentArtifact(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, artifact), false)).Succeeded);
        var preset = Path.Combine(game.DeploymentDirectory, ReShadePresetService.CleanPresetFileName);
        File.Delete(preset);

        Assert.True((await executor.ExecuteAsync(
            await planner.BuildRepairPlanAsync(game, ComponentKind.ReShade, artifact), false)).Succeeded);
        Assert.True(File.Exists(preset));
        Assert.Empty(ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(preset)).Get(string.Empty, "Techniques")));
    }

    [Fact]
    public async Task RemovingOptiScalerDoesNotResetReShadeShaders()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0"),
            null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler,
                temp.File("stage/OptiScaler.ini", "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n"),
                "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        var preset = Path.Combine(game.DeploymentDirectory, ReShadePresetService.CleanPresetFileName);
        await File.WriteAllTextAsync(preset, "Techniques=Deband\n");

        Assert.True((await executor.ExecuteAsync(await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler), false)).Succeeded);
        Assert.Equal(["Deband"], ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(preset)).Get(string.Empty, "Techniques")));
    }

    [Fact]
    public async Task RemovingRenoDxDoesNotResetUnrelatedShaders()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0"),
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-unrealengine.addon64"), "renodx-unrealengine.addon64", "snapshot"),
            null);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        var preset = Path.Combine(game.DeploymentDirectory, ReShadePresetService.CleanPresetFileName);
        await File.WriteAllTextAsync(preset, "Techniques=Tonemap\n");

        Assert.True((await executor.ExecuteAsync(await planner.BuildRemovePlanAsync(game, ComponentKind.RenoDx), false)).Succeeded);
        Assert.Equal(["Tonemap"], ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(preset)).Get(string.Empty, "Techniques")));
    }

    [Fact]
    public async Task ObservedDefaultEffectsAreClearedOnFreshManagedInstall()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        Directory.CreateDirectory(game.DeploymentDirectory);
        await File.WriteAllTextAsync(Path.Combine(game.DeploymentDirectory, "ReShadePreset.ini"),
            "Techniques=SMAA,Clarity\nTechniqueSorting=SMAA,Clarity\n");
        await File.WriteAllTextAsync(Path.Combine(game.DeploymentDirectory, "ReShade.ini"),
            "[GENERAL]\nPresetPath=.\\ReShadePreset.ini\n");
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var artifact = new ComponentArtifact(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0");

        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, artifact), false)).Succeeded);
        Assert.Empty(ReShadePresetService.ParseTechniqueNames(
            IniDocument.Parse(await File.ReadAllTextAsync(Path.Combine(game.DeploymentDirectory, "ReShadePreset.ini")))
                .Get(string.Empty, "Techniques")));
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new SteamGame(42, "Fixture", temp.Path, temp.Path, root, temp.Combine("pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unreal,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64, 1024, ["fixture"])]);
    }
}
