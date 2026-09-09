using System.Text.Json;

namespace RhiLinux.Core;

public enum ManagedBackupStatus
{
    Restorable,
    InUse,
    Incomplete,
    Unavailable
}

public sealed record ManagedBackupEntry(
    string Id,
    GameInstallId InstallId,
    string GameName,
    DateTimeOffset CreatedAt,
    string Operation,
    int FileCount,
    long SizeBytes,
    ManagedBackupStatus Status,
    string Location,
    IReadOnlyList<string> Files);

public sealed record BackupRestorePreview(
    int FilesRestored,
    int CurrentManagedFilesReplaced,
    int ForeignFilesAffected,
    int ConfigurationFilesRestored,
    bool CanRestore,
    IReadOnlyList<string> BlockingReasons);

public sealed record InterruptedOperationEntry(
    string TransactionId,
    GameInstallId InstallId,
    string GameName,
    string Operation,
    string State,
    int CompletedSteps,
    int PendingSteps,
    bool BackupAvailable,
    string RecommendedAction,
    string JournalPath);

public interface IBackupRecoveryCatalog
{
    Task<IReadOnlyList<ManagedBackupEntry>> ListBackupsAsync(
        InstalledGame game,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InterruptedOperationEntry>> ListInterruptedAsync(
        InstalledGame game,
        CancellationToken cancellationToken = default);

    BackupRestorePreview PreviewRestore(DeploymentPlan restorePlan);
    Task DeleteBackupAsync(InstalledGame game, string backupId, bool confirmed, CancellationToken cancellationToken = default);
    Task<long> GetTotalSizeAsync(IEnumerable<InstalledGame> games, CancellationToken cancellationToken = default);
}

public sealed class BackupRecoveryCatalog : IBackupRecoveryCatalog
{
    public async Task<IReadOnlyList<ManagedBackupEntry>> ListBackupsAsync(
        InstalledGame game,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(game.GameRoot, ".rhi-linux", "backups");
        if (!Directory.Exists(root)) return [];
        var result = new List<ManagedBackupEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GameOverrideValidator.IsContained(root, directory)) continue;
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => GameOverrideValidator.IsContained(directory, path))
                .Order(StringComparer.Ordinal)
                .ToArray();
            long size = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { size += new FileInfo(file).Length; }
                catch (IOException) { }
            }
            var id = Path.GetFileName(directory);
            var journal = Path.Combine(game.GameRoot, ".rhi-linux", "transactions", id + ".json");
            var (operation, created, state) = await ReadJournalSummaryAsync(journal, cancellationToken).ConfigureAwait(false);
            result.Add(new(
                id,
                game.InstallId,
                game.Name,
                created ?? Directory.GetCreationTimeUtc(directory),
                operation ?? "Managed file operation",
                files.Length,
                size,
                state is null || DeploymentRecoveryProbe.IsTerminalJournalState(state)
                    ? ManagedBackupStatus.Restorable
                    : ManagedBackupStatus.InUse,
                directory,
                files.Select(file => Path.GetRelativePath(directory, file)).ToArray()));
        }
        return result.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<InterruptedOperationEntry>> ListInterruptedAsync(
        InstalledGame game,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(game.GameRoot, ".rhi-linux", "transactions");
        if (!Directory.Exists(root)) return [];
        var result = new List<InterruptedOperationEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var rootElement = document.RootElement;
                var state = Text(rootElement, "state") ?? "Unknown";
                if (DeploymentRecoveryProbe.IsTerminalJournalState(state)) continue;
                var id = Text(rootElement, "id") ?? Path.GetFileNameWithoutExtension(path);
                var operation = Text(rootElement, "action") ?? "Interrupted file operation";
                var operations = Property(rootElement, "operations");
                var completed = 0;
                var pending = 0;
                if (operations is { ValueKind: JsonValueKind.Array })
                {
                    foreach (var item in operations.Value.EnumerateArray())
                    {
                        var status = Text(item, "status");
                        if (IsCompletedStepState(status)) completed++;
                        else pending++;
                    }
                }
                var backup = Directory.Exists(Path.Combine(game.GameRoot, ".rhi-linux", "backups", id)) ||
                             Directory.Exists(Path.Combine(root, id + ".snapshots"));
                result.Add(new(id, game.InstallId, game.Name, operation, state, completed, pending, backup,
                    backup ? "Restore the previous state before starting another deployment." :
                        "Review the transaction details; automatic recovery data may be incomplete.", path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                result.Add(new(Path.GetFileNameWithoutExtension(path), game.InstallId, game.Name,
                    "Interrupted file operation", "Unreadable", 0, 0, false,
                    "Open details and repair access to the transaction journal before continuing.", path));
            }
        }
        return result;
    }

    public BackupRestorePreview PreviewRestore(DeploymentPlan restorePlan)
    {
        var restores = restorePlan.Operations.Where(operation => operation.Type == DeploymentOperationType.RestoreBackup).ToArray();
        var foreign = restorePlan.Warnings.Count(warning =>
            warning.Contains("foreign", StringComparison.OrdinalIgnoreCase) ||
            warning.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        var blocking = foreign == 0
            ? Array.Empty<string>()
            : ["Restore would replace a foreign or unexpected file. Review the target before continuing."];
        return new(
            restores.Length,
            restores.Count(operation => File.Exists(operation.Target)),
            foreign,
            restores.Count(operation => operation.Target.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)),
            foreign == 0,
            blocking);
    }

    public Task DeleteBackupAsync(
        InstalledGame game,
        string backupId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new InvalidOperationException("Backup deletion requires explicit confirmation.");
        if (string.IsNullOrWhiteSpace(backupId) || backupId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            backupId is "." or ".." || backupId.Contains(Path.DirectorySeparatorChar) ||
            backupId.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("The backup identifier is invalid.");
        var root = Path.Combine(game.GameRoot, ".rhi-linux", "backups");
        var target = Path.GetFullPath(Path.Combine(root, backupId));
        if (!GameOverrideValidator.IsContained(root, target) || Path.GetDirectoryName(target) != Path.GetFullPath(root))
            throw new InvalidDataException("The backup path is outside RHI-owned storage.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(target)) Directory.Delete(target, true);
        return Task.CompletedTask;
    }

    public async Task<long> GetTotalSizeAsync(
        IEnumerable<InstalledGame> games,
        CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (var game in games.DistinctBy(item => item.InstallId))
            foreach (var backup in await ListBackupsAsync(game, cancellationToken).ConfigureAwait(false))
                total += backup.SizeBytes;
        return total;
    }

    private static async Task<(string? Operation, DateTimeOffset? Created, string? State)> ReadJournalSummaryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return (null, null, null);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            DateTimeOffset? started = DateTimeOffset.TryParse(Text(root, "startedUtc"), out var parsed) ? parsed : null;
            return (Text(root, "action"), started, Text(root, "state"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null, "Unreadable");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool IsCompletedStepState(string? state) =>
        state is not null &&
        (state.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("skipped", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("rolledBack", StringComparison.OrdinalIgnoreCase));

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }
}
