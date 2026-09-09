using System.Text.Json;

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
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    var state = ReadState(document.RootElement);
                    if (IsTerminalJournalState(state))
                        continue;
                    var id = Path.GetFileNameWithoutExtension(path);
                    journalIds.Add(id);
                    message ??= $"Interrupted deployment transaction {id} requires recovery.";
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
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

    internal static bool IsTerminalJournalState(string? state) =>
        state is not null &&
        (state.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("rolledBack", StringComparison.OrdinalIgnoreCase));

    private static string? ReadState(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals("state", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }
}
