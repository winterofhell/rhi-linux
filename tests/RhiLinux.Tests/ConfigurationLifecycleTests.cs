using System.Text;
using RhiLinux.Core;
using RhiLinux.Gui;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class ConfigurationLifecycleTests
{
    private const string OptiIni = "[Upscalers]\nDx11Upscaler=auto\nDx12Upscaler=auto\nVulkanUpscaler=auto\n" +
        "[Inputs]\nEnableDlssInputs=auto\n[FrameGen]\nEnabled=auto\nFGInput=auto\nFGOutput=auto\n" +
        "[Plugins]\nLoadReshade=false\nLoadAsiPlugins=false\n[User]\nCustom=keep\n";

    [Fact]
    public async Task SettingsDryRunPreservesGameAndManifestBytes()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var path = Path.Combine(game.DeploymentDirectory, "OptiScaler.ini");
        var original = await File.ReadAllBytesAsync(path);
        var manifestPath = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var manifest = await File.ReadAllBytesAsync(manifestPath);
        var settings = await OptiScalerSettingsService.ReadAsync(game);

        var plan = await new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game,
            settings with { Dx12Upscaler = "fsr22", DlssInputs = "true" });
        var result = await new DeploymentExecutor().ExecuteAsync(plan, true);

        Assert.True(result.Succeeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(manifestPath));
        Assert.All(plan.Operations.Where(operation => operation.BackupPath is not null),
            operation => Assert.False(File.Exists(operation.BackupPath)));
    }

    [Fact]
    public async Task SettingsKeepUserValuesAndCoexistenceAndBackUpOriginalBytes()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var path = Path.Combine(game.DeploymentDirectory, "OptiScaler.ini");
        var original = await File.ReadAllBytesAsync(path);
        var settings = await OptiScalerSettingsService.ReadAsync(game);
        var plan = await new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game,
            settings with { Dx12Upscaler = "fsr22", DlssInputs = "true" });

        Assert.True((await new DeploymentExecutor().ExecuteAsync(plan, false)).Succeeded);

        var document = IniDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal("fsr22", document.Get("Upscalers", "Dx12Upscaler"));
        Assert.Equal("true", document.Get("Inputs", "EnableDlssInputs"));
        Assert.Equal("keep", document.Get("User", "Custom"));
        Assert.Equal("true", document.Get("Plugins", "LoadReshade"));
        Assert.Equal("true", document.Get("Plugins", "LoadAsiPlugins"));
        var backup = plan.Operations.First(operation => operation.BackupPath is not null).BackupPath!;
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "renodx-fixture.addon64")));
    }

    [Fact]
    public async Task FailedSettingsVerificationRollsBackConfigurationAndBackup()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var path = Path.Combine(game.DeploymentDirectory, "OptiScaler.ini");
        var original = await File.ReadAllBytesAsync(path);
        var settings = await OptiScalerSettingsService.ReadAsync(game);
        var plan = await new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game,
            settings with { Dx12Upscaler = "fsr22" });
        plan.Operations.Insert(plan.Operations.Count - 1,
            new(DeploymentOperationType.VerifyIniValue, path, Value: "Upscalers:Dx12Upscaler=impossible"));

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.All(plan.Operations.Where(operation => operation.BackupPath is not null),
            operation => Assert.False(File.Exists(operation.BackupPath)));
    }

    [Fact]
    public async Task FrameGenerationRequiresReleaseMatchedRuntimeBeforePlanIsReturned()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var settings = await OptiScalerSettingsService.ReadAsync(game);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game, settings with
            {
                FrameGeneration = "true",
                FrameGenerationInput = "upscaler",
                FrameGenerationOutput = "fsrfg"
            }));
        Assert.Contains("FidelityFX", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(settings, await OptiScalerSettingsService.ReadAsync(game));
    }

    [Fact]
    public async Task FrameGenerationWorksWithValidatedFidelityFxRuntime()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        temp.Pe("game/amd_fidelityfx_dx12.dll");
        var settings = await OptiScalerSettingsService.ReadAsync(game);
        var plan = await new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game, settings with
        {
            FrameGeneration = "true",
            FrameGenerationInput = "upscaler",
            FrameGenerationOutput = "fsrfg"
        });

        Assert.True((await new DeploymentExecutor().ExecuteAsync(plan, false)).Succeeded);
        var result = await OptiScalerSettingsService.ReadAsync(game);
        Assert.Equal("true", result.FrameGeneration);
        Assert.Equal("upscaler", result.FrameGenerationInput);
        Assert.Equal("fsrfg", result.FrameGenerationOutput);
    }

    [Fact]
    public async Task SettingsRefuseUnmanagedConfigurationAndModifiedRuntime()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var settings = await OptiScalerSettingsService.ReadAsync(game);
        await File.AppendAllTextAsync(Path.Combine(game.DeploymentDirectory, "dxgi.dll"), "foreign");

        await Assert.ThrowsAsync<DeploymentConflictException>(() =>
            new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game, settings with { Dx12Upscaler = "fsr22" }));

        var manifest = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        File.Delete(manifest);
        await Assert.ThrowsAsync<DeploymentConflictException>(() =>
            new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game, settings with { Dx12Upscaler = "fsr22" }));
    }

    [Fact]
    public async Task ConfigurationEditsKeepOriginalPreInstallationBackupForRemoval()
    {
        using var temp = new TestDirectory();
        var originalPath = temp.File("game/OptiScaler.ini", OptiIni);
        var original = await File.ReadAllBytesAsync(originalPath);
        var game = await InstallAsync(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var originalBackup = (await ComponentDetector.LoadManifestAsync(game.GameRoot)).Files
            .Single(file => Path.GetFileName(file.RelativePath) == "OptiScaler.ini").BackupRelativePath;
        Assert.NotNull(originalBackup);
        foreach (var mode in new[] { "fsr22", "fsr31" })
        {
            var settings = await OptiScalerSettingsService.ReadAsync(game);
            var plan = await planner.BuildOptiScalerSettingsPlanAsync(game, settings with { Dx12Upscaler = mode });
            Assert.True((await executor.ExecuteAsync(plan, false)).Succeeded);
            var record = (await ComponentDetector.LoadManifestAsync(game.GameRoot)).Files
                .Single(file => Path.GetFileName(file.RelativePath) == "OptiScaler.ini");
            Assert.Equal(originalBackup, record.BackupRelativePath);
        }
        var removal = await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler);
        Assert.True((await executor.ExecuteAsync(removal, false)).Succeeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(originalPath));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    [Fact]
    public async Task SettingsEditsDoNotTurnNewConfigurationIntoAnOriginalBackup()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var settings = await OptiScalerSettingsService.ReadAsync(game);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var plan = await planner.BuildOptiScalerSettingsPlanAsync(game, settings with { Dx12Upscaler = "fsr22" });

        Assert.True((await executor.ExecuteAsync(plan, false)).Succeeded);
        var record = (await ComponentDetector.LoadManifestAsync(game.GameRoot)).Files
            .Single(file => Path.GetFileName(file.RelativePath) == "OptiScaler.ini");
        Assert.Null(record.BackupRelativePath);
        Assert.True((await executor.ExecuteAsync(await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler), false)).Succeeded);
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "OptiScaler.ini")));
    }

    [Theory]
    [InlineData("dlss")]
    [InlineData("../../../outside")]
    public async Task UnsupportedUpscalerIsRejectedBeforeModification(string mode)
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var settings = await OptiScalerSettingsService.ReadAsync(game);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new DeploymentPlanner().BuildOptiScalerSettingsPlanAsync(game, settings with { Dx12Upscaler = mode }));
        Assert.Equal(settings, await OptiScalerSettingsService.ReadAsync(game));
    }

    [Fact]
    public async Task PresetImportAndResetPreserveRenoDxAndPreviousPreset()
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var source = temp.File("presets/custom.ini", "Techniques=Clarity@Clarity.fx\nTechniqueSorting=Clarity@Clarity.fx\n[Clarity.fx]\nStrength=0.75\n");
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var imported = await planner.BuildReShadePresetPlanAsync(game, source);
        Assert.True((await executor.ExecuteAsync(imported, true)).Succeeded);
        Assert.NotEqual(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(ReShadePresetService.CleanPresetPath(game)));
        Assert.True((await executor.ExecuteAsync(imported, false)).Succeeded);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(ReShadePresetService.CleanPresetPath(game)));

        var reset = await planner.BuildReShadePresetPlanAsync(game, null);
        Assert.True((await executor.ExecuteAsync(reset, false)).Succeeded);

        var document = IniDocument.Parse(await File.ReadAllBytesAsync(ReShadePresetService.CleanPresetPath(game)));
        Assert.Equal(string.Empty, document.Get(string.Empty, "Techniques"));
        Assert.Equal("0.75", document.Get("Clarity.fx", "Strength"));
        var backup = reset.Operations.First(operation => operation.BackupPath is not null).BackupPath!;
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(backup));
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "renodx-fixture.addon64")));
        Assert.True(File.Exists(Path.Combine(game.DeploymentDirectory, "ReShade64.dll")));
    }

    [Theory]
    [InlineData("[GENERAL]\nNotPreset=1\n")]
    [InlineData("Techniques=Bad@../escape.fx\n")]
    [InlineData("Techniques=Bad@C:\\outside.fx\n")]
    [InlineData("Techniques=NoShaderName\n")]
    [InlineData("Techniques=Bad@Shader.fx\n[Broken\n")]
    public async Task PresetImportRejectsInvalidDataWithoutChangingGame(string contents)
    {
        using var temp = new TestDirectory();
        var game = await InstallAsync(temp);
        var original = await File.ReadAllBytesAsync(ReShadePresetService.CleanPresetPath(game));
        var source = temp.File("presets/invalid.ini", contents);

        await Assert.ThrowsAsync<InvalidDataException>(() => new DeploymentPlanner().BuildReShadePresetPlanAsync(game, source));
        Assert.Equal(original, await File.ReadAllBytesAsync(ReShadePresetService.CleanPresetPath(game)));
    }

    [Fact]
    public async Task PresetImportRejectsOversizedAndInvalidUtf8Files()
    {
        using var temp = new TestDirectory();
        var source = temp.File("oversized.ini", "Techniques=\n");
        using (var stream = File.OpenWrite(source)) stream.SetLength(4 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReShadePresetService.ReadValidatedPresetAsync(source));
        await File.WriteAllBytesAsync(source, [0xc3, 0x28]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => ReShadePresetService.ReadValidatedPresetAsync(source));
    }

    [Fact]
    public void SettingsEditorPreservesUnknownExistingValues()
    {
        var settings = new OptiScalerSettings("future", "fsr22", "auto", "auto", "auto", "auto", "auto");
        var editor = new OptiScalerSettingsViewModel(settings);
        Assert.Equal(settings, editor.ToSettings());
        editor.Dx12 = editor.Dx12Choices.Single(choice => choice.Value == "fsr31");
        Assert.Equal("future", editor.ToSettings().Dx11Upscaler);
        Assert.Equal("fsr31", editor.ToSettings().Dx12Upscaler);
    }

    [Theory]
    [InlineData("configure OptiScaler", "Save settings")]
    [InlineData("import ReShade preset", "Import preset")]
    [InlineData("reset ReShade preset", "Reset preset")]
    public void ConfigurationPlanDialogShowsCorrectActionAndBlocksRepeatExecution(string action, string label)
    {
        var dialog = new DeploymentPlanDialogViewModel(new DeploymentPlan
        {
            Id = "fixture",
            InstallId = "fixture",
            GameRoot = "game",
            DeploymentDirectory = "game",
            Action = action
        });
        Assert.Equal(label, dialog.ActionButtonText);
        Assert.Equal("Configuration plan", dialog.PlanSectionTitle);
        dialog.Begin();
        Assert.False(dialog.CanExecute);
        Assert.False(dialog.CanClose);
        dialog.EndPreparation();
        Assert.True(dialog.CanExecute);
    }

    private static async Task<DeploymentTarget> InstallAsync(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var exe = temp.Pe("game/Fixture.exe");
        var game = new SteamGame(424242, "Fixture", temp.Path, temp.Path, root, temp.Combine("pfx"), exe, root,
            DetectionConfidence.High, "fixture", GameEngine.Unity,
            [new(exe, 100, DetectionConfidence.High, PeArchitecture.X64, 2 * 1024 * 1024, [])]).ToDeploymentTarget();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.PeWithMarker("stage/ReShade.dll", "reshade.me"), "ReShade.dll", "6.8.0"),
            new(ComponentKind.RenoDx, temp.Pe("stage/renodx-fixture.addon64"), "renodx-fixture.addon64", "snapshot"),
            new(ComponentKind.OptiScaler, temp.PeWithMarker("stage/OptiScaler.dll", "OptiScaler"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, temp.File("stage/OptiScaler.ini", OptiIni), "OptiScaler.ini", "v0.9.4")]);
        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);
        Assert.True(result.Succeeded, result.Error);
        return game;
    }
}
