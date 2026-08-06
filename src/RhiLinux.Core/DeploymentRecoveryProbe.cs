namespace RhiLinux.Core;

public sealed record DeploymentRecoveryStatus(
    bool HasInterruptedTransaction,
    bool HasManagedBackups,
    string? Message,
    IReadOnlyList<string> JournalIds);

public static class DeploymentRecoveryProbe
{
    private const string MetadataDirectoryName = ".rhi-linux";

    public static DeploymentRecoveryStatus Probe(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return new(false, false, null, []);

        var journalIds = new List<string>();
        string? message = null;
        var transactionDirectory = Path.Combine(gameRoot, MetadataDirectoryName, "transactions");
        if (Directory.Exists(transactionDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(transactionDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains("\"Completed\"", StringComparison.Ordinal) ||
                        text.Contains("\"RolledBack\"", StringComparison.Ordinal))
                        continue;
                    var id = Path.GetFileNameWithoutExtension(path);
                    journalIds.Add(id);
                    message ??= $"Interrupted deployment transaction {id} requires recovery.";
                }
                catch (IOException)
                {
                    journalIds.Add(Path.GetFileNameWithoutExtension(path));
                    message ??= "An interrupted deployment journal could not be read safely.";
                }
            }
        }

        var backupDirectory = Path.Combine(gameRoot, MetadataDirectoryName, "backups");
        var hasBackups = Directory.Exists(backupDirectory) &&
            Directory.EnumerateFileSystemEntries(backupDirectory).Any();
        return new(journalIds.Count > 0, hasBackups, message, journalIds);
    }
}
