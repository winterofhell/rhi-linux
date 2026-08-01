using System.Security.Cryptography;
using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Tests;

public sealed class InstallationRecoveryTests
{
    [Fact]
    public async Task GameOwnedFidelityFxDllIsPreservedAndDoesNotBlockInstallation()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var target = temp.PeWithMarker("game/amd_fidelityfx_dx12.dll", "AMD FidelityFX SDK");
        var originalHash = Hash(target);
        var incoming = temp.PeWithMarker("stage/amd_fidelityfx_dx12.dll", "OptiScaler bundle runtime");
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, incoming, "amd_fidelityfx_dx12.dll", "v0.9.4",
                Sha256: Hash(incoming), RelativePath: "amd_fidelityfx_dx12.dll",
                Requirement: DeploymentFileRequirement.Conditional,
                RequirementReason: "Release-matched FidelityFX backend.", Feature: "fidelityfx-dx12",
                CanOmitOnCollision: true)]);

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);

        Assert.Equal("A compatible installation method was found. Existing game files will not be changed.",
            plan.CompatibilityMessage);
        Assert.DoesNotContain(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.Equals(target, StringComparison.Ordinal));
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.VerifySha256 &&
            x.Target.Equals(target, StringComparison.Ordinal));
        var decision = Assert.Single(plan.FileDecisions, x => x.DestinationPath.Equals(target, StringComparison.Ordinal));
        Assert.Equal(DeploymentFileRequirement.Conditional, decision.Requirement);
        Assert.Equal(DeploymentFileAction.PreserveExisting, decision.Action);
        Assert.True(decision.PreservesExistingFile);
        Assert.True(decision.AlternativeExists);
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(originalHash, Hash(target));
    }

    [Fact]
    public async Task UnknownFidelityFxDllIsPreservedAndConditionalBundleCopyIsOmitted()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var target = temp.Pe("game/amd_fidelityfx_dx12.dll", size: 768);
        var originalHash = Hash(target);
        var incoming = temp.PeWithMarker("stage/amd_fidelityfx_dx12.dll", "official different bytes");
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, incoming, "amd_fidelityfx_dx12.dll", "v0.9.4",
                Sha256: Hash(incoming), RelativePath: "amd_fidelityfx_dx12.dll",
                Requirement: DeploymentFileRequirement.Conditional,
                RequirementReason: "Release-matched FidelityFX backend.", Feature: "fidelityfx-dx12",
                CanOmitOnCollision: true)]);

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);

        Assert.DoesNotContain(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.Equals(target, StringComparison.Ordinal));
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.VerifySha256 &&
            x.Target.Equals(target, StringComparison.Ordinal) && x.ExpectedSha256 == originalHash);
        var decision = Assert.Single(plan.FileDecisions, x => x.DestinationPath.Equals(target, StringComparison.Ordinal));
        Assert.Equal(DeploymentFileAction.PreserveExisting, decision.Action);
        Assert.Contains("omitted", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True((await new DeploymentExecutor().ExecuteAsync(plan, false)).Succeeded);
        Assert.Equal(originalHash, Hash(target));
    }

    [Fact]
    public async Task OptionalCollisionIsOmittedWithCompleteFileDecision()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var target = temp.Pe("game/libxess.dll", size: 768);
        var originalHash = Hash(target);
        var incoming = temp.PeWithMarker("stage/libxess.dll", "official XeSS bytes");
        var support = new ComponentArtifact(ComponentKind.OptiScaler, incoming, "libxess.dll", "v0.9.4",
            "https://github.com/optiscaler/OptiScaler/releases/tag/v0.9.4", Hash(incoming), "libxess.dll",
            Hash(incoming), new string('a', 64), DeploymentFileRequirement.Optional,
            "Optional XeSS backend.", "xess", true);
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [support]);

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);

        Assert.DoesNotContain(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.Equals(target, StringComparison.Ordinal));
        var decision = Assert.Single(plan.FileDecisions, x => x.DestinationPath.Equals(target, StringComparison.Ordinal));
        Assert.Equal(ComponentKind.OptiScaler, decision.Component);
        Assert.Equal(DeploymentFileRequirement.Optional, decision.Requirement);
        Assert.Equal(DeploymentFileAction.PreserveExisting, decision.Action);
        Assert.Equal("v0.9.4", decision.SourceRelease);
        Assert.Equal("libxess.dll", decision.SourceArchivePath);
        Assert.Equal(target, decision.DestinationPath);
        Assert.False(decision.ReplacesManagedFile);
        Assert.True(decision.PreservesExistingFile);
        Assert.True(decision.AlternativeExists);
        Assert.Contains(decision.Alternatives, x => x.Contains("without xess", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        Assert.True((await new DeploymentExecutor().ExecuteAsync(plan, false)).Succeeded);
        Assert.Equal(originalHash, Hash(target));
    }

    [Fact]
    public async Task ByteIdenticalOfficialOptionalFileIsAdopted()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var incoming = temp.PeWithMarker("stage/libxell.dll", "official XeLL bytes");
        var target = Path.Combine(game.DeploymentDirectory, "libxell.dll");
        File.Copy(incoming, target);
        var incomingHash = Hash(incoming);
        var support = new ComponentArtifact(ComponentKind.OptiScaler, incoming, "libxell.dll", "v0.9.4",
            Sha256: incomingHash, RelativePath: "libxell.dll", SourceBlobSha256: incomingHash,
            SourceBundleSha256: new string('b', 64), Requirement: DeploymentFileRequirement.Optional,
            RequirementReason: "Optional latency backend.", Feature: "xess", CanOmitOnCollision: true);
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [support]);

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);

        Assert.DoesNotContain(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.Equals(target, StringComparison.Ordinal));
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.TrackExistingFile &&
            x.Target.Equals(target, StringComparison.Ordinal) && x.ExpectedSha256 == incomingHash);
        var decision = Assert.Single(plan.FileDecisions, x => x.DestinationPath.Equals(target, StringComparison.Ordinal));
        Assert.Equal(DeploymentFileAction.AdoptOfficial, decision.Action);
        Assert.False(decision.ReplacesManagedFile);
        Assert.False(decision.PreservesExistingFile);
    }

    [Fact]
    public async Task UnknownRequiredRuntimeCollisionBlocksPlanning()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        temp.Pe("game/required-runtime.dll", size: 768);
        var incoming = temp.PeWithMarker("stage/required-runtime.dll", "different required bytes");
        var support = new ComponentArtifact(ComponentKind.OptiScaler, incoming, "required-runtime.dll", "v0.9.4",
            Sha256: Hash(incoming), RelativePath: "required-runtime.dll",
            Requirement: DeploymentFileRequirement.Required,
            RequirementReason: "Required by the selected strategy.", Feature: "required-feature");
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [support]);

        var exception = await Assert.ThrowsAsync<DeploymentConflictException>(() =>
            new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts));

        Assert.DoesNotContain("required-runtime.dll", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required-runtime.dll", exception.TechnicalDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OccupiedPreferredProxySelectsSafeAlternative()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        temp.Pe("game/dxgi.dll", size: 768);
        var source = temp.PeWithMarker("stage/ReShade64.dll", "reshade.me");

        var plan = await new DeploymentPlanner().BuildInstallPlanAsync(game,
            new(ComponentKind.ReShade, source, "ReShade64.dll"));

        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.EndsWith("winmm.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Operations, x => x.Type is DeploymentOperationType.Copy or DeploymentOperationType.Backup &&
            x.Target.EndsWith("dxgi.dll", StringComparison.Ordinal));
        Assert.Contains("winmm=n,b", plan.LaunchOption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CyberpunkGameOwnedDbgHelpIsPreservedAndRecordedWithAlternativeProxy()
    {
        using var temp = new TestDirectory();
        var game = Game(temp) with { AppId = 1091500, Name = "Cyberpunk 2077" };
        var dbgHelp = temp.PeWithMarker("game/dbghelp.dll", "Microsoft Corporation\0Debugging Tools for Windows");
        var originalHash = Hash(dbgHelp);
        var reshade = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("stage/ReShade64.dll", "reshade.me"), "ReShade64.dll", "6.7.3");

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, new(reshade, null, null));

        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.EndsWith("version.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Operations, x => x.Type is DeploymentOperationType.Copy or DeploymentOperationType.Backup &&
            x.Target.Equals(dbgHelp, StringComparison.Ordinal));
        var decision = Assert.Single(plan.FileDecisions, x => x.DestinationPath.Equals(dbgHelp, StringComparison.Ordinal));
        Assert.Equal(DeploymentFileRequirement.NeverReplace, decision.Requirement);
        Assert.Equal(DeploymentFileAction.PreserveExisting, decision.Action);
        Assert.True(decision.PreservesExistingFile);
        Assert.True(decision.AlternativeExists);
        Assert.Contains("Use version.dll", decision.Alternatives);
        Assert.Equal(originalHash, Hash(dbgHelp));
    }

    [Fact]
    public async Task MultipleOccupiedProxyNamesArePreservedWhileLastSafeAlternativeIsUsed()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var occupiedNames = DeploymentPlanner.SupportedProxyNames
            .Where(x => !x.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        var occupied = occupiedNames.Select(name => temp.Pe($"game/{name}", size: 768)).ToArray();
        var originalHashes = occupied.ToDictionary(path => path, Hash, StringComparer.Ordinal);
        var reshade = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("stage/ReShade64.dll", "reshade.me"), "ReShade64.dll", "6.7.3");

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, new(reshade, null, null));

        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.Copy &&
            x.Target.EndsWith("winhttp.dll", StringComparison.Ordinal));
        Assert.All(occupied, target =>
        {
            Assert.DoesNotContain(plan.Operations, x => x.Type is DeploymentOperationType.Copy or DeploymentOperationType.Backup &&
                x.Target.Equals(target, StringComparison.Ordinal));
            var decision = Assert.Single(plan.FileDecisions, x => x.DestinationPath.Equals(target, StringComparison.Ordinal));
            Assert.Equal(DeploymentFileRequirement.NeverReplace, decision.Requirement);
            Assert.True(decision.PreservesExistingFile);
            Assert.Contains("Use winhttp.dll", decision.Alternatives);
            Assert.Equal(originalHashes[target], Hash(target));
        });
    }

    [Fact]
    public async Task ManagedRelocatedLeftoverIsRepairedBeforeContinuing()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var leftover = temp.PeWithMarker("game/amd_fidelityfx_dx12.dll", "old managed bundle");
        await WriteManifestAsync(game, new ManagedFile("legacy/amd_fidelityfx_dx12.dll",
            ComponentKind.OptiScaler, Hash(leftover), "old", null, null));
        var incoming = temp.PeWithMarker("stage/amd_fidelityfx_dx12.dll", "new managed bundle");
        var artifacts = new RecommendedStackArtifacts(null, null,
            new(ComponentKind.OptiScaler, temp.Pe("stage/OptiScaler.dll"), "OptiScaler.dll", "v0.9.4"),
            [new(ComponentKind.OptiScaler, incoming, "amd_fidelityfx_dx12.dll", "v0.9.4",
                Sha256: Hash(incoming), RelativePath: "amd_fidelityfx_dx12.dll")]);

        var plan = await new DeploymentPlanner().BuildRecommendedStackPlanAsync(game, artifacts);

        Assert.True(plan.RequiresRepair);
        Assert.Equal("Some files from an earlier installation need to be repaired before continuing.",
            plan.CompatibilityMessage);
        Assert.Contains(plan.Operations, x => x.Type == DeploymentOperationType.DeleteVerifiedFile &&
            x.Target.Equals(leftover, StringComparison.Ordinal));
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Hash(incoming), Hash(leftover));
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot);
        Assert.DoesNotContain(manifest.Files, x => x.RelativePath.StartsWith("legacy/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovalClearsMissingOwnershipAndAllowsImmediateReinstall()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var reshade = new ComponentArtifact(ComponentKind.ReShade,
            temp.PeWithMarker("stage/ReShade64.dll", "reshade.me"), "ReShade64.dll", "6.7.3");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, reshade), false)).Succeeded);
        var source = temp.Pe("stage/reno.addon64");
        var artifact = new ComponentArtifact(ComponentKind.RenoDx, source, "reno.addon64", "snapshot");
        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, artifact), false)).Succeeded);
        File.Delete(Path.Combine(game.GameRoot, "reno.addon64"));

        var remove = await planner.BuildRemovePlanAsync(game, ComponentKind.RenoDx);
        Assert.Contains(remove.Operations, x => x.Type == DeploymentOperationType.ForgetOwnership);
        Assert.True((await executor.ExecuteAsync(remove, false)).Succeeded);
        Assert.DoesNotContain((await ComponentDetector.LoadManifestAsync(game.GameRoot)).Files,
            x => x.Component == ComponentKind.RenoDx);

        var reinstall = await planner.BuildInstallPlanAsync(game, artifact);
        Assert.True((await executor.ExecuteAsync(reinstall, false)).Succeeded);
        Assert.Equal(ComponentHealth.Installed,
            Assert.Single(await new ComponentDetector().DetectAsync(game), x => x.Component == ComponentKind.RenoDx).Health);
    }

    [Fact]
    public async Task SuccessfulUpdateBackupIsNotExposedAsAnUnownedRestoreAfterRemoval()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var planner = new DeploymentPlanner();
        var executor = new DeploymentExecutor();
        var firstSource = temp.PeWithMarker("stage/v1/ReShade64.dll", "reshade.me v1");
        var secondSource = temp.PeWithMarker("stage/v2/ReShade64.dll", "reshade.me v2");
        var first = new ComponentArtifact(ComponentKind.ReShade, firstSource, "ReShade64.dll", "1.0");
        var second = new ComponentArtifact(ComponentKind.ReShade, secondSource, "ReShade64.dll", "2.0");

        Assert.True((await executor.ExecuteAsync(await planner.BuildInstallPlanAsync(game, first), false)).Succeeded);
        var update = await planner.BuildInstallPlanAsync(game, second);
        Assert.Contains(update.Operations, operation => operation.Type == DeploymentOperationType.Backup);
        Assert.True((await executor.ExecuteAsync(update, false)).Succeeded);
        var updateBackup = Assert.Single(update.Operations, operation => operation.Type == DeploymentOperationType.Backup).Source!;
        Assert.True(File.Exists(updateBackup));

        Assert.True((await executor.ExecuteAsync(
            await planner.BuildRemovePlanAsync(game, ComponentKind.ReShade), false)).Succeeded);
        var restore = await planner.BuildRestorePlanAsync(game);

        Assert.DoesNotContain(restore.Operations, operation => operation.Type == DeploymentOperationType.RestoreBackup);
        Assert.True(File.Exists(updateBackup));
        Assert.False(File.Exists(Path.Combine(game.GameRoot, "dxgi.dll")));
    }

    [Fact]
    public async Task RestoreUsesOnlyOwnershipRecordedBackupAndClearsStaleOwnership()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var backup = temp.PeWithMarker("game/.rhi-linux/backups/original/dxgi.dll", "original game proxy");
        var backupRelative = Path.GetRelativePath(game.GameRoot, backup);
        await WriteManifestAsync(game, new ManagedFile("dxgi.dll", ComponentKind.ReShade,
            new string('a', 64), "6.7.3", null, backupRelative, BackupSha256: Hash(backup)));

        var plan = await new DeploymentPlanner().BuildRestorePlanAsync(game);
        var restore = Assert.Single(plan.Operations, operation => operation.Type == DeploymentOperationType.RestoreBackup);
        Assert.Equal(Hash(backup), restore.ExpectedSha256);
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(backup));
        Assert.Contains("original game proxy", File.ReadAllText(Path.Combine(game.GameRoot, "dxgi.dll")),
            StringComparison.Ordinal);
        Assert.DoesNotContain((await ComponentDetector.LoadManifestAsync(game.GameRoot)).Files,
            file => file.RelativePath == "dxgi.dll");
    }

    [Fact]
    public async Task LegacyBackupWithoutRecordedDigestIsNotExposedForRestore()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var backup = temp.File("game/.rhi-linux/backups/legacy/OptiScaler.ini", "legacy original");
        await WriteManifestAsync(game, new ManagedFile("OptiScaler.ini", ComponentKind.OptiScaler,
            new string('a', 64), "v1", null, Path.GetRelativePath(game.GameRoot, backup)));

        var plan = await new DeploymentPlanner().BuildRestorePlanAsync(game);

        Assert.DoesNotContain(plan.Operations, operation => operation.Type == DeploymentOperationType.RestoreBackup);
        Assert.Contains(plan.Warnings, warning => warning.Contains("no trusted SHA-256", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public async Task BackupTamperedBeforePlanningIsNotExposedForRestore()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var backup = temp.File("game/.rhi-linux/backups/tampered/OptiScaler.ini", "trusted original");
        var recordedHash = Hash(backup);
        await WriteManifestAsync(game, new ManagedFile("OptiScaler.ini", ComponentKind.OptiScaler,
            new string('a', 64), "v1", null, Path.GetRelativePath(game.GameRoot, backup),
            BackupSha256: recordedHash));
        await File.WriteAllTextAsync(backup, "tampered backup");

        var plan = await new DeploymentPlanner().BuildRestorePlanAsync(game);

        Assert.DoesNotContain(plan.Operations, operation => operation.Type == DeploymentOperationType.RestoreBackup);
        Assert.Contains(plan.Warnings, warning => warning.Contains("changed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("tampered backup", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task BackupTamperedAfterPlanningFailsClosedAtExecution()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var backup = temp.File("game/.rhi-linux/backups/late-tamper/OptiScaler.ini", "trusted original");
        var target = Path.Combine(game.GameRoot, "OptiScaler.ini");
        var recordedHash = Hash(backup);
        await WriteManifestAsync(game, new ManagedFile("OptiScaler.ini", ComponentKind.OptiScaler,
            new string('a', 64), "v1", null, Path.GetRelativePath(game.GameRoot, backup),
            BackupSha256: recordedHash));
        var plan = await new DeploymentPlanner().BuildRestorePlanAsync(game);
        await File.WriteAllTextAsync(backup, "tampered after planning");

        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Contains("recorded backup changed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
        Assert.Equal("tampered after planning", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task ChangedManagedIniIsPreservedThenTrustedOriginalIsRestored()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var managedBytes = "[Plugins]\nLoadReshade=true\n";
        var changedBytes = "[Plugins]\nLoadReshade=false\n[User]\nKeep=1\n";
        var originalBytes = "[Game]\nOriginalSetting=1\n";
        var target = temp.File("game/OptiScaler.ini", changedBytes);
        var backup = temp.File("game/.rhi-linux/backups/original/OptiScaler.ini", originalBytes);
        await WriteManifestAsync(game, new ManagedFile("OptiScaler.ini", ComponentKind.OptiScaler,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(managedBytes))).ToLowerInvariant(),
            "v0.9.4", null, Path.GetRelativePath(game.GameRoot, backup), BackupSha256: Hash(backup)));

        var plan = await new DeploymentPlanner().BuildRemovePlanAsync(game, ComponentKind.OptiScaler);
        var result = await new DeploymentExecutor().ExecuteAsync(plan, false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(originalBytes, await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(backup));
        var changedRecovery = Path.Combine(game.GameRoot, ".rhi-linux", "recovery", plan.Id,
            "OptiScaler.ini.changed");
        Assert.Equal(changedBytes, await File.ReadAllTextAsync(changedRecovery));
        Assert.DoesNotContain((await ComponentDetector.LoadManifestAsync(game.GameRoot)).Files,
            file => file.Component == ComponentKind.OptiScaler);
    }

    [Fact]
    public void ManagedFileBackupDigestSerializationRemainsBackwardCompatible()
    {
        const string legacy = """
            {
              "relativePath": "dxgi.dll",
              "component": 0,
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "version": "1",
              "sourceUrl": null,
              "backupRelativePath": ".rhi-linux/backups/original/dxgi.dll"
            }
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyRecord = JsonSerializer.Deserialize<ManagedFile>(legacy, options);
        Assert.NotNull(legacyRecord);
        Assert.Null(legacyRecord.BackupSha256);

        var expectedHash = new string('b', 64);
        var current = legacyRecord with { BackupSha256 = expectedHash };
        var roundTrip = JsonSerializer.Deserialize<ManagedFile>(JsonSerializer.Serialize(current, options), options);
        Assert.NotNull(roundTrip);
        Assert.Equal(expectedHash, roundTrip.BackupSha256);
    }

    private static async Task WriteManifestAsync(SteamGame game, params ManagedFile[] files)
    {
        var path = Path.Combine(game.GameRoot, ".rhi-linux", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new GameManifest
        {
            AppId = game.AppId,
            TransactionIds = ["legacy-install"],
            Files = files.ToList()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new(900001, "Fixture", temp.Path, temp.Path, root, temp.Combine("compatdata", "900001", "pfx"),
            executable, root, DetectionConfidence.High, "fixture", GameEngine.Unknown,
            [new(executable, 100, DetectionConfidence.High, PeArchitecture.X64,
                new FileInfo(executable).Length, ["fixture"])]);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
