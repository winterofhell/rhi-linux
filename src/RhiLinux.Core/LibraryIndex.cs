using System.Text.Json.Serialization;

namespace RhiLinux.Core;

public sealed class LibraryIndexDocument
{
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public const int CurrentSchemaVersion = 1;
    public int AnalyzerSchemaVersion { get; set; } = GameAnalyzerVersions.SchemaVersion;
    public DateTimeOffset UpdatedUtc { get; set; }
    public long ScanGeneration { get; set; }
    public List<IndexedGameEntry> Games { get; set; } = [];
    public List<string> SteamRoots { get; set; } = [];
    public List<string> Libraries { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed record IndexedGameEntry
{
    public string Store { get; init; } = "steam";
    public string StoreGameId { get; init; } = string.Empty;
    public string CanonicalRoot { get; init; } = string.Empty;
    public string? ManifestPath { get; init; }
    public long ManifestSize { get; init; }
    public long ManifestMtimeUtcTicks { get; init; }
    public long DirectoryFingerprintHash { get; init; }
    public int AnalyzerSchemaVersion { get; init; } = GameAnalyzerVersions.SchemaVersion;
    public DateTimeOffset IndexedUtc { get; init; }
    public DateTimeOffset? LastVerifiedUtc { get; init; }
    public string? SelectedExecutable { get; init; }
    public GameEngine Engine { get; init; }
    public DetectionConfidence Confidence { get; init; }
    public bool HasAntiCheat { get; init; }
    public bool IsNativeLinux { get; init; }
    public bool HasProtonPrefix { get; init; }
    public string? ProtonPrefix { get; init; }
    public string? CompatibilityTool { get; init; }
    public string? Error { get; init; }
    public SteamGame Game { get; set; } = null!;
}

public interface ILibraryIndexStore
{
    Task<LibraryIndexDocument> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LibraryIndexDocument document, CancellationToken cancellationToken = default);
    Task<LibraryIndexDocument?> TryLoadAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonLibraryIndexStore(string path) : ILibraryIndexStore
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<LibraryIndexDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        return loaded ?? new LibraryIndexDocument();
    }

    public async Task<LibraryIndexDocument?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var input = File.OpenRead(path);
            var document = await System.Text.Json.JsonSerializer.DeserializeAsync<LibraryIndexDocument>(input, Options, cancellationToken)
                .ConfigureAwait(false);
            if (document is null) return null;
            if (document.SchemaVersion > LibraryIndexDocument.CurrentSchemaVersion)
                return null;
            if (document.SchemaVersion < LibraryIndexDocument.CurrentSchemaVersion)
                document = Migrate(document);
            if (document.AnalyzerSchemaVersion != GameAnalyzerVersions.SchemaVersion)
            {
                document.Games.Clear();
                document.AnalyzerSchemaVersion = GameAnalyzerVersions.SchemaVersion;
            }

            return document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Text.Json.JsonException or InvalidDataException)
        {
            try
            {
                var backup = path + ".corrupt." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                File.Copy(path, backup, true);
            }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    public async Task SaveAsync(LibraryIndexDocument document, CancellationToken cancellationToken = default)
    {
        document.SchemaVersion = LibraryIndexDocument.CurrentSchemaVersion;
        document.AnalyzerSchemaVersion = GameAnalyzerVersions.SchemaVersion;
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Library index path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                await System.Text.Json.JsonSerializer.SerializeAsync(output, document, Options, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static LibraryIndexDocument Migrate(LibraryIndexDocument document)
    {
        document.SchemaVersion = LibraryIndexDocument.CurrentSchemaVersion;
        return document;
    }
}
