using System.Security.Cryptography;
using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public static class EnhancementStateStore
{
    public const string MetadataDirectoryName = ".rhi-linux";
    public const string StateFileName = "enhancements.json";
    public const string JournalFileName = "enhancements.journal.json";
    public const string ShaderMarkerFileName = ".rhi-linux-shaders.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string StatePath(string gameRoot) => Path.Combine(gameRoot, MetadataDirectoryName, StateFileName);

    public static string JournalPath(string gameRoot) => Path.Combine(gameRoot, MetadataDirectoryName, JournalFileName);

    public static async Task<EnhancementState> LoadAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        var path = StatePath(gameRoot);
        if (!File.Exists(path)) return new EnhancementState();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        var state = await JsonSerializer.DeserializeAsync<EnhancementState>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The enhancement state file is empty.");
        if (state.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported enhancement state schema {state.SchemaVersion}.");
        state.Files ??= [];
        state.IniPatches ??= [];
        return state;
    }

    public static byte[] Serialize(EnhancementState state) => JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);

    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
                await output.WriteAsync(bytes, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed class EnhancementExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EnhancementExecutionResult> ExecuteAsync(
        EnhancementPlan plan,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        if (plan.Blockers.Count > 0)
            return new(false, dryRun, log, plan.Blockers[0]);
        if (dryRun)
        {
            log.AddRange(plan.Steps.Select(step => $"Would {LowerFirst(step.Description)}"));
            return new(true, true, log, null);
        }
        if (plan.Steps.Count == 0) return new(true, false, ["Nothing to change."], null);

        var gameRoot = Path.GetFullPath(plan.Game.GameRoot);
        var metadata = Path.Combine(gameRoot, EnhancementStateStore.MetadataDirectoryName);
        Directory.CreateDirectory(metadata);
        await using var transactionLock = OpenLock(metadata);
        if (File.Exists(EnhancementStateStore.JournalPath(gameRoot)))
            await RecoverLockedAsync(gameRoot, cancellationToken);

        var journal = new EnhancementJournal(plan.Id, []);
        var trash = Path.Combine(metadata, "enhancement-trash", plan.Id);
        var staging = Path.Combine(metadata, "staging", plan.Id);
        var backedUpFiles = new HashSet<string>(StringComparer.Ordinal);
        var counter = 0;
        try
        {
            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyAsync(step);
                log.Add(step.Description);
            }
            var statePath = EnhancementStateStore.StatePath(gameRoot);
            await BackupFileAsync(statePath);
            plan.ResultingState.InstallId ??= plan.Game.InstallId.Value;
            await EnhancementStateStore.WriteAtomicAsync(statePath, EnhancementStateStore.Serialize(plan.ResultingState), cancellationToken);
            File.Delete(EnhancementStateStore.JournalPath(gameRoot));
            TryDeleteDirectory(trash);
            TryDeleteDirectory(staging);
            if (plan.StagingDirectory is not null) TryDeleteDirectory(plan.StagingDirectory);
            return new(true, false, log, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          InvalidOperationException or OperationCanceledException)
        {
            var rolledBack = await RollbackAsync(gameRoot, journal, CancellationToken.None);
            TryDeleteDirectory(staging);
            log.Add(rolledBack ? "All changes were rolled back." : "Rollback could not be completed; recovery will retry on the next run.");
            return new(false, false, log, exception.Message);
        }

        async Task ApplyAsync(EnhancementStep step)
        {
            var target = Contained(gameRoot, step.Target);
            switch (step.Type)
            {
                case EnhancementStepType.PlaceFile:
                    {
                        var source = step.Source ?? throw new InvalidOperationException("The step has no source file.");
                        if (step.ExpectedSha256 is not null &&
                            !(await Sha256Async(source, cancellationToken)).Equals(step.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"The staged file for {Path.GetFileName(target)} changed after the plan was created.");
                        if (File.Exists(target)) await MoveToTrashAsync(target);
                        var parent = Path.GetDirectoryName(target)!;
                        if (!Directory.Exists(parent))
                        {
                            await RecordAsync(new("DeleteEmptyDirectory", parent, null));
                            Directory.CreateDirectory(parent);
                        }
                        await RecordAsync(new("Delete", target, null));
                        var partial = target + ".rhi-partial";
                        File.Copy(source, partial, true);
                        File.Move(partial, target, false);
                        break;
                    }
                case EnhancementStepType.RemoveFile:
                    if (File.Exists(target)) await MoveToTrashAsync(target);
                    break;
                case EnhancementStepType.PreserveOriginalShaders:
                case EnhancementStepType.RestoreOriginalShaders:
                    {
                        var source = Contained(gameRoot, step.Source ?? throw new InvalidOperationException("The step has no source."));
                        if (!Directory.Exists(source)) throw new IOException($"{Path.GetFileName(source)} no longer exists.");
                        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"{Path.GetFileName(target)} already exists.");
                        await RecordAsync(new("MoveBack", target, source));
                        Directory.Move(source, target);
                        break;
                    }
                case EnhancementStepType.InstallShaderTree:
                    {
                        var source = step.Source ?? throw new InvalidOperationException("The step has no source tree.");
                        var stagedTree = Path.Combine(staging, "reshade-shaders");
                        await RecordAsync(new("DeleteTree", staging, null));
                        await CopyTreeAsync(source, stagedTree, cancellationToken);
                        if (Directory.Exists(target)) await MoveToTrashAsync(target);
                        await RecordAsync(new("DeleteTree", target, null));
                        Directory.Move(stagedTree, target);
                        break;
                    }
                case EnhancementStepType.RemoveShaderTree:
                    if (Directory.Exists(target)) await MoveToTrashAsync(target);
                    break;
                case EnhancementStepType.SetIniValue:
                case EnhancementStepType.RestoreIniValue:
                    await PatchIniAsync(target, step);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported enhancement step {step.Type}.");
            }
        }

        async Task PatchIniAsync(string target, EnhancementStep step)
        {
            await BackupFileAsync(target);
            var document = File.Exists(target)
                ? IniDocument.Parse(await File.ReadAllBytesAsync(target, cancellationToken))
                : IniDocument.Parse(string.Empty);
            if (document.HasFatalIssues) throw new InvalidDataException($"{Path.GetFileName(target)} is malformed and was not changed.");
            var section = step.Section ?? string.Empty;
            var key = step.Key ?? throw new InvalidOperationException("The INI step has no key.");
            if (step.Value is null) document.Remove(section, key);
            else document.Set(section, key, step.Value);
            var bytes = document.ToUtf8Bytes();
            if (step.Type == EnhancementStepType.RestoreIniValue && step.Source == "delete-if-empty" &&
                System.Text.Encoding.UTF8.GetString(bytes).Split('\n')
                    .All(line => line.Trim().Length == 0 || line.Trim().StartsWith('[')))
            {
                File.Delete(target);
                return;
            }
            await EnhancementStateStore.WriteAtomicAsync(target, bytes, cancellationToken);
        }

        async Task BackupFileAsync(string target)
        {
            if (!backedUpFiles.Add(target)) return;
            if (!File.Exists(target))
            {
                await RecordAsync(new("Delete", target, null));
                return;
            }
            var backup = Path.Combine(trash, $"{counter++:D4}-{Path.GetFileName(target)}");
            Directory.CreateDirectory(trash);
            File.Copy(target, backup, true);
            await RecordAsync(new("RestoreFile", target, backup));
        }

        async Task MoveToTrashAsync(string target)
        {
            Directory.CreateDirectory(trash);
            var destination = Path.Combine(trash, $"{counter++:D4}-{Path.GetFileName(target)}");
            await RecordAsync(new("MoveBack", destination, target));
            if (Directory.Exists(target)) Directory.Move(target, destination);
            else File.Move(target, destination);
        }

        async Task RecordAsync(EnhancementUndo undo)
        {
            journal.Undo.Add(undo);
            await EnhancementStateStore.WriteAtomicAsync(
                EnhancementStateStore.JournalPath(gameRoot),
                JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions),
                cancellationToken);
        }
    }

    public static bool HasPendingRecovery(string gameRoot) =>
        File.Exists(EnhancementStateStore.JournalPath(gameRoot));

    public async Task<bool> RecoverAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        gameRoot = Path.GetFullPath(gameRoot);
        if (!HasPendingRecovery(gameRoot)) return true;
        var metadata = Path.Combine(gameRoot, EnhancementStateStore.MetadataDirectoryName);
        await using var transactionLock = OpenLock(metadata);
        return await RecoverLockedAsync(gameRoot, cancellationToken);
    }

    private static async Task<bool> RecoverLockedAsync(string gameRoot, CancellationToken cancellationToken)
    {
        var path = EnhancementStateStore.JournalPath(gameRoot);
        EnhancementJournal? journal;
        await using (var stream = File.OpenRead(path))
            journal = await JsonSerializer.DeserializeAsync<EnhancementJournal>(stream, JsonOptions, cancellationToken);
        if (journal is null)
        {
            File.Delete(path);
            return true;
        }
        var succeeded = await RollbackAsync(gameRoot, journal, cancellationToken);
        if (succeeded)
            TryDeleteDirectory(Path.Combine(gameRoot, EnhancementStateStore.MetadataDirectoryName, "staging", journal.PlanId));
        return succeeded;
    }

    private static Task<bool> RollbackAsync(string gameRoot, EnhancementJournal journal, CancellationToken cancellationToken)
    {
        var succeeded = true;
        for (var index = journal.Undo.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var undo = journal.Undo[index];
            try
            {
                var path = Contained(gameRoot, undo.Path);
                switch (undo.Operation)
                {
                    case "Delete":
                        if (File.Exists(path)) File.Delete(path);
                        if (File.Exists(path + ".rhi-partial")) File.Delete(path + ".rhi-partial");
                        break;
                    case "DeleteTree":
                        if (Directory.Exists(path)) Directory.Delete(path, true);
                        break;
                    case "DeleteEmptyDirectory":
                        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                        break;
                    case "MoveBack":
                        {
                            var destination = Contained(gameRoot, undo.Other!);
                            if (Directory.Exists(path) && !Directory.Exists(destination) && !File.Exists(destination))
                                Directory.Move(path, destination);
                            else if (File.Exists(path) && !File.Exists(destination) && !Directory.Exists(destination))
                                File.Move(path, destination);
                            break;
                        }
                    case "RestoreFile":
                        {
                            var backup = Contained(gameRoot, undo.Other!);
                            if (File.Exists(backup)) File.Copy(backup, path, true);
                            break;
                        }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                succeeded = false;
            }
        }
        if (succeeded)
        {
            var journalPath = EnhancementStateStore.JournalPath(gameRoot);
            if (File.Exists(journalPath)) File.Delete(journalPath);
            TryDeleteDirectory(Path.Combine(gameRoot, EnhancementStateStore.MetadataDirectoryName, "enhancement-trash", journal.PlanId));
        }
        return Task.FromResult(succeeded);
    }

    private static FileStream OpenLock(string metadata)
    {
        Directory.CreateDirectory(metadata);
        try
        {
            return new FileStream(Path.Combine(metadata, "transaction.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another operation is already changing this game. Try again when it finishes.", exception);
        }
    }

    internal static string Contained(string gameRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing to touch a path outside the game directory: {path}");
        return full;
    }

    private static async Task CopyTreeAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(source);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(sourceRoot, directory)));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The staged shader tree contains a symbolic link.");
            var target = Path.Combine(destination, Path.GetRelativePath(sourceRoot, file));
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    internal static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string LowerFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private sealed record EnhancementJournal(string PlanId, List<EnhancementUndo> Undo);

    private sealed record EnhancementUndo(string Operation, string Path, string? Other);
}
