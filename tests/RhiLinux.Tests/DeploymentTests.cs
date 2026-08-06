using RhiLinux.Core;
using RhiLinux.Mods;
using System.Text.Json;

namespace RhiLinux.Tests;

public sealed class DeploymentTests
{
    [Fact]
    public async Task DryRunDoesNotWriteGameFiles()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var source = temp.Pe("staging/ReShade64.dll");
        var plan = await new DeploymentPlanner().BuildInstallPlanAsync(game, new(ComponentKind.ReShade, source, "ReShade64.dll"));
        var result = await new DeploymentExecutor().ExecuteAsync(plan, true);
        Assert.True(result.Succeeded); Assert.False(File.Exists(Path.Combine(game.DeploymentDirectory, "dxgi.dll")));
    }

    [Fact]
    public async Task InstallCreatesOwnedManifestAndRemoveOnlyDeletesOwnedFile()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var source = temp.Pe("staging/ReShade64.dll");
        var planner = new DeploymentPlanner(); var install = await planner.BuildInstallPlanAsync(game, new(ComponentKind.ReShade, source, "ReShade64.dll", "1"));
        Assert.True((await new DeploymentExecutor().ExecuteAsync(install, false)).Succeeded);
        var installed = Path.Combine(game.DeploymentDirectory, "dxgi.dll"); Assert.True(File.Exists(installed)); Assert.True(File.Exists(Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json")));
        var remove = await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade); Assert.True((await new DeploymentExecutor().ExecuteAsync(remove, false)).Succeeded); Assert.False(File.Exists(installed));
    }

    [Fact]
    public async Task PlannerRejectsReShadeArchitectureMismatchBeforeCreatingAPlan()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var x86ReShade = new ComponentArtifact(ComponentKind.ReShade,
            temp.Pe("staging/ReShade32.dll", 0x014c), "ReShade32.dll", "6.7.3");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DeploymentPlanner().BuildInstallPlanAsync(game, x86ReShade));

        Assert.Contains("selected game is X64", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    [Fact]
    public async Task ComponentStateMismatchRollsBackBeforeManifestCommit()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var source = temp.Pe("staging/ReShade64.dll");
        var target = Path.Combine(game.DeploymentDirectory, "dxgi.dll");
        var plan = new DeploymentPlan
        {
            Id = "component-state-mismatch",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory,
            Action = "test component postcondition",
            Operations =
            {
                new(DeploymentOperationType.Copy, target, source, Component: ComponentKind.ReShade)
            },
            ExpectedComponentStates = [new(ComponentKind.RenoDx, true)]
        };

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Contains("RenoDx", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json")));
    }

    [Fact]
    public async Task IniBackupOwnershipRecordsItsCreationDigest()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var ini = temp.File("game/OptiScaler.ini", "[Game]\nKeep=1\n");
        var backup = Path.Combine(game.GameRoot, ".rhi-linux", "backups", "ini-digest", "OptiScaler.ini");
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(ini))).ToLowerInvariant();
        var plan = new DeploymentPlan
        {
            Id = "ini-digest",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory,
            Action = "test backup digest",
            Operations =
            {
                new(DeploymentOperationType.WriteIniValue, ini, Value: "Plugins:LoadReshade=true",
                    Component: ComponentKind.OptiScaler, BackupPath: backup)
            },
            ExpectedComponentStates = [new(ComponentKind.OptiScaler, true)]
        };

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        var manifest = JsonSerializer.Deserialize<GameManifest>(await File.ReadAllTextAsync(
            Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var record = Assert.Single(Assert.IsType<GameManifest>(manifest).Files);
        Assert.Equal(Path.GetRelativePath(game.GameRoot, backup), record.BackupRelativePath);
        Assert.Equal(originalHash, record.BackupSha256);
    }

    [Fact]
    public async Task InvalidExistingCopyTargetIsRejectedBeforeMetadataIsCreated()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var target = temp.File("game/occupied.dll", "foreign target");
        var plan = new DeploymentPlan
        {
            Id = "invalid-before-lock",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory,
            Action = "invalid hand-built plan",
            Operations =
            {
                new(DeploymentOperationType.Copy, target, temp.File("staging/source.dll", "source"),
                    Component: ComponentKind.ReShade)
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeploymentExecutor().ExecuteAsync(plan, false));

        Assert.Equal("foreign target", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(Path.Combine(game.GameRoot, ".rhi-linux")));
    }

    [Fact]
    public async Task PreservesUnknownForeignProxyAndUsesAlternative()
    {
        using var temp = new TestDirectory(); var game = Game(temp); temp.Pe("game/dxgi.dll", size: 3 * 1024 * 1024); var source = temp.Pe("staging/ReShade64.dll");
        var plan = await new DeploymentPlanner().BuildInstallPlanAsync(game, new(ComponentKind.ReShade, source, "ReShade64.dll"));
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Copy && x.Target.EndsWith("winmm.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Operations, x => x.Type is DeploymentOperationType.Copy or DeploymentOperationType.Backup &&
            x.Target.EndsWith("dxgi.dll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RollsBackCompletedOperationsWhenLaterCopyFails()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var source = temp.Pe("staging/good.dll");
        var first = Path.Combine(game.GameRoot, "first.dll"); var second = Path.Combine(game.GameRoot, "second.dll");
        var plan = new DeploymentPlan
        {
            Id = "rollback",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory,
            Action = "test",
            Operations = { new(DeploymentOperationType.Copy, first, source, Component: ComponentKind.ReShade), new(DeploymentOperationType.Copy, second, temp.Combine("missing.dll"), Component: ComponentKind.ReShade) }
        };
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);
        Assert.False(result.Succeeded); Assert.True(result.RolledBack); Assert.False(File.Exists(first));
    }

    [Fact]
    public async Task CopyReadFailureCleansPartialOutputRestoresBackupAndPersistsRollbackJournal()
    {
        if (!File.Exists("/proc/self/mem")) return;
        using var temp = new TestDirectory();
        var game = Game(temp);
        var target = temp.File("game/proxy.dll", "original game bytes");
        var original = await File.ReadAllBytesAsync(target);
        var backup = temp.Combine("game/.rhi-linux/backups/copy-read-failure/proxy.dll");
        var plan = new DeploymentPlan
        {
            Id = "copy-read-failure",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory,
            Action = "test failed copy rollback",
            Operations =
            {
                new(DeploymentOperationType.Backup, target, backup),
                new(DeploymentOperationType.Copy, target, "/proc/self/mem", Component: ComponentKind.ReShade),
                new(DeploymentOperationType.WriteManifest, Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json"))
            }
        };

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(backup));
        Assert.DoesNotContain(Directory.EnumerateFiles(game.GameRoot, "*.tmp", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains("proxy.dll", StringComparison.Ordinal));
        var journalPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(game.GameRoot, ".rhi-linux", "transactions"), "*.json"));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(journalPath));
        Assert.Equal("rolledBack", journal.RootElement.GetProperty("state").GetString());
        var operations = journal.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Equal("rolledBack", operations[0].GetProperty("status").GetString());
        Assert.Equal("failed", operations[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task SuccessfulDeploymentPersistsCompletedOperationJournal()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var source = temp.Pe("staging/ReShade64.dll");
        var plan = await new DeploymentPlanner().BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, source, "ReShade64.dll", "6.7.3"));

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        var journalPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(game.GameRoot, ".rhi-linux", "transactions"), "*.json"));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(journalPath));
        Assert.Equal(plan.Id, journal.RootElement.GetProperty("id").GetString());
        Assert.Equal("completed", journal.RootElement.GetProperty("state").GetString());
        Assert.All(journal.RootElement.GetProperty("operations").EnumerateArray(), operation =>
            Assert.Contains(operation.GetProperty("status").GetString(), new[] { "completed", "skipped" }));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(game.GameRoot, ".rhi-linux", "transactions"), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task VerifyIniValueAcceptsAnExpectedRemovedKey()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var ini = temp.File("game/OptiScaler.ini", "[Plugins]\nLoadReshade=true\nKeepMe=1\n");
        var manifest = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        var plan = new DeploymentPlan
        {
            Id = "verify-removed-ini-key",
            InstallId = game.EffectiveInstallId,
            SteamAppId = game.SteamAppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory,
            Action = "remove managed INI value",
            Operations =
            {
                new(DeploymentOperationType.WriteIniValue, ini, Value: "Plugins:LoadReshade=<remove>"),
                new(DeploymentOperationType.VerifyIniValue, ini, Value: "Plugins:LoadReshade=<remove>"),
                new(DeploymentOperationType.WriteManifest, manifest)
            }
        };

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        var document = IniDocument.Parse(await File.ReadAllTextAsync(ini));
        Assert.Null(document.Get("Plugins", "LoadReshade"));
        Assert.Equal("1", document.Get("Plugins", "KeepMe"));
    }

    [Fact]
    public async Task OptiScalerMovesManagedReShadeAndEnforcesIni()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var planner = new DeploymentPlanner();
        var reshade = temp.Pe("staging/ReShade.dll"); var reshadePlan = await planner.BuildInstallPlanAsync(game, new(ComponentKind.ReShade, reshade, "ReShade.dll"));
        Assert.True((await new DeploymentExecutor().ExecuteAsync(reshadePlan, false)).Succeeded);
        var opti = temp.Pe("staging/OptiScaler.dll"); var optiPlan = await planner.BuildInstallPlanAsync(game, new(ComponentKind.OptiScaler, opti, "OptiScaler.dll", "v0.9.4"));
        var optiResult = await new DeploymentExecutor().ExecuteAsync(optiPlan, false);
        Assert.True(optiResult.Succeeded, optiResult.Error);
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll"))); Assert.Equal("true", IniDocument.Parse(File.ReadAllText(Path.Combine(game.GameRoot, "OptiScaler.ini"))).Get("Plugins", "LoadReshade"));
    }

    [Fact]
    public async Task MovingReShadeBehindOptiScalerPreservesOwnershipProvenance()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var reshadeSource = temp.Pe("staging/ReShade64.dll");
        var reshadeHash = await ArtifactDownloader.Sha256Async(reshadeSource);
        var bundleHash = new string('b', 64);
        var reshade = new ComponentArtifact(ComponentKind.ReShade, reshadeSource, "ReShade64.dll", "6.7.3",
            "https://reshade.me/downloads/ReShade_Setup_6.7.3_Addon.exe", reshadeHash,
            SourceBlobSha256: reshadeHash, SourceBundleSha256: bundleHash);
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, reshade), false)).Succeeded);

        var opti = new ComponentArtifact(ComponentKind.OptiScaler, temp.Pe("staging/OptiScaler.dll"),
            "OptiScaler.dll", "v0.7.7");
        var result = await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, opti), false);

        Assert.True(result.Succeeded, result.Error);
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        var moved = Assert.Single(manifest.Files, file =>
            file.Component == ComponentKind.ReShade &&
            Path.GetFileName(file.RelativePath).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ReShade64.dll", Path.GetFileName(moved.RelativePath));
        Assert.Equal(reshade.Version, moved.Version);
        Assert.Equal(reshade.SourceUrl, moved.SourceUrl);
        Assert.Equal(reshadeHash, moved.SourceBlobSha256);
        Assert.Equal(bundleHash, moved.SourceBundleSha256);
    }

    [Fact]
    public async Task OptiScalerRemovalRestoresOriginalIni()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var planner = new DeploymentPlanner();
        const string originalIni = "[Plugins]\nLoadReshade=auto\nLoadAsiPlugins=auto\n[User]\nSetting=keep\n";
        File.WriteAllText(Path.Combine(game.GameRoot, "OptiScaler.ini"), originalIni);
        var opti = temp.Pe("staging/OptiScaler.dll");
        var install = await planner.BuildInstallPlanAsync(game,
            new(ComponentKind.OptiScaler, opti, "OptiScaler.dll", "v0.9.4"));
        Assert.True((await new DeploymentExecutor().ExecuteAsync(install, false)).Succeeded);
        var remove = await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler);
        Assert.True((await new DeploymentExecutor().ExecuteAsync(remove, false)).Succeeded);
        Assert.Equal(originalIni, File.ReadAllText(Path.Combine(game.GameRoot, "OptiScaler.ini")));
    }

    [Fact]
    public async Task RecommendedStackOrdersDependenciesAndPreservesRenoDxFilename()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var planner = new DeploymentPlanner();
        var reshade = new ComponentArtifact(ComponentKind.ReShade, temp.Pe("staging/ReShade64.dll"), "ReShade64.dll", "6.7.3");
        var reno = new ComponentArtifact(ComponentKind.RenoDx, temp.Pe("staging/renodx-fixture.addon64"), "renodx-fixture.addon64", "snapshot");
        var opti = new ComponentArtifact(ComponentKind.OptiScaler, temp.Pe("staging/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4");
        var plan = await planner.BuildRecommendedStackPlanAsync(game, new(reshade, reno, opti));

        var descriptions = plan.Operations.Select(DeploymentExecutor.Describe).ToArray();
        Assert.True(Array.FindIndex(descriptions, x => x.Contains("ReShade64.dll", StringComparison.Ordinal)) <
            Array.FindIndex(descriptions, x => x.Contains("renodx-fixture.addon64", StringComparison.Ordinal)));
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Copy && x.Target.EndsWith("ReShade64.dll", StringComparison.Ordinal));
        Assert.Contains(plan.Operations, x => x.Target.EndsWith("renodx-fixture.addon64", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Operations, x => x.Target.EndsWith("RenoDX.addon64", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Operations, x => x.Target.Contains("OptiPatcher", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecommendedStackExecutesCoexistenceAndRemovalRestoresReShadeProxy()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var planner = new DeploymentPlanner(); var executor = new DeploymentExecutor();
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.Pe("staging/ReShade64.dll"), "ReShade64.dll"),
            new(ComponentKind.RenoDx, temp.Pe("staging/renodx-fixture.addon64"), "renodx-fixture.addon64"),
            new(ComponentKind.OptiScaler, temp.Pe("staging/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"));

        var install = await planner.BuildRecommendedStackPlanAsync(game, artifacts);
        Assert.True((await executor.ExecuteAsync(install, false)).Succeeded);
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll")));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "renodx-fixture.addon64")));

        var remove = await planner.BuildRemovePlanAsync(game, ComponentKind.OptiScaler);
        Assert.True((await executor.ExecuteAsync(remove, false)).Succeeded);
        Assert.True(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "ReShade64.dll")));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "plugins", "OptiPatcher.asi")));
    }

    [Fact]
    public async Task OptiScalerUsesTemplateSectionAndDoesNotDuplicateIniKeys()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var planner = new DeploymentPlanner(); var executor = new DeploymentExecutor();
        var ini = temp.File("staging/OptiScaler.ini", "[OptiScaler]\nLoadReshade=false\nLoadReshade=false\nLoadAsiPlugins=false\n");
        File.WriteAllText(Path.Combine(game.GameRoot, "OptiScaler.ini"), "[User]\nSharpness=0.7\n[OptiScaler]\nLoadReshade=false\nLoadReshade=false\n");
        var artifacts = new RecommendedStackArtifacts(
            new(ComponentKind.ReShade, temp.Pe("staging/ReShade64.dll"), "ReShade64.dll"),
            new(ComponentKind.RenoDx, temp.Pe("staging/renodx-fixture.addon64"), "renodx-fixture.addon64"),
            new(ComponentKind.OptiScaler, temp.Pe("staging/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, ini, "OptiScaler.ini", "v0.9.4")]);

        var plan = await planner.BuildRecommendedStackPlanAsync(game, artifacts);
        Assert.Contains(plan.FileDecisions, decision =>
            decision.Component == ComponentKind.OptiScaler &&
            decision.DestinationPath.Equals(Path.Combine(game.GameRoot, "OptiScaler.ini"), StringComparison.Ordinal) &&
            decision.PreservesExistingFile && decision.Reason.Contains("back up", StringComparison.OrdinalIgnoreCase));
        Assert.True((await executor.ExecuteAsync(plan, false)).Succeeded);
        var text = File.ReadAllText(Path.Combine(game.GameRoot, "OptiScaler.ini"));
        Assert.Contains("Sharpness=0.7", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split('\n').Count(x => x.Equals("LoadReshade=true", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, text.Split('\n').Count(x => x.Equals("LoadAsiPlugins=true", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("true", IniDocument.Parse(text).Get("OptiScaler", "LoadReshade"));
    }

    [Fact]
    public async Task ManagedMalformedOptiScalerIniIsReplacedAndVerifiedDuringRepair()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var template = temp.File("staging/OptiScaler.ini",
            "[Plugins]\nLoadReshade=auto\nLoadAsiPlugins=auto\n[User]\nSharpness=0.8\n");
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.PeWithMarker("staging/OptiScaler.dll", "OptiScaler"),
                "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, template, "OptiScaler.ini", "v0.9.4")]);
        Assert.True((await executor.ExecuteAsync(
            await planner.BuildRecommendedStackPlanAsync(game, artifacts), false)).Succeeded);
        var installedIni = Path.Combine(game.GameRoot, "OptiScaler.ini");
        await File.WriteAllTextAsync(installedIni, "[Plugins\nLoadReshade=true\nLoadAsiPlugins=true\n");

        var before = Assert.Single(await new ComponentDetector().DetectAsync(game),
            status => status.Component == ComponentKind.OptiScaler);
        Assert.Contains(before.Health, new[] { ComponentHealth.IncorrectlyConfigured, ComponentHealth.RepairAvailable });
        var repair = await planner.BuildRecommendedStackPlanAsync(game, artifacts);
        Assert.True(repair.RequiresRepair);
        Assert.Contains(repair.Operations, operation => operation.Type == DeploymentOperationType.WriteIniValue &&
            operation.Value == "Plugins:LoadReshade=false" && !operation.ClearConfigurationPatch);
        Assert.Contains(repair.Operations, operation => operation.Type == DeploymentOperationType.WriteIniValue &&
            operation.Value == "Plugins:LoadAsiPlugins=false" && !operation.ClearConfigurationPatch);
        var result = await executor.ExecuteAsync(repair, false);

        Assert.True(result.Succeeded, result.Error);
        var repaired = IniDocument.Parse(await File.ReadAllTextAsync(installedIni));
        Assert.Equal("false", repaired.Get("Plugins", "LoadReshade"));
        Assert.Equal("false", repaired.Get("Plugins", "LoadAsiPlugins"));
        Assert.Equal("0.8", repaired.Get("User", "Sharpness"));
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        var compatibilityPatches = manifest.ConfigurationPatches.Where(patch =>
            patch.Key is "LoadReshade" or "LoadAsiPlugins").ToArray();
        Assert.Equal(2, compatibilityPatches.Length);
        Assert.All(compatibilityPatches, patch =>
        {
            Assert.Equal("auto", patch.PreviousValue);
            Assert.Equal("false", patch.NewValue);
        });
        Assert.Equal(ComponentHealth.Installed, Assert.Single(await new ComponentDetector().DetectAsync(game),
            status => status.Component == ComponentKind.OptiScaler).Health);
    }

    [Fact]
    public async Task UnownedMalformedOptiScalerIniIsPreservedAndBlocksAutomaticReplacement()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var installedIni = temp.File("game/OptiScaler.ini", "[Plugins\nUserSetting=keep\n");
        var original = await File.ReadAllBytesAsync(installedIni);
        var template = temp.File("staging/OptiScaler.ini",
            "[Plugins]\nLoadReshade=auto\nLoadAsiPlugins=auto\n");
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("staging/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, template, "OptiScaler.ini", "v0.9.4")]);

        var exception = await Assert.ThrowsAsync<DeploymentConflictException>(() =>
            new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts));

        Assert.Contains("existing unowned INI is malformed", exception.TechnicalDetails, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(installedIni));
    }

    [Fact]
    public async Task RepairRestoresMissingManagedFileAndItsRecordedHash()
    {
        using var temp = new TestDirectory(); var game = Game(temp); var planner = new DeploymentPlanner(); var executor = new DeploymentExecutor();
        var reshade = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("staging/ReShade64.dll", "reshade.me"), "ReShade64.dll", "6.7.3");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, reshade), false)).Succeeded);
        var source = temp.Pe("staging/renodx-fixture.addon64");
        var artifact = new ComponentArtifact(ComponentKind.RenoDx, source, "renodx-fixture.addon64", "snapshot");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, artifact), false)).Succeeded);
        var installed = Path.Combine(game.GameRoot, artifact.FileName);
        File.Delete(installed);
        Assert.Equal(ComponentHealth.PartiallyInstalled, Assert.Single(await new ComponentDetector().DetectAsync(game), x => x.Component == ComponentKind.RenoDx).Health);

        Assert.True((await executor.ExecuteAsync(await planner.BuildRepairPlanAsync(game, ComponentKind.RenoDx, artifact), false)).Succeeded);

        Assert.True(File.Exists(installed));
        Assert.Equal(ComponentHealth.Installed, Assert.Single(await new ComponentDetector().DetectAsync(game), x => x.Component == ComponentKind.RenoDx).Health);
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game"); var exe = temp.Pe("game/Game.exe");
        return new(42, "Game", temp.Path, temp.Path, root, temp.Combine("compatdata", "42", "pfx"), exe, root, DetectionConfidence.High, "test", GameEngine.Unknown, []);
    }
}
