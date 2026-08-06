using System.Text.Json.Serialization;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class SourceIndexDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset UpdatedUtc { get; set; }
    public long ScanGeneration { get; set; }
    public Dictionary<string, string> Fingerprints { get; set; } = new(StringComparer.Ordinal);
    public List<SourceGameRecord> Records { get; set; } = [];
    public List<SourceDiagnostic> Diagnostics { get; set; } = [];
    public Dictionary<string, double> TimingsMilliseconds { get; set; } = new(StringComparer.Ordinal);
}

public interface ISourceIndexStore
{
    Task<SourceIndexDocument> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SourceIndexDocument document, CancellationToken cancellationToken = default);
    Task<SourceIndexDocument?> TryLoadAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonSourceIndexStore(string path) : ISourceIndexStore
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<SourceIndexDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        await TryLoadAsync(cancellationToken).ConfigureAwait(false) ?? new SourceIndexDocument();

    public async Task<SourceIndexDocument?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var input = File.OpenRead(path);
            var document = await System.Text.Json.JsonSerializer.DeserializeAsync<SourceIndexDocument>(input, Options, cancellationToken)
                .ConfigureAwait(false);
            if (document is null) return null;
            if (document.SchemaVersion > SourceIndexDocument.CurrentSchemaVersion)
                return null;
            document.SchemaVersion = SourceIndexDocument.CurrentSchemaVersion;
            return document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Text.Json.JsonException or InvalidDataException)
        {
            try
            {
                File.Copy(path, path + ".corrupt." + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true);
            }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    public async Task SaveAsync(SourceIndexDocument document, CancellationToken cancellationToken = default)
    {
        document.SchemaVersion = SourceIndexDocument.CurrentSchemaVersion;
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Source index path has no directory.");
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
}
