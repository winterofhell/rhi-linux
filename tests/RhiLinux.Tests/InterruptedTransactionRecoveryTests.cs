using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RhiLinux.Core;

namespace RhiLinux.Tests;

public sealed class InterruptedTransactionRecoveryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public async Task InterruptedBackupAndCopyRestoreOriginalAndRequireFreshPlan()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var original = Encoding.UTF8.GetBytes("original game file");
        var deployed = Encoding.UTF8.GetBytes("interrupted managed file");
        var target = temp.Combine("game", "dxgi.dll");
        var backup = temp.Combine("game", ".rhi-linux", "backups", "interrupted", "dxgi.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, deployed);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllBytesAsync(backup, original);
        var transactionId = "interrupted-backup-copy";

        var journalPath = await WriteJournalAsync(game, transactionId, 2, "inProgress",
            [
                new(Relative(game, target), original),
                new(Relative(game, backup), null),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.Backup, target, backup, "completed",
                [new(Relative(game, target), false, null), new(Relative(game, backup), true, Hash(original))]),
                new(1, DeploymentOperationType.Copy, target, temp.Combine("staging", "managed.dll"), "inProgress",
                [new(Relative(game, target), true, Hash(deployed))])
            ]);

        var freshSource = temp.File("staging/fresh.dll", "fresh deployment");
        var freshTarget = temp.Combine("game", "fresh.dll");
        var interruptedResult = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "fresh-plan-before-rescan", freshSource, freshTarget), false);

        Assert.False(interruptedResult.Succeeded);
        Assert.True(interruptedResult.RolledBack);
        Assert.Contains("Rescan", interruptedResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(freshTarget));
        Assert.Equal("rolledBack", await JournalStateAsync(journalPath));

        var freshResult = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "fresh-plan-after-rescan", freshSource, freshTarget), false);
        Assert.True(freshResult.Succeeded, freshResult.Error);
        Assert.Equal(await File.ReadAllBytesAsync(freshSource), await File.ReadAllBytesAsync(freshTarget));
    }

    [Fact]
    public async Task InterruptedIniWriteRestoresExactPreimage()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var original = Encoding.UTF8.GetBytes("[User]\nKeep=1\n[Plugins]\nLoadReshade=auto\n");
        var modified = Encoding.UTF8.GetBytes("[User]\nKeep=1\n[Plugins]\nLoadReshade=true\n");
        var ini = temp.Combine("game", "OptiScaler.ini");
        await File.WriteAllBytesAsync(ini, modified);
        var transactionId = "interrupted-ini";
        await WriteJournalAsync(game, transactionId, 2, "inProgress",
            [
                new(Relative(game, ini), original),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.WriteIniValue, ini, null, "inProgress",
                    [new(Relative(game, ini), true, Hash(modified))], "Plugins:LoadReshade=true")
            ]);

        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "after-ini-recovery", temp.File("staging/next.dll", "next"), temp.Combine("game", "next.dll")),
            false);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(original, await File.ReadAllBytesAsync(ini));
    }

    [Fact]
    public async Task UnexpectedFileHashBlocksRecoveryWithoutTouchingIt()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var expectedManaged = Encoding.UTF8.GetBytes("expected interrupted bytes");
        var unknown = Encoding.UTF8.GetBytes("foreign bytes written later");
        var target = temp.Combine("game", "dxgi.dll");
        await File.WriteAllBytesAsync(target, unknown);
        var transactionId = "unexpected-recovery-file";
        var journalPath = await WriteJournalAsync(game, transactionId, 2, "inProgress",
            [
                new(Relative(game, target), null),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.Copy, target, temp.Combine("staging", "old.dll"), "inProgress",
                    [new(Relative(game, target), true, Hash(expectedManaged))])
            ]);
        var nextTarget = temp.Combine("game", "next.dll");

        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "must-not-run", temp.File("staging/next.dll", "next"), nextTarget), false);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Contains("unexpected file", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unknown, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(nextTarget));
        Assert.Equal("rollbackFailed", await JournalStateAsync(journalPath));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(journalPath)!, "*.json"));
    }

    [Fact]
    public async Task DryRunReportsInterruptedJournalWithoutRecoveringIt()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var managed = Encoding.UTF8.GetBytes("interrupted managed bytes");
        var target = temp.Combine("game", "dxgi.dll");
        await File.WriteAllBytesAsync(target, managed);
        var journalPath = await WriteJournalAsync(game, "dry-run-interruption", 2, "inProgress",
            [
                new(Relative(game, target), null),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.Copy, target, temp.Combine("staging", "old.dll"), "inProgress",
                    [new(Relative(game, target), true, Hash(managed))])
            ]);
        var nextTarget = temp.Combine("game", "next.dll");
        var plan = CopyPlan(game, "dry-run-plan", temp.File("staging/next.dll", "next"), nextTarget);

        var result = await new DeploymentExecutor().ExecuteAsync(plan, true);

        Assert.False(result.Succeeded);
        Assert.True(result.DryRun);
        Assert.Contains("interrupted", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(managed, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(nextTarget));
        Assert.Equal("inProgress", await JournalStateAsync(journalPath));
    }

    [Fact]
    public async Task ManifestCommitMarkerFinalizesJournalAndDoesNotRollBackFiles()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var committedId = "committed-before-journal-status";
        var manifestPath = temp.Combine("game", ".rhi-linux", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new GameManifest
        {
            AppId = game.AppId,
            UpdatedUtc = DateTimeOffset.UtcNow,
            TransactionIds = [committedId]
        }, JsonOptions));
        var journalPath = await WriteJournalAsync(game, committedId, 1, "inProgress", [],
            [new(0, DeploymentOperationType.WriteManifest, manifestPath, null, "inProgress", [])]);
        var nextSource = temp.File("staging/next.dll", "next");
        var nextTarget = temp.Combine("game", "next.dll");

        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "after-committed-journal", nextSource, nextTarget), false);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(nextTarget));
        Assert.Equal("completed", await JournalStateAsync(journalPath));
        Assert.Contains(result.Messages, message => message.Contains("Finalized committed transaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UncommittedSchemaOneJournalFailsClosed()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var interruptedBytes = Encoding.UTF8.GetBytes("legacy transaction output");
        var interruptedTarget = temp.Combine("game", "dxgi.dll");
        await File.WriteAllBytesAsync(interruptedTarget, interruptedBytes);
        await WriteJournalAsync(game, "legacy-uncommitted", 1, "inProgress", [],
            [
                new(0, DeploymentOperationType.Copy, interruptedTarget, temp.Combine("staging", "legacy.dll"),
                    "completed", [])
            ]);
        var nextTarget = temp.Combine("game", "next.dll");

        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "after-legacy-journal", temp.File("staging/next.dll", "next"), nextTarget), false);

        Assert.False(result.Succeeded);
        Assert.Contains("schema 1", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(interruptedBytes, await File.ReadAllBytesAsync(interruptedTarget));
        Assert.False(File.Exists(nextTarget));
    }

    [Fact]
    public async Task InProgressOperationStillAtPreStateCanBeRecoveredWithoutInvalidatingPlan()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var interruptedTarget = temp.Combine("game", "never-written.dll");
        var expected = Encoding.UTF8.GetBytes("never committed");
        await WriteJournalAsync(game, "crash-before-copy", 2, "inProgress",
            [
                new(Relative(game, interruptedTarget), null),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.Copy, interruptedTarget, temp.Combine("staging", "old.dll"), "inProgress",
                    [new(Relative(game, interruptedTarget), true, Hash(expected))])
            ]);
        var nextSource = temp.File("staging/next.dll", "next");
        var nextTarget = temp.Combine("game", "next.dll");

        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "after-no-effect-crash", nextSource, nextTarget), false);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(interruptedTarget));
        Assert.True(File.Exists(nextTarget));
    }

    [Fact]
    public async Task RollbackInverseOperationsNeverDisplaceUnexpectedFiles()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var plan = CopyPlan(game, "inverse-safety", temp.File("staging/source.dll", "source"),
            temp.Combine("game", "unused.dll"));

        var uncommittedTarget = temp.File("game/uncommitted.dll", "foreign concurrent file");
        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeExecutorAsync(
            "RemoveTransactionFileAsync", plan, uncommittedTarget, null));
        Assert.Equal("foreign concurrent file", await File.ReadAllTextAsync(uncommittedTarget));

        var restoreTarget = temp.File("game/restore-target.dll", "foreign restore target");
        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeExecutorAsync(
            "RestoreBytesAsync", plan, restoreTarget, Encoding.UTF8.GetBytes("original"), Hash(Encoding.UTF8.GetBytes("managed"))));
        Assert.Equal("foreign restore target", await File.ReadAllTextAsync(restoreTarget));

        var backup = temp.File("game/.rhi-linux/backups/inverse/original.dll", "trusted backup");
        var occupiedTarget = temp.File("game/occupied.dll", "foreign occupied target");
        var backupHash = Hash(await File.ReadAllBytesAsync(backup));
        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeExecutorAsync(
            "RestoreMovedFileAsync", plan, backup, occupiedTarget, backupHash));
        Assert.Equal("trusted backup", await File.ReadAllTextAsync(backup));
        Assert.Equal("foreign occupied target", await File.ReadAllTextAsync(occupiedTarget));

        var movedTarget = temp.File("game/moved-target.dll", "transaction bytes");
        var occupiedSource = temp.File("game/move-source.dll", "foreign source");
        var movedTargetHash = Hash(await File.ReadAllBytesAsync(movedTarget));
        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeExecutorAsync(
            "UndoMoveAsync", plan, movedTarget, occupiedSource, movedTargetHash));
        Assert.Equal("transaction bytes", await File.ReadAllTextAsync(movedTarget));
        Assert.Equal("foreign source", await File.ReadAllTextAsync(occupiedSource));
    }

    [Fact]
    public async Task ConcurrentCopyTargetIsPreservedAndFailedUndoIsNotReportedAsRolledBack()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var source = temp.Combine("staging", "large-source.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await using (var output = File.Create(source)) output.SetLength(16 * 1024 * 1024);
        var target = temp.Combine("game", "concurrent.dll");
        const string transactionId = "copy-concurrent-target";
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transactionId))).ToLowerInvariant();
        var work = Path.Combine(game.GameRoot, ".rhi-linux", "transactions", stem + ".work");
        Directory.CreateDirectory(work);
        using var watcher = new FileSystemWatcher(work, "0000-copy.tmp")
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName
        };
        var targetCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) =>
        {
            try
            {
                File.WriteAllText(target, "foreign concurrent target");
                targetCreated.TrySetResult();
            }
            catch (Exception exception) { targetCreated.TrySetException(exception); }
        };

        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, transactionId, source, target), false);
        await targetCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Contains("Rollback errors", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("foreign concurrent target", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task PlanPathThroughNestedSymlinkIsRejectedBeforeMetadataWrite()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var outside = temp.Directory("outside");
        var link = Path.Combine(game.GameRoot, "nested-link");
        if (!TryCreateDirectorySymlink(link, outside)) return;
        var outsideTarget = Path.Combine(outside, "escaped.dll");
        var plan = CopyPlan(game, "symlink-plan", temp.File("staging/source.dll", "source"),
            Path.Combine(link, "escaped.dll"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeploymentExecutor().ExecuteAsync(plan, false));

        Assert.False(File.Exists(outsideTarget));
        Assert.False(Directory.Exists(Path.Combine(game.GameRoot, ".rhi-linux")));
    }

    [Fact]
    public async Task JournalPathThroughNestedSymlinkFailsClosedWithoutTouchingOutsideFile()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var outside = temp.Directory("outside");
        var outsideTarget = temp.File("outside/victim.dll", "outside foreign file");
        var link = Path.Combine(game.GameRoot, "nested-link");
        if (!TryCreateDirectorySymlink(link, outside)) return;
        var escapedTarget = Path.Combine(link, "victim.dll");
        var managed = Encoding.UTF8.GetBytes("interrupted managed bytes");
        await WriteJournalAsync(game, "symlink-journal", 2, "inProgress",
            [
                new(Path.Combine("nested-link", "victim.dll"), null),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.Copy, escapedTarget, temp.Combine("staging", "old.dll"), "inProgress",
                    [new(Path.Combine("nested-link", "victim.dll"), true, Hash(managed))])
            ]);

        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "after-symlink-journal", temp.File("staging/next.dll", "next"),
                temp.Combine("game", "next.dll")), false);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("outside foreign file", await File.ReadAllTextAsync(outsideTarget));
        Assert.False(File.Exists(temp.Combine("game", "next.dll")));
    }

    [Fact]
    public async Task JournalSnapshotThroughNestedSymlinkIsRejectedBeforeItIsRead()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var outside = temp.Directory("outside");
        var link = Path.Combine(game.GameRoot, "snapshot-link");
        if (!TryCreateDirectorySymlink(link, outside)) return;
        var original = Encoding.UTF8.GetBytes("original preimage");
        var managed = Encoding.UTF8.GetBytes("interrupted managed state");
        var outsideSnapshot = Path.Combine(outside, "original.bin");
        await File.WriteAllBytesAsync(outsideSnapshot, original);
        var target = temp.Combine("game", "managed.dll");
        await File.WriteAllBytesAsync(target, managed);
        var journalPath = await WriteJournalAsync(game, "symlink-snapshot", 2, "inProgress",
            [
                new(Relative(game, target), original),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.Copy, target, temp.Combine("staging", "old.dll"), "inProgress",
                    [new(Relative(game, target), true, Hash(managed))])
            ]);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        journal["originalFiles"]!.AsArray()[0]!["snapshotRelativePath"] =
            Path.Combine("snapshot-link", "original.bin");
        await File.WriteAllTextAsync(journalPath, journal.ToJsonString(JsonOptions));

        var nextTarget = temp.Combine("game", "next.dll");
        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "after-symlink-snapshot", temp.File("staging/next.dll", "next"), nextTarget), false);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal(original, await File.ReadAllBytesAsync(outsideSnapshot));
        Assert.Equal(managed, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(nextTarget));
    }

    [Fact]
    public async Task RecoveryRemovesDeterministicTransactionPrivateCrashTemp()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var interruptedTarget = temp.Combine("game", "never-written.dll");
        const string interruptedId = "crash-temp-cleanup";
        await WriteJournalAsync(game, interruptedId, 2, "inProgress",
            [
                new(Relative(game, interruptedTarget), null),
                new(Path.Combine(".rhi-linux", "manifest.json"), null)
            ],
            [
                new(0, DeploymentOperationType.Copy, interruptedTarget, temp.Combine("staging", "old.dll"), "inProgress",
                    [new(Relative(game, interruptedTarget), true, Hash(Encoding.UTF8.GetBytes("never committed")))])
            ]);
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(interruptedId))).ToLowerInvariant();
        var work = temp.Directory("game", ".rhi-linux", "transactions", stem + ".work");
        var crashTemp = Path.Combine(work, "0000-copy.tmp");
        await File.WriteAllTextAsync(crashTemp, "partial copy");

        var nextTarget = temp.Combine("game", "next.dll");
        var result = await new DeploymentExecutor().ExecuteAsync(
            CopyPlan(game, "after-crash-temp", temp.File("staging/next.dll", "next"), nextTarget), false);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(crashTemp));
        Assert.True(File.Exists(nextTarget));
    }

    [Fact]
    public async Task JournalSnapshotCancellationCleansCompletedSnapshotFiles()
    {
        using var temp = new TestDirectory();
        var game = Game(temp);
        var first = temp.File("game/a.ini", "first preimage");
        var second = temp.File("game/z.ini", "second preimage");
        const string id = "snapshot-cancellation";
        var plan = new DeploymentPlan
        {
            Id = id,
            AppId = game.AppId,
            GameRoot = game.GameRoot,
            DeploymentDirectory = game.DeploymentDirectory,
            Action = "snapshot cancellation test",
            Operations =
            {
                new(DeploymentOperationType.WriteIniValue, first, Value: "Plugins:A=true"),
                new(DeploymentOperationType.WriteIniValue, second, Value: "Plugins:B=true")
            }
        };
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        var transactionDirectory = temp.Directory("game", ".rhi-linux", "transactions");
        var snapshotDirectory = Path.Combine(transactionDirectory, stem + ".snapshots");
        using var cancellation = new CancellationTokenSource();
        var snapshotCount = 0;
        Action snapshotCompleted = () =>
        {
            snapshotCount++;
            cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeExecutorAsync(
            "CreateJournalAsync", plan, cancellation.Token, snapshotCompleted));

        Assert.Equal(1, snapshotCount);
        Assert.False(Directory.Exists(snapshotDirectory));
        Assert.Empty(Directory.EnumerateFiles(transactionDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    private static DeploymentPlan CopyPlan(SteamGame game, string id, string source, string target) => new()
    {
        Id = id,
        AppId = game.AppId,
        GameRoot = game.GameRoot,
        DeploymentDirectory = game.DeploymentDirectory,
        Action = "test deployment",
        Operations = { new(DeploymentOperationType.Copy, target, source, Component: ComponentKind.ReShade) }
    };

    private static async Task<string> WriteJournalAsync(
        SteamGame game,
        string id,
        int schemaVersion,
        string state,
        IReadOnlyList<SnapshotSpec> snapshots,
        IReadOnlyList<OperationSpec> operations)
    {
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        var transactionDirectory = Path.Combine(game.GameRoot, ".rhi-linux", "transactions");
        var snapshotDirectory = Path.Combine(transactionDirectory, stem + ".snapshots");
        Directory.CreateDirectory(transactionDirectory);
        var originalFiles = new List<JournalSnapshot>();
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (snapshot.OriginalBytes is null)
            {
                originalFiles.Add(new(snapshot.RelativePath, false, null, null));
                continue;
            }
            Directory.CreateDirectory(snapshotDirectory);
            var snapshotPath = Path.Combine(snapshotDirectory, $"{index:D4}.bin");
            await File.WriteAllBytesAsync(snapshotPath, snapshot.OriginalBytes);
            originalFiles.Add(new(snapshot.RelativePath, true, Hash(snapshot.OriginalBytes),
                Path.GetRelativePath(game.GameRoot, snapshotPath)));
        }
        var journal = new TestJournal
        {
            SchemaVersion = schemaVersion,
            Id = id,
            AppId = game.AppId,
            Action = "simulated interrupted transaction",
            State = state,
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTimeOffset.UtcNow,
            OriginalFiles = originalFiles,
            Operations = operations.Select(operation => new JournalOperation
            {
                Index = operation.Index,
                Type = operation.Type.ToString(),
                Target = operation.Target,
                Source = operation.Source,
                Value = operation.Value,
                Description = operation.Type.ToString(),
                Status = operation.Status,
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpectedFiles = operation.ExpectedFiles
            }).ToList()
        };
        var journalPath = Path.Combine(transactionDirectory, stem + ".json");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(journal, JsonOptions));
        return journalPath;
    }

    private static async Task<string> JournalStateAsync(string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return document.RootElement.GetProperty("state").GetString()!;
    }

    private static string Relative(SteamGame game, string path) => Path.GetRelativePath(game.GameRoot, path);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Task InvokeExecutorAsync(string methodName, params object?[] arguments)
    {
        var method = typeof(DeploymentExecutor).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(candidate => candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length) ??
            throw new MissingMethodException(typeof(DeploymentExecutor).FullName, methodName);
        return (Task)(method.Invoke(null, arguments) ??
            throw new InvalidOperationException($"{methodName} did not return a task."));
    }

    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SteamGame Game(TestDirectory temp)
    {
        var root = temp.Directory("game");
        var executable = temp.Pe("game/Game.exe");
        return new(42, "Game", temp.Path, temp.Path, root, temp.Combine("compatdata", "42", "pfx"),
            executable, root, DetectionConfidence.High, "test", GameEngine.Unknown, []);
    }

    private sealed record SnapshotSpec(string RelativePath, byte[]? OriginalBytes);
    private sealed record ExpectedSpec(string RelativePath, bool Exists, string? Sha256);
    private sealed record OperationSpec(
        int Index,
        DeploymentOperationType Type,
        string Target,
        string? Source,
        string Status,
        IReadOnlyList<ExpectedSpec> ExpectedFiles,
        string? Value = null);
    private sealed record JournalSnapshot(
        string RelativePath,
        bool Existed,
        string? Sha256,
        string? SnapshotRelativePath);

    private sealed class TestJournal
    {
        public int SchemaVersion { get; set; }
        public required string Id { get; set; }
        public uint AppId { get; set; }
        public required string Action { get; set; }
        public required string State { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public string? Error { get; set; }
        public IReadOnlyList<string> RollbackErrors { get; set; } = [];
        public IReadOnlyList<JournalSnapshot> OriginalFiles { get; set; } = [];
        public IReadOnlyList<JournalOperation> Operations { get; set; } = [];
    }

    private sealed class JournalOperation
    {
        public int Index { get; set; }
        public required string Type { get; set; }
        public required string Target { get; set; }
        public string? Source { get; set; }
        public string? Value { get; set; }
        public required string Description { get; set; }
        public required string Status { get; set; }
        public DateTimeOffset? StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string? Error { get; set; }
        public IReadOnlyList<ExpectedSpec> ExpectedFiles { get; set; } = [];
    }
}
