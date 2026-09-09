using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Sources;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class ReleaseCandidateServicesTests
{
    [Fact]
    public void OverrideValidationRejectsExternalTargetsAndAcceptsWinePrefix()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, "override");
        var prefix = temp.Directory("prefix", "drive_c");
        var prefixRoot = Directory.GetParent(prefix)!.FullName;
        temp.Directory("prefix", "dosdevices");
        var external = temp.Pe("outside.exe");

        var invalid = GameOverrideValidator.Validate(game, external, temp.Path, prefixRoot);
        var valid = GameOverrideValidator.Validate(game, game.Executable, game.GameRoot, prefixRoot);

        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Contains("inside the game", StringComparison.Ordinal));
        Assert.True(valid.IsValid);
        Assert.True(GameOverrideValidator.LooksLikePrefix(prefixRoot));
    }

    [Fact]
    public async Task PortableProfilesExcludeAbsolutePathsAndRequireReviewForLikelyMatches()
    {
        using var temp = new TestDirectory();
        var exact = Game(temp, "exact") with { ExternalId = "gog-42" };
        var likely = Game(temp, "likely") with
        {
            InstallId = new("likely-local"),
            ExternalId = "local-id",
            Name = "Likely Game"
        };
        var store = new JsonUserGameProfileStore(temp.Combine("profiles", "local.json"));
        var service = new PortableProfileService();
        var exportPath = temp.Combine("export.json");
        var profile = new UserGameProfile(exact.InstallId, exact.Executable, exact.GameRoot,
            "install-reshade", "dxgi.dll", true, DateTimeOffset.UtcNow);

        await service.ExportAsync(exportPath, [profile], new Dictionary<GameInstallId, InstalledGame>
        {
            [exact.InstallId] = exact
        });
        var exported = await File.ReadAllTextAsync(exportPath);
        Assert.DoesNotContain(exact.GameRoot, exported, StringComparison.Ordinal);

        var document = new PortableProfileDocument(PortableProfileService.CurrentSchemaVersion, DateTimeOffset.UtcNow,
        [
            new("exact", exact.Store, exact.PrimaryLauncher, exact.ExternalId, exact.Name,
                "install-reshade", "dxgi.dll", Path.GetFileName(exact.Executable), "", true),
            new("likely", likely.Store, likely.PrimaryLauncher, "different", likely.Name,
                "keep-current", "d3d11.dll", Path.GetFileName(likely.Executable), "", false)
        ]);
        var importPath = temp.Combine("import.json");
        await File.WriteAllTextAsync(importPath, JsonSerializer.Serialize(document));
        var preview = await service.PreviewImportAsync(importPath, [exact, likely]);

        Assert.True(preview.IsValid);
        Assert.Equal(ProfileImportMatchState.ExactMatch, preview.Entries[0].State);
        Assert.Equal(ProfileImportMatchState.LikelyMatch, preview.Entries[1].State);
        var values = await service.ApplyImportAsync(preview,
            [new("likely", likely.InstallId)], new Dictionary<GameInstallId, UserGameProfile>(), store);
        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.True(GameOverrideValidator.IsContained(
            value.InstallId.Equals(exact.InstallId) ? exact.GameRoot : likely.GameRoot,
            value.PreferredExecutable!)));
    }

    [Fact]
    public async Task PortableProfileImportRejectsTraversal()
    {
        using var temp = new TestDirectory();
        var document = new PortableProfileDocument(PortableProfileService.CurrentSchemaVersion, DateTimeOffset.UtcNow,
        [
            new("bad", GameStore.Gog, GameLauncher.Heroic, "42", "Bad", null, null,
                "../outside.exe", null, false)
        ]);
        var path = temp.Combine("bad.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));

        var preview = await new PortableProfileService().PreviewImportAsync(path, []);

        Assert.False(preview.IsValid);
        Assert.Contains(preview.Errors, error => error.Contains("outside", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreflightRejectsChangedApprovedFileAndRunningGame()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, "preflight");
        var target = temp.File("preflight/dxgi.dll", "old");
        var plan = new DeploymentPlan
        {
            Id = "preflight-plan",
            InstallId = game.InstallId.Value,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.GameRoot,
            Action = "Install ReShade",
            Operations = [new(DeploymentOperationType.Copy, target, game.Executable)]
        };
        await DeploymentPlanApproval.SealAsync(plan);
        await File.WriteAllTextAsync(target, "changed");
        var paths = Paths(temp);
        Directory.CreateDirectory(paths.AppDataDirectory);
        var service = new OperationPreflightService(paths, new FixedRunningDetector(RunningGameState.Running));

        var result = await service.CheckAsync(plan);

        Assert.False(result.CanProceed);
        Assert.Contains(result.Issues, issue => issue.Code == OperationPreflightCodes.PlanChanged);
        Assert.Contains(result.Issues, issue => issue.Code == OperationPreflightCodes.GameRunning);
    }

    [Fact]
    public async Task BackupCatalogListsOnlyOwnedDataAndDeletionRequiresConfirmation()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, "backup");
        temp.File("backup/.rhi-linux/backups/tx1/bin/dxgi.dll", "backup");
        temp.File("backup/.rhi-linux/transactions/tx1.json",
            """{"id":"tx1","action":"Install ReShade","state":"Completed","startedUtc":"2026-08-08T01:42:00Z","operations":[]}""");
        var catalog = new BackupRecoveryCatalog();

        var backup = Assert.Single(await catalog.ListBackupsAsync(game));
        Assert.Equal("Install ReShade", backup.Operation);
        Assert.Equal(1, backup.FileCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.DeleteBackupAsync(game, backup.Id, false));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.DeleteBackupAsync(game, "../foreign", true));
        await catalog.DeleteBackupAsync(game, backup.Id, true);
        Assert.Empty(await catalog.ListBackupsAsync(game));
    }

    [Fact]
    public async Task RecoveryCatalogExplainsJournalWithoutExposingRawJson()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, "recovery");
        temp.File("recovery/.rhi-linux/transactions/tx2.json",
            """{"id":"tx2","action":"Update OptiScaler","state":"inProgress","operations":[{"status":"completed"},{"status":"pending"}]}""");
        temp.File("recovery/.rhi-linux/transactions/tx2.snapshots/file.bin", "original");

        var entry = Assert.Single(await new BackupRecoveryCatalog().ListInterruptedAsync(game));

        Assert.Equal(1, entry.CompletedSteps);
        Assert.Equal(1, entry.PendingSteps);
        Assert.True(entry.BackupAvailable);
        Assert.Contains("Restore", entry.RecommendedAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryCatalogIgnoresTerminalJournalStatesRegardlessOfCasing()
    {
        using var temp = new TestDirectory();
        var game = Game(temp, "terminal-recovery");
        temp.File("terminal-recovery/.rhi-linux/transactions/completed.json",
            """{"id":"completed","state":"completed","operations":[]}""");
        temp.File("terminal-recovery/.rhi-linux/transactions/rolled-back.json",
            """{"id":"rolled-back","state":"rolledBack","operations":[]}""");

        Assert.Empty(await new BackupRecoveryCatalog().ListInterruptedAsync(game));
    }

    [Fact]
    public async Task TroubleshootingReportSanitizesHomeAndSecrets()
    {
        using var temp = new TestDirectory();
        var paths = Paths(temp);
        var report = await new TroubleshootingReportService().CreateAsync(new(
            paths, [], 2, LibraryMetrics.Empty, 2, "ok", "enabled", "success", 0,
            [new("heroic", "ReadFailed", SourceDiagnosticSeverity.Warning,
                $"token=abc123 path={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Games/Foo")]));

        Assert.DoesNotContain("abc123", report, StringComparison.Ordinal);
        Assert.Contains("token=<redacted>", report, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/", report,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderSpecificCustomRootsDoNotLeakIntoOtherProviders()
    {
        using var temp = new TestDirectory();
        var steamRoot = temp.Directory("custom-steam");
        var heroicRoot = temp.Directory("custom-heroic");
        var context = SourceRootDiscovery.CreateContext(temp.Path,
            customRootsByProvider: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["steam"] = [steamRoot],
                ["heroic"] = [heroicRoot]
            });

        var steam = await new SteamGameSourceProvider().DiscoverRootsAsync(context);
        var heroic = await new HeroicGameSourceProvider().DiscoverRootsAsync(context);

        Assert.Contains(steam, root => root.CanonicalPath == GameIdentity.NormalizePath(steamRoot));
        Assert.DoesNotContain(steam, root => root.CanonicalPath == GameIdentity.NormalizePath(heroicRoot));
        Assert.Contains(heroic, root => root.CanonicalPath == GameIdentity.NormalizePath(heroicRoot));
        Assert.DoesNotContain(heroic, root => root.CanonicalPath == GameIdentity.NormalizePath(steamRoot));
    }

    private static XdgPaths Paths(TestDirectory temp) => new(temp.Path, new Dictionary<string, string?>
    {
        ["XDG_CONFIG_HOME"] = temp.Combine("xdg-config"),
        ["XDG_DATA_HOME"] = temp.Combine("xdg-data"),
        ["XDG_CACHE_HOME"] = temp.Combine("xdg-cache")
    });

    private static InstalledGame Game(TestDirectory temp, string name)
    {
        var root = temp.Directory(name);
        var executable = temp.Pe($"{name}/Game.exe");
        var source = new SourceGameRecord("heroic", GameStore.Gog, GameLauncher.Heroic, name, name,
            root, executable, null, null, GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine,
            temp.Combine($"{name}.json"), null, DateTimeOffset.UtcNow,
            new Dictionary<string, string>(), []);
        return new(new(name), name, GameStore.Gog, GameLauncher.Heroic, name, null, root, executable, null,
            root, GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine, [source], null, [],
            DetectionConfidence.High, Candidates: [new(executable, 100, DetectionConfidence.High,
                PeArchitecture.X64, new FileInfo(executable).Length, ["Main game executable"])]);
    }

    private sealed class FixedRunningDetector(RunningGameState state) : IRunningGameDetector
    {
        public Task<RunningGameResult> DetectAsync(DeploymentPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RunningGameResult(state, state == RunningGameState.NotDetected ? [] : [42], state.ToString()));
    }
}
