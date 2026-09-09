using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhiLinux.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityOutcome
{
    Success,
    SuccessWithManualStep,
    Failed,
    RolledBack,
    RollbackFailed,
    Cancelled
}

public sealed record ActivityHistoryEntry(
    string Id,
    DateTimeOffset Timestamp,
    GameInstallId InstallId,
    string GameName,
    string Action,
    ActivityOutcome Outcome,
    int FilesChanged,
    int BackupsCreated,
    string? LaunchConfiguration,
    IReadOnlyList<string> Files,
    string? Summary)
{
    public bool HasLaunchConfiguration => !string.IsNullOrWhiteSpace(LaunchConfiguration);
    public bool HasFiles => Files.Count > 0;
}

public interface IActivityHistoryStore
{
    Task<IReadOnlyList<ActivityHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default);
    Task AppendAsync(ActivityHistoryEntry entry, CancellationToken cancellationToken = default);
}

public sealed class JsonActivityHistoryStore(string path, int maximumEntries = 200) : IActivityHistoryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<ActivityHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ActivityHistoryEntry>>(input, Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public async Task AppendAsync(ActivityHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            entries.Insert(0, Sanitize(entry));
            if (entries.Count > maximumEntries) entries.RemoveRange(maximumEntries, entries.Count - maximumEntries);
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("History path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 81920, FileOptions.WriteThrough))
                    await JsonSerializer.SerializeAsync(output, entries, Options, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static ActivityHistoryEntry Sanitize(ActivityHistoryEntry entry) => entry with
    {
        Files = entry.Files.Take(100).Select(value => value.Length <= 1024 ? value : value[..1024]).ToArray(),
        Summary = entry.Summary is { Length: > 2048 } value ? value[..2048] : entry.Summary,
        LaunchConfiguration = entry.LaunchConfiguration is { Length: > 2048 } launch ? launch[..2048] : entry.LaunchConfiguration
    };
}
