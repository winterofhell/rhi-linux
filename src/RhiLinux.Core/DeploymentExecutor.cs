using System.Security.Cryptography;
using System.Text.Json;

namespace RhiLinux.Core;

public interface IDeploymentExecutor
{
    Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default);
}

public sealed class DeploymentExecutor : IDeploymentExecutor
{
    private const string MetadataDirectoryName = ".rhi-linux";
    private const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ExecutionResult> ExecuteAsync(DeploymentPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        ValidatePlanPathSafety(plan);
        var messages = plan.Operations.Select(Describe).ToList();
        if (dryRun)
        {
            var interruption = await InspectInterruptedTransactionsAsync(plan, cancellationToken);
            if (interruption is not null)
                return new ExecutionResult(false, true, false, messages, interruption);
            ValidatePlan(plan);
            return new ExecutionResult(true, true, false, messages);
        }

        if (plan.Operations.Count == 0)
            return new ExecutionResult(true, false, false, ["No filesystem changes required."]);

        ValidatePlan(plan);
        FileStream transactionLock;
        try { transactionLock = AcquireTransactionLock(plan); }
        catch (IOException exception)
        {
            var metadataPathIsFile = File.Exists(Path.Combine(plan.GameRoot, MetadataDirectoryName));
            var error = metadataPathIsFile
                ? $"Deployment metadata cannot be created; no game file was changed. {exception.Message}"
                : $"Another deployment or recovery is already active for this game: {exception.Message}";
            return new ExecutionResult(false, false, false, [], error);
        }
        await using var heldTransactionLock = transactionLock;

        var recovery = await RecoverInterruptedTransactionsAsync(plan, cancellationToken);
        if (!recovery.Succeeded)
            return new ExecutionResult(false, false, false, recovery.Messages, recovery.Error);
        if (recovery.FilesChanged)
            return new ExecutionResult(false, false, true, recovery.Messages,
                "An interrupted transaction was rolled back safely. Rescan the game and build a fresh deployment plan before continuing.");

        ValidatePlan(plan);
        var journal = await CreateJournalAsync(plan, cancellationToken);
        var undo = new Stack<Func<Task>>();
        var completed = recovery.Messages.ToList();
        var activeOperation = -1;
        try
        {
            await SaveJournalAsync(plan, journal, cancellationToken);
            var manifest = await LoadManifestAsync(plan, cancellationToken);
            for (var index = 0; index < plan.Operations.Count; index++)
            {
                var operation = plan.Operations[index];
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.Type == DeploymentOperationType.WriteManifest) continue;
                activeOperation = index;
                await VerifyOperationPreStateAsync(plan, journal, index, operation, cancellationToken);
                journal.Operations[index].ExpectedFiles = await PredictOperationFileStatesAsync(plan, operation, cancellationToken);
                StartJournalOperation(journal, index);
                await SaveJournalAsync(plan, journal, cancellationToken);
                if (operation.Type is DeploymentOperationType.Download or DeploymentOperationType.VerifyPe or
                    DeploymentOperationType.ExtractArchive)
                {
                    SkipJournalOperation(journal, index);
                    await SaveJournalAsync(plan, journal, cancellationToken);
                    continue;
                }
                await ExecuteOperationAsync(plan, manifest, operation, undo,
                    index, journal.Operations[index].ExpectedFiles, cancellationToken);
                await VerifyExpectedFileStatesAsync(plan, journal.Operations[index].ExpectedFiles, cancellationToken);
                completed.Add(Describe(operation));
                CompleteJournalOperation(journal, index);
                await SaveJournalAsync(plan, journal, cancellationToken);
            }

            await VerifyExpectedComponentStatesAsync(plan, manifest, cancellationToken);
            manifest.AppId = plan.AppId;
            manifest.UpdatedUtc = DateTimeOffset.UtcNow;
            manifest.TransactionIds.Add(plan.Id);
            var manifestOperation = plan.Operations.FindIndex(x => x.Type == DeploymentOperationType.WriteManifest);
            if (manifestOperation < 0)
            {
                manifestOperation = journal.Operations.Count;
                journal.Operations.Add(TransactionJournalOperation.InternalManifest(manifestOperation, ManifestPath(plan)));
            }
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await VerifyOperationPreStateAsync(plan, journal, manifestOperation,
                new DeploymentOperation(DeploymentOperationType.WriteManifest, ManifestPath(plan)), cancellationToken);
            journal.Operations[manifestOperation].ExpectedFiles =
                [CreateExpectedFileState(plan, ManifestPath(plan), true, HashBytes(manifestBytes))];
            StartJournalOperation(journal, manifestOperation);
            await SaveJournalAsync(plan, journal, cancellationToken);
            activeOperation = manifestOperation;
            await SaveManifestTransactionalAsync(plan, manifest, undo, manifestOperation, cancellationToken);
            await VerifyExpectedFileStatesAsync(plan, journal.Operations[manifestOperation].ExpectedFiles, cancellationToken);
            completed.Add("Atomically update the game manifest");
            CompleteJournalOperation(journal, manifestOperation);
            journal.State = JournalState.Completed;
            journal.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveJournalAsync(plan, journal, cancellationToken);
            await TryDeleteSnapshotFilesAsync(plan.GameRoot, journal);
            return new ExecutionResult(true, false, false, completed);
        }
        catch (Exception exception)
        {
            if (activeOperation >= 0 && activeOperation < journal.Operations.Count &&
                journal.Operations[activeOperation].Status is JournalOperationStatus.InProgress or JournalOperationStatus.Pending)
            {
                journal.Operations[activeOperation].Status = JournalOperationStatus.Failed;
                journal.Operations[activeOperation].Error = exception.Message;
            }
            journal.State = JournalState.RollingBack;
            journal.Error = exception.Message;
            journal.UpdatedUtc = DateTimeOffset.UtcNow;
            _ = await TrySaveJournalAsync(plan, journal);
            var rollbackErrors = new List<string>();
            while (undo.TryPop(out var action))
            {
                try { await action(); }
                catch (Exception rollbackException) { rollbackErrors.Add(rollbackException.Message); }
            }
            foreach (var operation in journal.Operations.Where(x => x.Status == JournalOperationStatus.Completed))
                operation.Status = rollbackErrors.Count == 0
                    ? JournalOperationStatus.RolledBack
                    : JournalOperationStatus.RollbackFailed;
            foreach (var operation in journal.Operations.Where(x => x.Status == JournalOperationStatus.InProgress))
                operation.Status = rollbackErrors.Count == 0
                    ? JournalOperationStatus.RolledBack
                    : JournalOperationStatus.RollbackFailed;
            journal.State = rollbackErrors.Count == 0 ? JournalState.RolledBack : JournalState.RollbackFailed;
            journal.RollbackErrors = rollbackErrors;
            journal.UpdatedUtc = DateTimeOffset.UtcNow;
            _ = await TrySaveJournalAsync(plan, journal);
            if (rollbackErrors.Count == 0) await TryDeleteSnapshotFilesAsync(plan.GameRoot, journal);
            var error = rollbackErrors.Count == 0
                ? exception.Message
                : $"{exception.Message} Rollback errors: {string.Join("; ", rollbackErrors)}";
            return new ExecutionResult(false, false, rollbackErrors.Count == 0, completed, error);
        }
    }

    public static string Describe(DeploymentOperation operation) => operation.Description ?? operation.Type switch
    {
        DeploymentOperationType.CreateDirectory => $"Create directory: {operation.Target}",
        DeploymentOperationType.Download => $"Download {operation.Source} to staging",
        DeploymentOperationType.VerifySha256 => $"Verify SHA-256: {operation.Target}",
        DeploymentOperationType.VerifyPe => $"Validate PE binary: {operation.Target}",
        DeploymentOperationType.ExtractArchive => $"Extract validated archive: {operation.Source}",
        DeploymentOperationType.Backup => $"Back up {operation.Target}",
        DeploymentOperationType.Copy => $"Copy {operation.Source} to {operation.Target}",
        DeploymentOperationType.TrackExistingFile => $"Adopt verified official file: {operation.Target}",
        DeploymentOperationType.Move => $"Move {operation.Source} to {operation.Target}",
        DeploymentOperationType.WriteIniValue => $"Set {operation.Value} in {operation.Target}",
        DeploymentOperationType.DeleteManagedFile => $"Remove managed file: {operation.Target}",
        DeploymentOperationType.DeleteVerifiedFile => $"Clean up verified managed leftover: {operation.Target}",
        DeploymentOperationType.ForgetOwnership => $"Clear stale ownership record: {operation.Target}",
        DeploymentOperationType.ClearConfigurationPatches => "Clear stale managed configuration state",
        DeploymentOperationType.RestoreBackup => $"Restore backup to {operation.Target}",
        DeploymentOperationType.VerifyFileState => $"Verify final file state: {operation.Target}",
        DeploymentOperationType.VerifyIniValue => $"Verify setting {operation.Value}",
        DeploymentOperationType.VerifyManagedLayout => $"Verify managed layout: {operation.Target}",
        DeploymentOperationType.VerifyComponentRemoved => $"Verify {operation.Component} removal and ownership cleanup",
        DeploymentOperationType.WriteManifest => "Atomically update the game manifest",
        _ => operation.Type.ToString()
    };

    private static void ValidatePlan(DeploymentPlan plan)
    {
        ValidatePlanPathSafety(plan);
        var root = NormalizeDirectory(plan.GameRoot);
        var deployment = Path.GetFullPath(plan.DeploymentDirectory);
        EnsureContained(root, deployment);
        var backedUpTargets = plan.Operations.Where(x => x.Type == DeploymentOperationType.Backup)
            .Select(x => Path.GetFullPath(x.Target)).ToHashSet(StringComparer.Ordinal);
        var explicitlyRemovedTargets = plan.Operations.Where(x => x.Type == DeploymentOperationType.DeleteManagedFile)
            .Concat(plan.Operations.Where(x => x.Type == DeploymentOperationType.DeleteVerifiedFile))
            .Select(x => Path.GetFullPath(x.Target)).ToHashSet(StringComparer.Ordinal);
        var vacatedTargets = plan.Operations.Select((operation, index) => (operation, index))
            .Where(x => x.operation.Type == DeploymentOperationType.Move && x.operation.Source is not null)
            .GroupBy(x => Path.GetFullPath(x.operation.Source!), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(item => item.index), StringComparer.Ordinal);
        foreach (var (operation, operationIndex) in plan.Operations.Select((operation, index) => (operation, index)))
        {
            if (operation.Type is DeploymentOperationType.Download or DeploymentOperationType.ExtractArchive) continue;
            EnsureContained(root, Path.GetFullPath(operation.Target));
            if (operation.Type is DeploymentOperationType.Move or DeploymentOperationType.Backup or
                DeploymentOperationType.RestoreBackup && operation.Source is not null)
                EnsureContained(root, Path.GetFullPath(operation.Source));
            if (operation.BackupPath is not null)
                EnsureContained(root, Path.GetFullPath(operation.BackupPath));
            if (operation.Type is DeploymentOperationType.Copy or DeploymentOperationType.Move &&
                File.Exists(operation.Target) && !backedUpTargets.Contains(Path.GetFullPath(operation.Target)) &&
                !explicitlyRemovedTargets.Contains(Path.GetFullPath(operation.Target)) &&
                (!vacatedTargets.TryGetValue(Path.GetFullPath(operation.Target), out var moveIndex) || moveIndex >= operationIndex))
                throw new InvalidOperationException($"Existing target lacks an explicit backup operation: {operation.Target}");
        }
    }

    private static void ValidatePlanPathSafety(DeploymentPlan plan)
    {
        EnsureSafeContainedPath(plan.GameRoot, plan.GameRoot);
        EnsureSafeContainedPath(plan.GameRoot, plan.DeploymentDirectory);
        var metadata = Path.Combine(plan.GameRoot, MetadataDirectoryName);
        EnsureSafeContainedPath(plan.GameRoot, metadata);
        EnsureSafeContainedPath(plan.GameRoot, Path.Combine(metadata, "transactions"));
        EnsureSafeContainedPath(plan.GameRoot, Path.Combine(metadata, "backups"));
        EnsureSafeContainedPath(plan.GameRoot, Path.Combine(metadata, "recovery"));
        foreach (var operation in plan.Operations)
        {
            if (operation.Type is DeploymentOperationType.Download or DeploymentOperationType.ExtractArchive or
                DeploymentOperationType.VerifyPe)
                continue;
            EnsureSafeContainedPath(plan.GameRoot, operation.Target);
            if (operation.Type is DeploymentOperationType.Move or DeploymentOperationType.RestoreBackup)
                EnsureSafeContainedPath(plan.GameRoot, operation.Source ??
                    throw new InvalidOperationException($"{operation.Type} operation has no source path."));
            if (operation.Type == DeploymentOperationType.Backup)
                EnsureSafeContainedPath(plan.GameRoot, operation.Source ?? BackupPath(plan, operation.Target));
            if (operation.BackupPath is not null)
                EnsureSafeContainedPath(plan.GameRoot, operation.BackupPath);
        }
    }

    private static async Task ExecuteOperationAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentOperation operation,
        Stack<Func<Task>> undo,
        int operationIndex,
        IReadOnlyList<TransactionExpectedFileState> expectedFiles,
        CancellationToken cancellationToken)
    {
        switch (operation.Type)
        {
            case DeploymentOperationType.CreateDirectory:
                if (!Directory.Exists(operation.Target))
                {
                    Directory.CreateDirectory(operation.Target);
                    undo.Push(() => { if (!Directory.EnumerateFileSystemEntries(operation.Target).Any()) Directory.Delete(operation.Target); return Task.CompletedTask; });
                }
                break;
            case DeploymentOperationType.Backup:
                await BackupAsync(plan, operation, undo,
                    ExpectedTransactionHash(plan, expectedFiles, operation.Source ?? BackupPath(plan, operation.Target)),
                    cancellationToken);
                break;
            case DeploymentOperationType.Copy:
                await CopyAsync(plan, manifest, operation, undo,
                    operationIndex, ExpectedTransactionHash(plan, expectedFiles, operation.Target), cancellationToken);
                break;
            case DeploymentOperationType.TrackExistingFile:
                await VerifySha256Async(operation, cancellationToken);
                await TrackAsync(plan, manifest, operation, cancellationToken);
                break;
            case DeploymentOperationType.Move:
                await MoveAsync(plan, manifest, operation, undo,
                    ExpectedTransactionHash(plan, expectedFiles, operation.Target));
                break;
            case DeploymentOperationType.WriteIniValue:
                await WriteIniAsync(plan, manifest, operation, undo,
                    operationIndex, ExpectedTransactionHash(plan, expectedFiles, operation.Target), cancellationToken);
                break;
            case DeploymentOperationType.DeleteManagedFile:
                await DeleteManagedAsync(plan, manifest, operation, undo, cancellationToken);
                break;
            case DeploymentOperationType.DeleteVerifiedFile:
                await DeleteVerifiedAsync(plan, operation, undo, cancellationToken);
                break;
            case DeploymentOperationType.ForgetOwnership:
                ForgetOwnership(plan, manifest, operation);
                break;
            case DeploymentOperationType.ClearConfigurationPatches:
                ClearConfigurationPatches(manifest, operation);
                break;
            case DeploymentOperationType.RestoreBackup:
                await RestoreAsync(plan, manifest, operation, undo,
                    ExpectedTransactionHash(plan, expectedFiles, operation.Target));
                break;
            case DeploymentOperationType.VerifySha256:
                await VerifySha256Async(operation, cancellationToken);
                break;
            case DeploymentOperationType.VerifyFileState:
                VerifyFileState(operation);
                break;
            case DeploymentOperationType.VerifyIniValue:
                VerifyIniValue(operation);
                break;
            case DeploymentOperationType.VerifyManagedLayout:
                await VerifyManagedLayoutAsync(plan, manifest, operation, cancellationToken);
                break;
            case DeploymentOperationType.VerifyComponentRemoved:
                VerifyComponentRemoved(manifest, operation);
                break;
        }
    }

    private static async Task VerifySha256Async(DeploymentOperation operation, CancellationToken token)
    {
        if (!File.Exists(operation.Target)) throw new FileNotFoundException("Final layout verification failed; file is missing.", operation.Target);
        if (string.IsNullOrWhiteSpace(operation.ExpectedSha256)) return;
        var actual = await HashFileAsync(operation.Target, token);
        var expected = operation.ExpectedSha256.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Final layout verification failed for {operation.Target}.");
    }

    private static void VerifyFileState(DeploymentOperation operation)
    {
        var expected = operation.Value ?? "exists";
        var exists = File.Exists(operation.Target);
        if (expected.Equals("exists", StringComparison.OrdinalIgnoreCase) && !exists)
            throw new FileNotFoundException("Final layout verification failed; file is missing.", operation.Target);
        if (expected.Equals("absent", StringComparison.OrdinalIgnoreCase) && exists)
            throw new InvalidDataException($"Final layout verification failed; unexpected file exists: {operation.Target}");
    }

    private static void VerifyIniValue(DeploymentOperation operation)
    {
        if (!File.Exists(operation.Target)) throw new FileNotFoundException("Managed INI is missing.", operation.Target);
        var (section, key, value) = ParseIniValue(operation.Value);
        var actual = IniDocument.Parse(File.ReadAllText(operation.Target)).Get(section, key);
        var matches = value == "<remove>"
            ? actual is null
            : string.Equals(actual, value, StringComparison.OrdinalIgnoreCase);
        if (!matches)
            throw new InvalidDataException($"Managed INI value {section}.{key} was not applied.");
    }

    private static async Task VerifyManagedLayoutAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentOperation operation,
        CancellationToken token)
    {
        var files = manifest.Files.Where(x => operation.Component is null || x.Component == operation.Component)
            .Where(file => ClassifyManagedFile(file) == ManagedFileClass.ImmutableRuntimeBinary).ToArray();
        if (files.Length == 0) throw new InvalidDataException("The installation has no managed ownership records.");
        foreach (var file in files)
        {
            var path = Path.Combine(plan.GameRoot, file.RelativePath);
            if (!File.Exists(path)) throw new FileNotFoundException("Managed layout is incomplete.", path);
            if (!(await HashFileAsync(path, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Managed layout hash mismatch: {file.RelativePath}");
        }
        if (operation.Source is not null && operation.Value is not null)
        {
            var proxy = Path.GetFileNameWithoutExtension(operation.Source);
            var generated = $"WINEDLLOVERRIDES=\"{proxy}=n,b\" %command%";
            if (!generated.Equals(operation.Value, StringComparison.Ordinal))
                throw new InvalidDataException("The Proton launch option does not match the selected compatibility filename.");
        }
    }

    private static async Task VerifyExpectedComponentStatesAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        CancellationToken token)
    {
        foreach (var expectation in plan.ExpectedComponentStates)
        {
            var files = manifest.Files.Where(file => file.Component == expectation.Component).ToArray();
            if (!expectation.Installed)
            {
                if (files.Length != 0)
                    throw new InvalidDataException(
                        $"The final ownership manifest still records {expectation.Component} as installed.");
                continue;
            }

            if (files.Length == 0)
                throw new InvalidDataException(
                    $"The final ownership manifest does not record {expectation.Component} as installed.");
            foreach (var file in RuntimeCriticalFiles(expectation.Component, files))
            {
                var path = ResolveRelativePath(plan.GameRoot, file.RelativePath);
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        $"The expected {expectation.Component} installation is incomplete.", path);
                var hash = await HashFileAsync(path, token);
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"The expected {expectation.Component} installation has a changed managed file: {file.RelativePath}");
            }
        }
    }

    private static IEnumerable<ManagedFile> RuntimeCriticalFiles(
        ComponentKind component,
        IEnumerable<ManagedFile> files) => files.Where(file =>
    {
        if (ClassifyManagedFile(file) != ManagedFileClass.ImmutableRuntimeBinary) return false;
        var name = Path.GetFileName(file.RelativePath);
        return component switch
        {
            ComponentKind.ReShade => name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) ||
                IsProxyName(name),
            ComponentKind.RenoDx => Path.GetExtension(name).Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(name).Equals(".addon32", StringComparison.OrdinalIgnoreCase),
            ComponentKind.OptiScaler => IsProxyName(name),
            ComponentKind.OptiPatcher => Path.GetExtension(name).Equals(".asi", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    });

    private static bool IsProxyName(string name) => name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("dbghelp.dll", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("wininet.dll", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase);

    private static ManagedFileClass ClassifyManagedFile(ManagedFile file)
    {
        if (file.FileClass != ManagedFileClass.Unknown) return file.FileClass;
        var name = Path.GetFileName(file.RelativePath);
        var extension = Path.GetExtension(name);
        if (extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".toml", StringComparison.OrdinalIgnoreCase))
            return name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("fakenvapi.ini", StringComparison.OrdinalIgnoreCase)
                ? ManagedFileClass.UserEditableConfiguration
                : ManagedFileClass.MutableConfiguration;
        if (file.RelativePath.Contains("/.rhi-linux/backups/", StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.StartsWith(".rhi-linux/backups/", StringComparison.OrdinalIgnoreCase))
            return ManagedFileClass.Backup;
        if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cache", StringComparison.OrdinalIgnoreCase))
            return ManagedFileClass.ManagedGeneratedFile;
        return ManagedFileClass.ImmutableRuntimeBinary;
    }

    private static async Task BackupAsync(
        DeploymentPlan plan,
        DeploymentOperation operation,
        Stack<Func<Task>> undo,
        string? transactionExpectedHash,
        CancellationToken token)
    {
        if (!File.Exists(operation.Target)) return;
        var backup = operation.Source ?? BackupPath(plan, operation.Target);
        var originalHash = await HashFileAsync(operation.Target, token);
        if (transactionExpectedHash is not null &&
            !originalHash.Equals(transactionExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Backup source changed after the transaction intent was recorded: {operation.Target}");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if (File.Exists(backup)) throw new IOException($"Backup already exists: {backup}");
        File.Move(operation.Target, backup);
        undo.Push(() => RestoreMovedFileAsync(plan, backup, operation.Target, originalHash));
    }

    private static async Task CopyAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentOperation operation,
        Stack<Func<Task>> undo,
        int operationIndex,
        string? transactionExpectedHash,
        CancellationToken token)
    {
        var source = operation.Source ?? throw new InvalidOperationException("Copy operation has no source.");
        if (!File.Exists(source)) throw new FileNotFoundException("Staged source is missing.", source);
        var directory = Path.GetDirectoryName(operation.Target)!;
        var createdDirectory = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        var temporary = OperationTemporaryPath(plan, operationIndex, "copy");
        EnsureSafeContainedPath(plan.GameRoot, temporary);
        string? committedHash = null;
        undo.Push(async () =>
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            await RemoveTransactionFileAsync(plan, operation.Target, committedHash);
            if (createdDirectory && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        });
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            var stagedHash = await HashFileAsync(temporary, token);
            if (transactionExpectedHash is not null &&
                !stagedHash.Equals(transactionExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Staged source changed after the transaction intent was recorded: {source}");
            if (!string.IsNullOrWhiteSpace(operation.ExpectedSha256))
            {
                var expected = NormalizeSha256(operation.ExpectedSha256);
                if (!stagedHash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Staged copy hash mismatch for {operation.Target}.");
            }
            File.Move(temporary, operation.Target);
            committedHash = stagedHash;
            await TrackAsync(plan, manifest, operation, token);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task MoveAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentOperation operation,
        Stack<Func<Task>> undo,
        string? transactionExpectedHash)
    {
        var source = operation.Source ?? throw new InvalidOperationException("Move operation has no source.");
        if (!File.Exists(source)) throw new FileNotFoundException("Move source is missing.", source);
        var sourceHash = await HashFileAsync(source, CancellationToken.None);
        if (transactionExpectedHash is not null &&
            !sourceHash.Equals(transactionExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Move source changed after the transaction intent was recorded: {source}");
        var relativeSource = Path.GetRelativePath(plan.GameRoot, source);
        var sourceRecord = manifest.Files.FirstOrDefault(x => x.RelativePath.Equals(relativeSource, StringComparison.Ordinal));
        Directory.CreateDirectory(Path.GetDirectoryName(operation.Target)!);
        File.Move(source, operation.Target);
        undo.Push(() => UndoMoveAsync(plan, operation.Target, source, sourceHash));
        manifest.Files.RemoveAll(x => x.RelativePath.Equals(relativeSource, StringComparison.Ordinal));
        await TrackMovedAsync(plan, manifest, operation, sourceRecord, CancellationToken.None);
    }

    private static async Task WriteIniAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentOperation operation,
        Stack<Func<Task>> undo,
        int operationIndex,
        string? transactionExpectedHash,
        CancellationToken token)
    {
        var original = File.Exists(operation.Target) ? await File.ReadAllBytesAsync(operation.Target, token) : null;
        if (original is not null && operation.BackupPath is not null && !File.Exists(operation.BackupPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(operation.BackupPath)!);
            var originalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            undo.Push(() => RemoveTransactionFileAsync(plan, operation.BackupPath, originalHash));
            await AtomicWriteAsync(plan.GameRoot, operation.BackupPath, original, token,
                OperationTemporaryPath(plan, operationIndex, "ini-backup"));
        }
        var (section, key, value) = ParseIniValue(operation.Value);
        var document = original is null ? new IniDocument() : IniDocument.Parse(original);
        if (document.HasFatalIssues)
            throw new InvalidDataException(
                $"OptiScaler configuration could not be updated. The INI contains fatal corruption at {operation.Target}.");
        if (!document.CanSafelyEditManagedKeys([(section, key)]))
            throw new InvalidDataException(
                "OptiScaler configuration could not be updated. The existing INI contains a malformed section header. " +
                "The original file was restored and no OptiScaler files remain installed.");
        var previous = document.Get(section, key);
        if (value == "<remove>") document.Remove(section, key);
        else document.Set(section, key, value);
        Directory.CreateDirectory(Path.GetDirectoryName(operation.Target)!);
        var result = document.ToUtf8Bytes();
        var resultHash = Convert.ToHexString(SHA256.HashData(result)).ToLowerInvariant();
        if (transactionExpectedHash is not null &&
            !resultHash.Equals(transactionExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"INI changed after the transaction intent was recorded: {operation.Target}");
        undo.Push(() => RestoreBytesAsync(plan, operation.Target, original, resultHash));
        await AtomicWriteAsync(plan.GameRoot, operation.Target, result, token,
            OperationTemporaryPath(plan, operationIndex, "ini"));
        var existingPatch = manifest.ConfigurationPatches.FirstOrDefault(x =>
            x.RelativePath.Equals(Path.GetRelativePath(plan.GameRoot, operation.Target), StringComparison.Ordinal) &&
            x.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (operation.ClearConfigurationPatch)
            manifest.ConfigurationPatches.RemoveAll(x => ReferenceEquals(x, existingPatch) || x == existingPatch);
        else
        {
            manifest.ConfigurationPatches.RemoveAll(x => x == existingPatch);
            manifest.ConfigurationPatches.Add(new(Path.GetRelativePath(plan.GameRoot, operation.Target), section, key,
                existingPatch?.PreviousValue ?? previous, value,
                operation.ConfigurationSchema ?? "plugins-v1", operation.ConfigurationVersion ?? "unknown"));
        }
        await TrackAsync(plan, manifest, operation, token);
    }

    private static async Task DeleteManagedAsync(DeploymentPlan plan, GameManifest manifest, DeploymentOperation operation, Stack<Func<Task>> undo, CancellationToken token)
    {
        var relative = Path.GetRelativePath(plan.GameRoot, operation.Target);
        var managed = manifest.Files.SingleOrDefault(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
        if (managed is null) throw new InvalidOperationException($"Refusing to remove an unowned file: {operation.Target}");
        if (!File.Exists(operation.Target)) { manifest.Files.Remove(managed); return; }
        var bytes = await File.ReadAllBytesAsync(operation.Target, token);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!hash.Equals(managed.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Managed file changed since installation: {operation.Target}");
        File.Delete(operation.Target);
        undo.Push(() => RestoreBytesAsync(plan, operation.Target, bytes, null));
        manifest.Files.Remove(managed);
    }

    private static async Task DeleteVerifiedAsync(
        DeploymentPlan plan,
        DeploymentOperation operation,
        Stack<Func<Task>> undo,
        CancellationToken token)
    {
        if (!File.Exists(operation.Target)) return;
        var bytes = await File.ReadAllBytesAsync(operation.Target, token);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation.ExpectedSha256) ||
            !hash.Equals(operation.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Verified leftover changed before cleanup: {operation.Target}");
        File.Delete(operation.Target);
        undo.Push(() => RestoreBytesAsync(plan, operation.Target, bytes, null));
    }

    private static void ForgetOwnership(DeploymentPlan plan, GameManifest manifest, DeploymentOperation operation)
    {
        var relative = Path.GetRelativePath(plan.GameRoot, operation.Target);
        manifest.Files.RemoveAll(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
    }

    private static void ClearConfigurationPatches(GameManifest manifest, DeploymentOperation operation)
    {
        if (operation.Component == ComponentKind.OptiScaler)
            manifest.ConfigurationPatches.Clear();
    }

    private static void VerifyComponentRemoved(GameManifest manifest, DeploymentOperation operation)
    {
        if (operation.Component is { } component && manifest.Files.Any(x => x.Component == component))
            throw new InvalidDataException($"Ownership records remain after removing {component}.");
    }

    private static async Task RestoreAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentOperation operation,
        Stack<Func<Task>> undo,
        string? transactionExpectedHash)
    {
        var source = operation.Source ?? throw new InvalidOperationException("Restore operation has no backup source.");
        if (string.IsNullOrWhiteSpace(operation.ExpectedSha256))
            throw new InvalidOperationException("Restore operation has no trusted backup SHA-256 digest.");
        if (!File.Exists(source)) throw new FileNotFoundException("Backup is missing.", source);
        if (File.Exists(operation.Target)) throw new IOException($"Restore target is occupied: {operation.Target}");
        var restoredHash = await HashFileAsync(source, CancellationToken.None);
        if (transactionExpectedHash is not null &&
            !restoredHash.Equals(transactionExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The backup changed after the transaction intent was recorded: {source}");
        if (!restoredHash.Equals(NormalizeSha256(operation.ExpectedSha256), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The recorded backup changed after the restore plan was created: {source}");
        File.Move(source, operation.Target);
        undo.Push(() => UndoMoveAsync(plan, operation.Target, source, restoredHash));
        var relative = Path.GetRelativePath(plan.GameRoot, operation.Target);
        manifest.Files.RemoveAll(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    private static async Task TrackMovedAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentOperation operation,
        ManagedFile? sourceRecord,
        CancellationToken token)
    {
        var component = operation.Component ?? sourceRecord?.Component;
        if (component is null) return;
        var relative = Path.GetRelativePath(plan.GameRoot, operation.Target);
        var targetRecord = manifest.Files.FirstOrDefault(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
        var prior = sourceRecord ?? targetRecord;
        var hash = await HashFileAsync(operation.Target, token);
        var backup = await ResolveBackupProvenanceAsync(plan, operation.BackupPath,
            prior?.BackupRelativePath, prior?.BackupSha256, token);
        manifest.Files.RemoveAll(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
        manifest.Files.Add(new ManagedFile(
            relative,
            component.Value,
            hash,
            operation.Value ?? prior?.Version,
            operation.SourceUrl ?? prior?.SourceUrl,
            backup.RelativePath,
            operation.SourceBlobSha256 ?? prior?.SourceBlobSha256,
            operation.SourceBundleSha256 ?? prior?.SourceBundleSha256,
            operation.BundleRelativePath ?? prior?.BundleRelativePath,
            backup.Sha256,
            prior?.FileClass ?? ClassifyPath(relative),
            operation.Purpose ?? prior?.Purpose,
            operation.Requirement != DeploymentFileRequirement.Required
                ? operation.Requirement
                : prior?.Requirement ?? DeploymentFileRequirement.Required));
    }

    private static async Task TrackAsync(DeploymentPlan plan, GameManifest manifest, DeploymentOperation operation, CancellationToken token)
    {
        if (operation.Component is null) return;
        var relative = Path.GetRelativePath(plan.GameRoot, operation.Target);
        var hash = await HashFileAsync(operation.Target, token);
        var previous = manifest.Files.FirstOrDefault(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
        var backup = await ResolveBackupProvenanceAsync(plan, operation.BackupPath,
            previous?.BackupRelativePath, previous?.BackupSha256, token);
        manifest.Files.RemoveAll(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
        manifest.Files.Add(new ManagedFile(
            relative,
            operation.Component.Value,
            hash,
            operation.Type == DeploymentOperationType.WriteIniValue ? previous?.Version : operation.Value,
            operation.SourceUrl ?? previous?.SourceUrl,
            backup.RelativePath,
            operation.SourceBlobSha256 ?? previous?.SourceBlobSha256,
            operation.SourceBundleSha256 ?? previous?.SourceBundleSha256,
            operation.BundleRelativePath ?? previous?.BundleRelativePath,
            backup.Sha256,
            previous?.FileClass ?? ClassifyPath(relative),
            operation.Purpose ?? previous?.Purpose,
            operation.Requirement != DeploymentFileRequirement.Required
                ? operation.Requirement
                : previous?.Requirement ?? DeploymentFileRequirement.Required));
    }

    private static ManagedFileClass ClassifyPath(string relativePath) =>
        ClassifyManagedFile(new ManagedFile(relativePath, default, string.Empty, null, null, null));

    private static async Task<(string? RelativePath, string? Sha256)> ResolveBackupProvenanceAsync(
        DeploymentPlan plan,
        string? backupPath,
        string? previousRelativePath,
        string? previousSha256,
        CancellationToken token)
    {
        if (backupPath is null) return (previousRelativePath, previousSha256);
        EnsureSafeContainedPath(plan.GameRoot, backupPath);
        if (!File.Exists(backupPath)) return (null, null);
        return (Path.GetRelativePath(plan.GameRoot, backupPath), await HashFileAsync(backupPath, token));
    }

    private static async Task<GameManifest> LoadManifestAsync(DeploymentPlan plan, CancellationToken token)
    {
        var path = ManifestPath(plan);
        if (!File.Exists(path)) return new GameManifest { AppId = plan.AppId };
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<GameManifest>(input, JsonOptions, token)
            ?? throw new InvalidDataException("Game manifest is empty.");
    }

    private static async Task SaveManifestTransactionalAsync(
        DeploymentPlan plan,
        GameManifest manifest,
        Stack<Func<Task>> undo,
        int operationIndex,
        CancellationToken token)
    {
        var path = ManifestPath(plan);
        var original = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var committedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        undo.Push(() => RestoreBytesAsync(plan, path, original, committedHash));
        await AtomicWriteAsync(plan.GameRoot, path, bytes, token,
            OperationTemporaryPath(plan, operationIndex, "manifest"));
    }

    private static async Task AtomicWriteAsync(
        string gameRoot,
        string path,
        byte[] bytes,
        CancellationToken token,
        string temporary)
    {
        EnsureSafeContainedPath(gameRoot, path);
        EnsureSafeContainedPath(gameRoot, temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value) =>
        value.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

    private static async Task RemoveTransactionFileAsync(DeploymentPlan plan, string path, string? expectedHash)
    {
        EnsureSafeContainedPath(plan.GameRoot, path);
        if (!File.Exists(path)) return;
        if (expectedHash is null)
            throw new InvalidDataException($"Rollback cannot prove that this file belongs to the transaction: {path}");
        var actualHash = await HashFileAsync(path, CancellationToken.None);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Rollback refused to remove a file that changed after the transaction: {path}");
        File.Delete(path);
    }

    private static async Task RestoreBytesAsync(
        DeploymentPlan plan,
        string path,
        byte[]? original,
        string? expectedCurrentHash)
    {
        EnsureSafeContainedPath(plan.GameRoot, path);
        if (File.Exists(path))
        {
            if (expectedCurrentHash is null)
                throw new InvalidDataException($"Rollback cannot prove that this file belongs to the transaction: {path}");
            var actualHash = await HashFileAsync(path, CancellationToken.None);
            if (!actualHash.Equals(expectedCurrentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Rollback refused to overwrite a file that changed after the transaction: {path}");
        }
        if (original is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        await AtomicWriteAsync(plan.GameRoot, path, original, CancellationToken.None,
            RollbackTemporaryPath(plan, path));
    }

    private static async Task RestoreMovedFileAsync(
        DeploymentPlan plan,
        string source,
        string target,
        string expectedSourceHash)
    {
        EnsureSafeContainedPath(plan.GameRoot, source);
        EnsureSafeContainedPath(plan.GameRoot, target);
        if (!File.Exists(source))
            throw new FileNotFoundException("Rollback source is missing.", source);
        var sourceHash = await HashFileAsync(source, CancellationToken.None);
        if (!sourceHash.Equals(expectedSourceHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Rollback backup changed after it was created: {source}");
        if (File.Exists(target))
            throw new InvalidDataException($"Rollback refused to overwrite an occupied restore target: {target}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
    }

    private static async Task UndoMoveAsync(DeploymentPlan plan, string target, string source, string expectedHash)
    {
        EnsureSafeContainedPath(plan.GameRoot, target);
        EnsureSafeContainedPath(plan.GameRoot, source);
        if (!File.Exists(target)) throw new FileNotFoundException("Rollback move target is missing.", target);
        var actualHash = await HashFileAsync(target, CancellationToken.None);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Rollback refused to move a changed file: {target}");
        if (File.Exists(source))
            throw new InvalidDataException($"Rollback refused to overwrite an occupied move source: {source}");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Move(target, source);
    }

    private static FileStream AcquireTransactionLock(DeploymentPlan plan)
    {
        var metadata = Path.Combine(plan.GameRoot, MetadataDirectoryName);
        EnsureSafeContainedPath(plan.GameRoot, metadata);
        Directory.CreateDirectory(metadata);
        var lockPath = Path.Combine(metadata, "transaction.lock");
        EnsureSafeContainedPath(plan.GameRoot, lockPath);
        return new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
    }

    private static async Task<TransactionJournal> CreateJournalAsync(DeploymentPlan plan, CancellationToken token)
    {
        return await CreateJournalAsync(plan, token, null);
    }

    private static async Task<TransactionJournal> CreateJournalAsync(
        DeploymentPlan plan,
        CancellationToken token,
        Action? snapshotCompleted)
    {
        var journalPath = JournalPath(plan);
        EnsureSafeContainedPath(plan.GameRoot, journalPath);
        if (File.Exists(journalPath))
            throw new InvalidOperationException($"Transaction ID {plan.Id} has already been used for this game.");
        if (await ManifestContainsTransactionAsync(plan.GameRoot, plan.Id, token))
            throw new InvalidOperationException($"Transaction ID {plan.Id} is already committed in the game manifest.");

        var now = DateTimeOffset.UtcNow;
        var journal = new TransactionJournal
        {
            Id = plan.Id,
            AppId = plan.AppId,
            Action = plan.Action,
            State = JournalState.InProgress,
            StartedUtc = now,
            UpdatedUtc = now,
            Operations = plan.Operations.Select((operation, index) => new TransactionJournalOperation
            {
                Index = index,
                Type = operation.Type.ToString(),
                Target = operation.Target,
                Source = operation.Source,
                Value = operation.Value,
                ExpectedSha256 = operation.ExpectedSha256,
                Component = operation.Component?.ToString(),
                BackupPath = operation.BackupPath,
                SourceUrl = operation.SourceUrl,
                SourceBlobSha256 = operation.SourceBlobSha256,
                SourceBundleSha256 = operation.SourceBundleSha256,
                BundleRelativePath = operation.BundleRelativePath,
                Description = Describe(operation),
                Status = JournalOperationStatus.Pending
            }).ToList()
        };

        var mutationPaths = EnumerateMutationPaths(plan)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var root = NormalizeDirectory(plan.GameRoot);
        try
        {
            for (var index = 0; index < mutationPaths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var path = mutationPaths[index];
                EnsureContained(root, path);
                EnsureSafeContainedPath(plan.GameRoot, path);
                var relativePath = Path.GetRelativePath(plan.GameRoot, path);
                if (!File.Exists(path))
                {
                    journal.OriginalFiles.Add(new TransactionFileSnapshot(relativePath, false, null, null));
                    continue;
                }

                var snapshotPath = Path.Combine(SnapshotDirectory(plan.GameRoot, plan.Id), $"{index:D4}.bin");
                var snapshotRelativePath = Path.GetRelativePath(plan.GameRoot, snapshotPath);
                EnsureSafeContainedPath(plan.GameRoot, snapshotPath);
                var hash = await CreateSnapshotAsync(plan.GameRoot, path, snapshotPath, token);
                journal.OriginalFiles.Add(new TransactionFileSnapshot(relativePath, true, hash, snapshotRelativePath));
                snapshotCompleted?.Invoke();
                token.ThrowIfCancellationRequested();
            }
            return journal;
        }
        catch
        {
            await TryDeleteSnapshotFilesAsync(plan.GameRoot, journal);
            throw;
        }
    }

    private static IEnumerable<string> EnumerateMutationPaths(DeploymentPlan plan)
    {
        yield return ManifestPath(plan);
        foreach (var operation in plan.Operations)
            foreach (var path in EnumerateOperationMutationPaths(plan, operation))
                yield return path;
    }

    private static IEnumerable<string> EnumerateOperationMutationPaths(
        DeploymentPlan plan,
        DeploymentOperation operation)
    {
        switch (operation.Type)
        {
            case DeploymentOperationType.Backup:
                yield return operation.Target;
                yield return operation.Source ?? BackupPath(plan, operation.Target);
                break;
            case DeploymentOperationType.Copy:
            case DeploymentOperationType.WriteIniValue:
            case DeploymentOperationType.DeleteManagedFile:
            case DeploymentOperationType.DeleteVerifiedFile:
            case DeploymentOperationType.WriteManifest:
                yield return operation.Target;
                if (operation.Type == DeploymentOperationType.WriteIniValue && operation.BackupPath is not null)
                    yield return operation.BackupPath;
                break;
            case DeploymentOperationType.Move:
            case DeploymentOperationType.RestoreBackup:
                yield return operation.Target;
                if (operation.Source is not null) yield return operation.Source;
                break;
        }
    }

    private static async Task<string> CreateSnapshotAsync(
        string gameRoot,
        string source,
        string destination,
        CancellationToken token)
    {
        EnsureSafeContainedPath(gameRoot, source);
        EnsureSafeContainedPath(gameRoot, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        EnsureSafeContainedPath(gameRoot, temporary);
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            var snapshotHash = await HashFileAsync(temporary, token);
            var sourceHash = await HashFileAsync(source, token);
            if (!snapshotHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"File changed while its transaction snapshot was created: {source}");
            File.Move(temporary, destination);
            return snapshotHash;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task VerifyOperationPreStateAsync(
        DeploymentPlan plan,
        TransactionJournal journal,
        int operationIndex,
        DeploymentOperation operation,
        CancellationToken token)
    {
        var originals = journal.OriginalFiles.ToDictionary(
            original => ResolveRelativePath(plan.GameRoot, original.RelativePath),
            StringComparer.Ordinal);
        foreach (var path in EnumerateOperationMutationPaths(plan, operation)
                     .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            EnsureSafeContainedPath(plan.GameRoot, path);
            if (!originals.TryGetValue(path, out var original))
                throw new InvalidDataException($"Transaction journal lacks the original state for {path}.");
            var relativePath = Path.GetRelativePath(plan.GameRoot, path);
            var priorExpected = journal.Operations.Take(operationIndex)
                .SelectMany(previous => previous.ExpectedFiles)
                .LastOrDefault(expected => expected.RelativePath.Equals(relativePath, StringComparison.Ordinal));
            var expectedExists = priorExpected?.Exists ?? original.Existed;
            var expectedHash = priorExpected?.Sha256 ?? original.Sha256;
            if (File.Exists(path) != expectedExists)
                throw new InvalidDataException($"File state changed after the transaction snapshot was created: {path}");
            if (!expectedExists) continue;
            var actualHash = await HashFileAsync(path, token);
            if (!IsSha256(expectedHash) || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"File changed after the transaction snapshot was created: {path}");
        }
    }

    private static async Task<List<TransactionExpectedFileState>> PredictOperationFileStatesAsync(
        DeploymentPlan plan,
        DeploymentOperation operation,
        CancellationToken token)
    {
        var states = new List<TransactionExpectedFileState>();
        async Task AddCurrentAsync(string path)
        {
            if (!File.Exists(path))
            {
                states.Add(CreateExpectedFileState(plan, path, false, null));
                return;
            }
            states.Add(CreateExpectedFileState(plan, path, true, await HashFileAsync(path, token)));
        }

        switch (operation.Type)
        {
            case DeploymentOperationType.Backup:
                {
                    var backup = operation.Source ?? BackupPath(plan, operation.Target);
                    if (File.Exists(operation.Target))
                    {
                        var hash = await HashFileAsync(operation.Target, token);
                        states.Add(CreateExpectedFileState(plan, operation.Target, false, null));
                        states.Add(CreateExpectedFileState(plan, backup, true, hash));
                    }
                    else
                    {
                        await AddCurrentAsync(operation.Target);
                        await AddCurrentAsync(backup);
                    }
                    break;
                }
            case DeploymentOperationType.Copy:
                {
                    var source = operation.Source ?? throw new InvalidOperationException("Copy operation has no source.");
                    states.Add(CreateExpectedFileState(plan, operation.Target, true, await HashFileAsync(source, token)));
                    break;
                }
            case DeploymentOperationType.Move:
                {
                    var source = operation.Source ?? throw new InvalidOperationException("Move operation has no source.");
                    var hash = await HashFileAsync(source, token);
                    states.Add(CreateExpectedFileState(plan, source, false, null));
                    states.Add(CreateExpectedFileState(plan, operation.Target, true, hash));
                    break;
                }
            case DeploymentOperationType.WriteIniValue:
                {
                    var original = File.Exists(operation.Target) ? await File.ReadAllBytesAsync(operation.Target, token) : null;
                    var (section, key, value) = ParseIniValue(operation.Value);
                    var document = original is null
                        ? new IniDocument()
                        : IniDocument.Parse(System.Text.Encoding.UTF8.GetString(original));
                    if (value == "<remove>") document.Remove(section, key);
                    else document.Set(section, key, value);
                    var result = System.Text.Encoding.UTF8.GetBytes(document.ToString());
                    states.Add(CreateExpectedFileState(plan, operation.Target, true, HashBytes(result)));
                    if (original is not null && operation.BackupPath is not null && !File.Exists(operation.BackupPath))
                        states.Add(CreateExpectedFileState(plan, operation.BackupPath, true, HashBytes(original)));
                    break;
                }
            case DeploymentOperationType.DeleteManagedFile:
            case DeploymentOperationType.DeleteVerifiedFile:
                states.Add(CreateExpectedFileState(plan, operation.Target, false, null));
                break;
            case DeploymentOperationType.RestoreBackup:
                {
                    var source = operation.Source ?? throw new InvalidOperationException("Restore operation has no backup source.");
                    var hash = await HashFileAsync(source, token);
                    states.Add(CreateExpectedFileState(plan, source, false, null));
                    states.Add(CreateExpectedFileState(plan, operation.Target, true, hash));
                    break;
                }
        }
        return states;
    }

    private static TransactionExpectedFileState CreateExpectedFileState(
        DeploymentPlan plan,
        string path,
        bool exists,
        string? sha256)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureSafeContainedPath(plan.GameRoot, fullPath);
        return new TransactionExpectedFileState(Path.GetRelativePath(plan.GameRoot, fullPath), exists, sha256);
    }

    private static string? ExpectedTransactionHash(
        DeploymentPlan plan,
        IReadOnlyList<TransactionExpectedFileState> states,
        string path)
    {
        var relativePath = Path.GetRelativePath(plan.GameRoot, Path.GetFullPath(path));
        return states.LastOrDefault(state => state.Exists &&
            state.RelativePath.Equals(relativePath, StringComparison.Ordinal))?.Sha256;
    }

    private static async Task VerifyExpectedFileStatesAsync(
        DeploymentPlan plan,
        IReadOnlyList<TransactionExpectedFileState> states,
        CancellationToken token)
    {
        foreach (var state in states)
        {
            var path = ResolveRelativePath(plan.GameRoot, state.RelativePath);
            if (File.Exists(path) != state.Exists)
                throw new InvalidDataException($"Transaction postcondition failed for {path}.");
            if (!state.Exists) continue;
            var actual = await HashFileAsync(path, token);
            if (state.Sha256 is null || !actual.Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Transaction postcondition hash mismatch for {path}.");
        }
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<string?> InspectInterruptedTransactionsAsync(
        DeploymentPlan plan,
        CancellationToken token)
    {
        var transactionDirectory = Path.Combine(plan.GameRoot, MetadataDirectoryName, "transactions");
        EnsureSafeContainedPath(plan.GameRoot, transactionDirectory);
        if (!Directory.Exists(transactionDirectory)) return null;
        foreach (var path in Directory.EnumerateFiles(transactionDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                EnsureSafeContainedPath(plan.GameRoot, path);
                await using var input = File.OpenRead(path);
                var journal = await JsonSerializer.DeserializeAsync<TransactionJournal>(input, JsonOptions, token)
                    ?? throw new InvalidDataException($"Transaction journal is empty: {path}");
                if (journal.State is JournalState.Completed or JournalState.RolledBack) continue;
                ValidateJournalIdentity(plan, path, journal);
                return $"Transaction {journal.Id} was interrupted. Dry-run did not change any file; " +
                       "run a confirmed modifying action to recover it, then rescan and rebuild the plan.";
            }
            catch (Exception exception) when (exception is JsonException or IOException or
                                               InvalidDataException or InvalidOperationException)
            {
                return $"An interrupted transaction journal cannot be validated safely. " +
                       $"Dry-run did not change any file. {exception.Message}";
            }
        }
        return null;
    }

    private static async Task<InterruptedRecoveryResult> RecoverInterruptedTransactionsAsync(
        DeploymentPlan plan,
        CancellationToken token)
    {
        var transactionDirectory = Path.Combine(plan.GameRoot, MetadataDirectoryName, "transactions");
        EnsureSafeContainedPath(plan.GameRoot, transactionDirectory);
        if (!Directory.Exists(transactionDirectory)) return InterruptedRecoveryResult.Success();

        var journals = new List<(string Path, TransactionJournal Journal)>();
        foreach (var path in Directory.EnumerateFiles(transactionDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            TransactionJournal journal;
            try
            {
                EnsureSafeContainedPath(plan.GameRoot, path);
                await using var input = File.OpenRead(path);
                journal = await JsonSerializer.DeserializeAsync<TransactionJournal>(input, JsonOptions, token)
                    ?? throw new InvalidDataException($"Interrupted transaction journal is empty: {path}");
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                return InterruptedRecoveryResult.Failure(
                    $"An interrupted transaction journal cannot be read safely. No game file was changed. {exception.Message}");
            }

            if (journal.State is JournalState.Completed or JournalState.RolledBack)
            {
                if (journal.SchemaVersion == TransactionJournal.CurrentSchemaVersion)
                    await TryDeleteSnapshotFilesAsync(plan.GameRoot, journal);
                continue;
            }
            try { ValidateJournalIdentity(plan, path, journal); }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                return InterruptedRecoveryResult.Failure(
                    $"An interrupted transaction journal failed safety validation. No game file was changed. {exception.Message}");
            }
            journals.Add((path, journal));
        }

        if (journals.Count == 0) return InterruptedRecoveryResult.Success();
        var messages = new List<string>();
        var recoveredAny = false;
        var filesChanged = false;
        foreach (var (_, journal) in journals.OrderByDescending(entry => entry.Journal.StartedUtc))
        {
            bool isCommitted;
            try { isCommitted = await ManifestContainsTransactionAsync(plan.GameRoot, journal.Id, token); }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                return InterruptedRecoveryResult.Failure(
                    $"The game manifest cannot be validated while recovering transaction {journal.Id}. " +
                    $"No game file was changed. {exception.Message}", messages);
            }
            if (isCommitted)
            {
                foreach (var operation in journal.Operations.Where(operation =>
                    operation.Status is not JournalOperationStatus.Skipped))
                {
                    operation.Status = JournalOperationStatus.Completed;
                    operation.CompletedUtc ??= DateTimeOffset.UtcNow;
                    operation.Error = null;
                }
                journal.State = JournalState.Completed;
                journal.Error = null;
                journal.RollbackErrors = [];
                journal.UpdatedUtc = DateTimeOffset.UtcNow;
                await SaveJournalAsync(plan.GameRoot, journal, CancellationToken.None);
                await TryDeleteSnapshotFilesAsync(plan.GameRoot, journal);
                messages.Add($"Finalized committed transaction {journal.Id} after an interrupted journal update.");
                recoveredAny = true;
                continue;
            }

            if (journal.SchemaVersion != TransactionJournal.CurrentSchemaVersion)
            {
                return InterruptedRecoveryResult.Failure(
                    $"Transaction {journal.Id} uses journal schema {journal.SchemaVersion}, which lacks durable pre-operation snapshots. " +
                    "Automatic recovery stopped without changing any game file.", messages);
            }

            var rollback = await RollBackInterruptedJournalAsync(plan.GameRoot, journal);
            messages.AddRange(rollback.Messages);
            if (!rollback.Succeeded)
                return InterruptedRecoveryResult.Failure(rollback.Error ?? "Interrupted transaction recovery failed.", messages);
            recoveredAny = true;
            filesChanged |= rollback.FilesChanged;
        }
        return InterruptedRecoveryResult.Success(messages, recoveredAny, filesChanged);
    }

    private static void ValidateJournalIdentity(DeploymentPlan plan, string path, TransactionJournal journal)
    {
        if (string.IsNullOrWhiteSpace(journal.Id)) throw new InvalidDataException("Journal transaction ID is missing.");
        if (journal.AppId != plan.AppId)
            throw new InvalidDataException($"Journal AppID {journal.AppId} does not match game AppID {plan.AppId}.");
        var expectedPath = Path.Combine(plan.GameRoot, MetadataDirectoryName, "transactions",
            JournalFileStem(journal.Id) + ".json");
        if (!Path.GetFullPath(path).Equals(Path.GetFullPath(expectedPath), StringComparison.Ordinal))
            throw new InvalidDataException("Journal filename does not match its transaction ID.");
        if (journal.State is not (JournalState.InProgress or JournalState.RollingBack or JournalState.RollbackFailed))
            throw new InvalidDataException($"Journal has an unknown nonterminal state: {journal.State}");
        if (journal.Operations.Select(operation => operation.Index).Where((index, position) => index != position).Any())
            throw new InvalidDataException("Journal operation indices are not contiguous.");
        foreach (var operation in journal.Operations)
        {
            if (!Enum.TryParse<DeploymentOperationType>(operation.Type, out _))
                throw new InvalidDataException($"Journal contains an unknown operation type: {operation.Type}");
            if (operation.Status is not (JournalOperationStatus.Pending or JournalOperationStatus.InProgress or
                JournalOperationStatus.Completed or JournalOperationStatus.Skipped or JournalOperationStatus.Failed or
                JournalOperationStatus.RolledBack or JournalOperationStatus.RollbackFailed))
                throw new InvalidDataException($"Journal contains an unknown operation status: {operation.Status}");
        }
    }

    private static async Task<InterruptedRecoveryResult> RollBackInterruptedJournalAsync(
        string gameRoot,
        TransactionJournal journal)
    {
        try
        {
            var changedFiles = false;
            await DeleteTransactionTemporaryFilesAsync(gameRoot, journal);
            var files = await BuildRecoveryFilesAsync(gameRoot, journal);
            foreach (var file in files)
                if (!await IsKnownRecoveryStateAsync(file))
                    throw new InvalidDataException(
                        $"Recovery refused to touch an unexpected file: {file.Path}");

            journal.State = JournalState.RollingBack;
            journal.Error = "Recovering an interrupted transaction before another deployment.";
            journal.RollbackErrors = [];
            journal.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveJournalAsync(gameRoot, journal, CancellationToken.None);

            var manifestPath = Path.Combine(gameRoot, MetadataDirectoryName, ManifestFileName);
            foreach (var file in files.OrderBy(file =>
                         file.Path.Equals(manifestPath, StringComparison.Ordinal) ? 1 : 0))
            {
                if (!await IsKnownRecoveryStateAsync(file))
                    throw new InvalidDataException(
                        $"Recovery stopped because a file changed after validation: {file.Path}");
                if (file.Original.Existed)
                {
                    if (File.Exists(file.Path) &&
                        (await HashFileAsync(file.Path, CancellationToken.None)).Equals(
                            file.Original.Sha256, StringComparison.OrdinalIgnoreCase))
                        continue;
                    await RestoreSnapshotFileAsync(file);
                    changedFiles = true;
                }
                else if (File.Exists(file.Path))
                {
                    if (!await IsKnownRecoveryStateAsync(file))
                        throw new InvalidDataException(
                            $"Recovery refused to remove an unexpected file: {file.Path}");
                    File.Delete(file.Path);
                    changedFiles = true;
                }
            }

            foreach (var file in files)
            {
                if (File.Exists(file.Path) != file.Original.Existed)
                    throw new InvalidDataException($"Recovery did not restore the original state of {file.Path}.");
                if (file.Original.Existed &&
                    !(await HashFileAsync(file.Path, CancellationToken.None)).Equals(
                        file.Original.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Recovery restored an unexpected hash for {file.Path}.");
            }

            foreach (var operation in journal.Operations.Where(operation =>
                         operation.Status is not (JournalOperationStatus.Pending or JournalOperationStatus.Skipped)))
            {
                operation.Status = JournalOperationStatus.RolledBack;
                operation.CompletedUtc ??= DateTimeOffset.UtcNow;
                operation.Error = null;
            }
            journal.State = JournalState.RolledBack;
            journal.Error = null;
            journal.RollbackErrors = [];
            journal.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveJournalAsync(gameRoot, journal, CancellationToken.None);
            await TryDeleteSnapshotFilesAsync(gameRoot, journal);
            return InterruptedRecoveryResult.Success(
                [$"Recovered interrupted transaction {journal.Id}; its recorded pre-operation file state was restored."],
                true,
                changedFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
        {
            journal.State = JournalState.RollbackFailed;
            journal.Error = exception.Message;
            journal.RollbackErrors = [exception.Message];
            journal.UpdatedUtc = DateTimeOffset.UtcNow;
            _ = await TrySaveJournalAsync(gameRoot, journal);
            return InterruptedRecoveryResult.Failure(
                $"Interrupted transaction {journal.Id} could not be recovered automatically. " +
                $"No unexpected file was changed. {exception.Message}");
        }
    }

    private static async Task<List<TransactionRecoveryFile>> BuildRecoveryFilesAsync(
        string gameRoot,
        TransactionJournal journal)
    {
        var originals = new Dictionary<string, TransactionFileSnapshot>(StringComparer.Ordinal);
        var snapshotDirectoryPath = SnapshotDirectory(gameRoot, journal.Id);
        EnsureSafeContainedPath(gameRoot, snapshotDirectoryPath);
        var snapshotDirectory = NormalizeDirectory(ResolveLinks(snapshotDirectoryPath));
        foreach (var original in journal.OriginalFiles)
        {
            var path = ResolveRelativePath(gameRoot, original.RelativePath);
            if (ResolveLinks(path).StartsWith(snapshotDirectory, StringComparison.Ordinal))
                throw new InvalidDataException("A journal cannot restore a transaction-private snapshot as a game file.");
            if (!originals.TryAdd(path, original))
                throw new InvalidDataException($"Journal contains duplicate original-file state for {path}.");
            if (!original.Existed)
            {
                if (original.Sha256 is not null || original.SnapshotRelativePath is not null)
                    throw new InvalidDataException($"Absent original file has unexpected snapshot metadata: {path}");
                continue;
            }
            if (!IsSha256(original.Sha256) || string.IsNullOrWhiteSpace(original.SnapshotRelativePath))
                throw new InvalidDataException($"Original file snapshot metadata is incomplete: {path}");
            var snapshotPath = ResolveRelativePath(gameRoot, original.SnapshotRelativePath);
            if (!ResolveLinks(snapshotPath).StartsWith(snapshotDirectory, StringComparison.Ordinal))
                throw new InvalidDataException($"Snapshot path escapes its transaction-private directory: {snapshotPath}");
            if (!File.Exists(snapshotPath))
                throw new FileNotFoundException("Transaction recovery snapshot is missing.", snapshotPath);
            if (!(await HashFileAsync(snapshotPath, CancellationToken.None)).Equals(
                    original.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Transaction recovery snapshot changed: {snapshotPath}");
        }

        var requiredPaths = EnumerateJournalMutationPaths(gameRoot, journal).ToHashSet(StringComparer.Ordinal);
        foreach (var path in requiredPaths)
            if (!originals.ContainsKey(path))
                throw new InvalidDataException($"Journal lacks the original state for a modified path: {path}");
        foreach (var path in originals.Keys)
            if (!requiredPaths.Contains(path))
                throw new InvalidDataException($"Journal contains an unrelated original-file snapshot: {path}");

        var files = originals.Select(pair => new TransactionRecoveryFile(
            gameRoot,
            journal.Id,
            pair.Key,
            pair.Value,
            pair.Value.Existed ? new HashSet<string>([pair.Value.Sha256!], StringComparer.OrdinalIgnoreCase) :
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            !pair.Value.Existed,
            pair.Value.SnapshotRelativePath is null ? null : ResolveRelativePath(gameRoot, pair.Value.SnapshotRelativePath)))
            .ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var operation in journal.Operations.Where(operation =>
                     operation.Status is not (JournalOperationStatus.Pending or JournalOperationStatus.Skipped)))
        {
            var operationPaths = EnumerateJournalOperationMutationPaths(gameRoot, journal, operation)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var expected in operation.ExpectedFiles)
            {
                var path = ResolveRelativePath(gameRoot, expected.RelativePath);
                if (!operationPaths.Contains(path))
                    throw new InvalidDataException($"Journal operation contains an unrelated postcondition path: {path}");
                if (!files.TryGetValue(path, out var file))
                    throw new InvalidDataException($"Journal postcondition has no original-file snapshot: {path}");
                if (expected.Exists)
                {
                    if (!IsSha256(expected.Sha256))
                        throw new InvalidDataException($"Journal postcondition hash is invalid for {path}.");
                    file.AllowedHashes.Add(expected.Sha256!);
                }
                else file.AllowsAbsent = true;
            }
        }
        return files.Values.ToList();
    }

    private static IEnumerable<string> EnumerateJournalMutationPaths(string gameRoot, TransactionJournal journal)
    {
        yield return ValidateJournalPath(gameRoot,
            Path.Combine(gameRoot, MetadataDirectoryName, ManifestFileName));
        foreach (var operation in journal.Operations)
            foreach (var path in EnumerateJournalOperationMutationPaths(gameRoot, journal, operation))
                yield return path;
    }

    private static IEnumerable<string> EnumerateJournalOperationMutationPaths(
        string gameRoot,
        TransactionJournal journal,
        TransactionJournalOperation operation)
    {
        var type = Enum.Parse<DeploymentOperationType>(operation.Type);
        switch (type)
        {
            case DeploymentOperationType.Backup:
                yield return ValidateJournalPath(gameRoot, operation.Target);
                yield return ValidateJournalPath(gameRoot,
                    operation.Source ?? BackupPath(gameRoot, journal.Id, operation.Target));
                break;
            case DeploymentOperationType.Copy:
            case DeploymentOperationType.DeleteManagedFile:
            case DeploymentOperationType.DeleteVerifiedFile:
            case DeploymentOperationType.WriteManifest:
                yield return ValidateJournalPath(gameRoot, operation.Target);
                break;
            case DeploymentOperationType.WriteIniValue:
                yield return ValidateJournalPath(gameRoot, operation.Target);
                if (operation.BackupPath is not null)
                    yield return ValidateJournalPath(gameRoot, operation.BackupPath);
                break;
            case DeploymentOperationType.Move:
            case DeploymentOperationType.RestoreBackup:
                yield return ValidateJournalPath(gameRoot, operation.Target);
                yield return ValidateJournalPath(gameRoot, operation.Source ??
                    throw new InvalidDataException($"Journal {type} operation has no source."));
                break;
        }
    }

    private static string ValidateJournalPath(string gameRoot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureSafeContainedPath(gameRoot, fullPath);
        return fullPath;
    }

    private static async Task<bool> IsKnownRecoveryStateAsync(TransactionRecoveryFile file)
    {
        EnsureSafeContainedPath(file.GameRoot, file.Path);
        if (!File.Exists(file.Path)) return file.AllowsAbsent;
        var hash = await HashFileAsync(file.Path, CancellationToken.None);
        return file.AllowedHashes.Contains(hash);
    }

    private static async Task RestoreSnapshotFileAsync(TransactionRecoveryFile file)
    {
        EnsureSafeContainedPath(file.GameRoot, file.Path);
        var snapshotPath = file.SnapshotPath ??
            throw new InvalidDataException($"Recovery snapshot path is missing for {file.Path}.");
        EnsureSafeContainedPath(file.GameRoot, snapshotPath);
        var expectedHash = file.Original.Sha256 ??
            throw new InvalidDataException($"Recovery snapshot hash is missing for {file.Path}.");
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        var temporary = RecoveryTemporaryPath(file.GameRoot, file.TransactionId, file.Path);
        EnsureSafeContainedPath(file.GameRoot, temporary);
        try
        {
            await using (var input = File.OpenRead(snapshotPath))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
                output.Flush(true);
            }
            if (!(await HashFileAsync(temporary, CancellationToken.None)).Equals(
                    expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Recovery snapshot copy hash mismatch for {file.Path}.");
            if (!await IsKnownRecoveryStateAsync(file))
                throw new InvalidDataException($"Recovery refused to overwrite a file that changed: {file.Path}");
            File.Move(temporary, file.Path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> ManifestContainsTransactionAsync(
        string gameRoot,
        string transactionId,
        CancellationToken token)
    {
        var path = Path.Combine(gameRoot, MetadataDirectoryName, ManifestFileName);
        EnsureSafeContainedPath(gameRoot, path);
        if (!File.Exists(path)) return false;
        await using var input = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<GameManifest>(input, JsonOptions, token)
            ?? throw new InvalidDataException("Game manifest is empty.");
        return manifest.TransactionIds.Contains(transactionId, StringComparer.Ordinal);
    }

    private static async Task TryDeleteSnapshotFilesAsync(string gameRoot, TransactionJournal journal)
    {
        try
        {
            await DeleteTransactionTemporaryFilesAsync(gameRoot, journal);
            var snapshotDirectory = SnapshotDirectory(gameRoot, journal.Id);
            EnsureSafeContainedPath(gameRoot, snapshotDirectory);
            var resolvedSnapshotDirectory = NormalizeDirectory(ResolveLinks(snapshotDirectory));
            foreach (var original in journal.OriginalFiles.Where(original =>
                         original.Existed && original.SnapshotRelativePath is not null && IsSha256(original.Sha256)))
            {
                var snapshotPath = ResolveRelativePath(gameRoot, original.SnapshotRelativePath!);
                if (!ResolveLinks(snapshotPath).StartsWith(resolvedSnapshotDirectory, StringComparison.Ordinal) ||
                    !File.Exists(snapshotPath))
                    continue;
                if ((await HashFileAsync(snapshotPath, CancellationToken.None)).Equals(
                        original.Sha256, StringComparison.OrdinalIgnoreCase))
                    File.Delete(snapshotPath);
            }
            if (Directory.Exists(snapshotDirectory) && !Directory.EnumerateFileSystemEntries(snapshotDirectory).Any())
                Directory.Delete(snapshotDirectory);
            var workDirectory = TransactionWorkDirectory(gameRoot, journal.Id);
            if (Directory.Exists(workDirectory) && !Directory.EnumerateFileSystemEntries(workDirectory).Any())
                Directory.Delete(workDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
        {
        }
    }

    private static async Task DeleteTransactionTemporaryFilesAsync(
        string gameRoot,
        TransactionJournal journal)
    {
        foreach (var temporary in EnumerateTransactionTemporaryPaths(gameRoot, journal)
                     .Distinct(StringComparer.Ordinal))
        {
            EnsureSafeContainedPath(gameRoot, temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        await Task.CompletedTask;
    }

    private static IEnumerable<string> EnumerateTransactionTemporaryPaths(
        string gameRoot,
        TransactionJournal journal)
    {
        yield return TransactionTemporaryPath(gameRoot, journal.Id, "journal");
        foreach (var operation in journal.Operations)
        {
            var type = Enum.Parse<DeploymentOperationType>(operation.Type);
            if (type == DeploymentOperationType.Copy)
                yield return OperationTemporaryPath(gameRoot, journal.Id, operation.Index, "copy");
            if (type == DeploymentOperationType.WriteIniValue)
            {
                yield return OperationTemporaryPath(gameRoot, journal.Id, operation.Index, "ini");
                yield return OperationTemporaryPath(gameRoot, journal.Id, operation.Index, "ini-backup");
            }
            if (type == DeploymentOperationType.WriteManifest)
                yield return OperationTemporaryPath(gameRoot, journal.Id, operation.Index, "manifest");
        }
        foreach (var original in journal.OriginalFiles.Where(original => original.SnapshotRelativePath is not null))
            yield return ResolveRelativePath(gameRoot, original.SnapshotRelativePath!) + ".tmp";
        foreach (var path in EnumerateJournalMutationPaths(gameRoot, journal).Distinct(StringComparer.Ordinal))
        {
            yield return RollbackTemporaryPath(gameRoot, journal.Id, path);
            yield return RecoveryTemporaryPath(gameRoot, journal.Id, path);
        }
    }

    private static string ResolveRelativePath(string gameRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Journal path must be a non-empty relative path.");
        var path = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
        EnsureSafeContainedPath(gameRoot, path);
        return path;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void StartJournalOperation(TransactionJournal journal, int index)
    {
        var operation = journal.Operations[index];
        operation.Status = JournalOperationStatus.InProgress;
        operation.StartedUtc ??= DateTimeOffset.UtcNow;
        operation.Error = null;
        journal.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    private static void CompleteJournalOperation(TransactionJournal journal, int index)
    {
        var operation = journal.Operations[index];
        operation.Status = JournalOperationStatus.Completed;
        operation.CompletedUtc = DateTimeOffset.UtcNow;
        journal.UpdatedUtc = operation.CompletedUtc.Value;
    }

    private static void SkipJournalOperation(TransactionJournal journal, int index)
    {
        var operation = journal.Operations[index];
        operation.Status = JournalOperationStatus.Skipped;
        operation.CompletedUtc = DateTimeOffset.UtcNow;
        journal.UpdatedUtc = operation.CompletedUtc.Value;
    }

    private static async Task SaveJournalAsync(
        DeploymentPlan plan,
        TransactionJournal journal,
        CancellationToken token)
    {
        await SaveJournalAsync(plan.GameRoot, journal, token);
    }

    private static async Task SaveJournalAsync(
        string gameRoot,
        TransactionJournal journal,
        CancellationToken token)
    {
        var path = JournalPath(gameRoot, journal.Id);
        EnsureSafeContainedPath(gameRoot, path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        await AtomicWriteAsync(gameRoot, path, bytes, token,
            TransactionTemporaryPath(gameRoot, journal.Id, "journal"));
    }

    private static async Task<bool> TrySaveJournalAsync(DeploymentPlan plan, TransactionJournal journal)
    {
        try
        {
            await SaveJournalAsync(plan, journal, CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> TrySaveJournalAsync(string gameRoot, TransactionJournal journal)
    {
        try
        {
            await SaveJournalAsync(gameRoot, journal, CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static (string Section, string Key, string Value) ParseIniValue(string? expression)
    {
        var equals = expression?.IndexOf('=') ?? -1;
        var colon = expression?.IndexOf(':') ?? -1;
        if (equals <= 0 || colon < 0 || colon > equals) throw new InvalidOperationException("INI value must use section:key=value syntax.");
        return (expression![..colon], expression[(colon + 1)..equals], expression[(equals + 1)..]);
    }

    private static string BackupPath(DeploymentPlan plan, string target) => Path.Combine(
        plan.GameRoot, MetadataDirectoryName, "backups", plan.Id, Path.GetRelativePath(plan.GameRoot, target));
    private static string BackupPath(string gameRoot, string transactionId, string target) => Path.Combine(
        gameRoot, MetadataDirectoryName, "backups", transactionId, Path.GetRelativePath(gameRoot, target));
    private static string ManifestPath(DeploymentPlan plan) => Path.Combine(plan.GameRoot, MetadataDirectoryName, ManifestFileName);
    private static string JournalPath(DeploymentPlan plan) => JournalPath(plan.GameRoot, plan.Id);
    private static string JournalPath(string gameRoot, string transactionId) => Path.Combine(
        gameRoot, MetadataDirectoryName, "transactions", JournalFileStem(transactionId) + ".json");
    private static string TransactionWorkDirectory(string gameRoot, string transactionId) => Path.Combine(
        gameRoot, MetadataDirectoryName, "transactions", JournalFileStem(transactionId) + ".work");
    private static string SnapshotDirectory(string gameRoot, string transactionId) => Path.Combine(
        gameRoot, MetadataDirectoryName, "transactions", JournalFileStem(transactionId) + ".snapshots");
    private static string TransactionTemporaryPath(string gameRoot, string transactionId, string purpose) => Path.Combine(
        TransactionWorkDirectory(gameRoot, transactionId), purpose + ".tmp");
    private static string OperationTemporaryPath(DeploymentPlan plan, int operationIndex, string purpose) =>
        OperationTemporaryPath(plan.GameRoot, plan.Id, operationIndex, purpose);
    private static string OperationTemporaryPath(
        string gameRoot,
        string transactionId,
        int operationIndex,
        string purpose) =>
        TransactionTemporaryPath(gameRoot, transactionId, $"{operationIndex:D4}-{purpose}");
    private static string RollbackTemporaryPath(DeploymentPlan plan, string path) =>
        RollbackTemporaryPath(plan.GameRoot, plan.Id, path);
    private static string RollbackTemporaryPath(string gameRoot, string transactionId, string path) =>
        TransactionTemporaryPath(gameRoot, transactionId, "rollback-" + PathIdentity(path));
    private static string RecoveryTemporaryPath(string gameRoot, string transactionId, string path) =>
        TransactionTemporaryPath(gameRoot, transactionId, "recovery-" + PathIdentity(path));
    private static string PathIdentity(string path) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))))
            .ToLowerInvariant();
    private static string JournalFileStem(DeploymentPlan plan) => JournalFileStem(plan.Id);
    private static string JournalFileStem(string transactionId) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(transactionId))).ToLowerInvariant();
    private static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    private static void EnsureSafeContainedPath(string gameRoot, string path)
    {
        var lexicalRoot = NormalizeDirectory(gameRoot);
        var fullPath = Path.GetFullPath(path);
        EnsureContained(lexicalRoot, fullPath);

        var resolvedRoot = NormalizeDirectory(ResolveLinks(Path.GetFullPath(gameRoot)));
        var resolvedPath = ResolveLinks(fullPath);
        EnsureContained(resolvedRoot, resolvedPath);
    }

    private static string ResolveLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException($"Path has no filesystem root: {path}");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            var info = new FileInfo(candidate);
            if (info.LinkTarget is null)
            {
                current = candidate;
                continue;
            }
            var resolved = info.ResolveLinkTarget(true) ??
                throw new InvalidOperationException($"Path contains an unresolved symbolic link: {candidate}");
            current = Path.GetFullPath(resolved.FullName);
        }
        return Path.GetFullPath(current);
    }

    private static void EnsureContained(string normalizedRoot, string path)
    {
        if (!path.StartsWith(normalizedRoot, StringComparison.Ordinal) &&
            !Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(normalizedRoot), StringComparison.Ordinal))
            throw new InvalidOperationException($"Deployment path escapes the game root: {path}");
    }

    private static class JournalState
    {
        public const string InProgress = "inProgress";
        public const string RollingBack = "rollingBack";
        public const string Completed = "completed";
        public const string RolledBack = "rolledBack";
        public const string RollbackFailed = "rollbackFailed";
    }

    private static class JournalOperationStatus
    {
        public const string Pending = "pending";
        public const string InProgress = "inProgress";
        public const string Completed = "completed";
        public const string Skipped = "skipped";
        public const string Failed = "failed";
        public const string RolledBack = "rolledBack";
        public const string RollbackFailed = "rollbackFailed";
    }

    private sealed class TransactionJournal
    {
        public const int CurrentSchemaVersion = 2;
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public required string Id { get; set; }
        public uint AppId { get; set; }
        public required string Action { get; set; }
        public required string State { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public string? Error { get; set; }
        public IReadOnlyList<string> RollbackErrors { get; set; } = [];
        public List<TransactionFileSnapshot> OriginalFiles { get; set; } = [];
        public List<TransactionJournalOperation> Operations { get; set; } = [];
    }

    private sealed class TransactionJournalOperation
    {
        public int Index { get; set; }
        public required string Type { get; set; }
        public required string Target { get; set; }
        public string? Source { get; set; }
        public string? Value { get; set; }
        public string? ExpectedSha256 { get; set; }
        public string? Component { get; set; }
        public string? BackupPath { get; set; }
        public string? SourceUrl { get; set; }
        public string? SourceBlobSha256 { get; set; }
        public string? SourceBundleSha256 { get; set; }
        public string? BundleRelativePath { get; set; }
        public required string Description { get; set; }
        public required string Status { get; set; }
        public DateTimeOffset? StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string? Error { get; set; }
        public List<TransactionExpectedFileState> ExpectedFiles { get; set; } = [];

        public static TransactionJournalOperation InternalManifest(int index, string target) => new()
        {
            Index = index,
            Type = DeploymentOperationType.WriteManifest.ToString(),
            Target = target,
            Description = "Atomically update the game manifest",
            Status = JournalOperationStatus.Pending
        };
    }

    private sealed record TransactionFileSnapshot(
        string RelativePath,
        bool Existed,
        string? Sha256,
        string? SnapshotRelativePath);

    private sealed record TransactionExpectedFileState(
        string RelativePath,
        bool Exists,
        string? Sha256);

    private sealed class TransactionRecoveryFile(
        string gameRoot,
        string transactionId,
        string path,
        TransactionFileSnapshot original,
        HashSet<string> allowedHashes,
        bool allowsAbsent,
        string? snapshotPath)
    {
        public string GameRoot { get; } = gameRoot;
        public string TransactionId { get; } = transactionId;
        public string Path { get; } = path;
        public TransactionFileSnapshot Original { get; } = original;
        public HashSet<string> AllowedHashes { get; } = allowedHashes;
        public bool AllowsAbsent { get; set; } = allowsAbsent;
        public string? SnapshotPath { get; } = snapshotPath;
    }

    private sealed record InterruptedRecoveryResult(
        bool Succeeded,
        bool Recovered,
        bool FilesChanged,
        IReadOnlyList<string> Messages,
        string? Error)
    {
        public static InterruptedRecoveryResult Success(
            IReadOnlyList<string>? messages = null,
            bool recovered = false,
            bool filesChanged = false) => new(true, recovered, filesChanged, messages ?? [], null);

        public static InterruptedRecoveryResult Failure(
            string error,
            IReadOnlyList<string>? messages = null) => new(false, false, false, messages ?? [], error);
    }
}
