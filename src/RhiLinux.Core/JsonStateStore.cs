using System.Text.Json;

namespace RhiLinux.Core;

public interface IStateStore
{
    Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default);
}

public sealed class JsonStateStore(string path) : IStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new ApplicationState();
        await using var input = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<ApplicationState>(input, Options, cancellationToken)
            ?? throw new InvalidDataException($"State file '{path}' is empty.");
        return state.SchemaVersion switch
        {
            ApplicationState.CurrentSchemaVersion => state,
            > ApplicationState.CurrentSchemaVersion => throw new InvalidDataException("State was written by a newer RHI Linux version."),
            _ => Migrate(state)
        };
    }

    public async Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default)
    {
        state.SchemaVersion = ApplicationState.CurrentSchemaVersion;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("State path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(output, state, Options, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ApplicationState Migrate(ApplicationState state)
    {
        state.SchemaVersion = ApplicationState.CurrentSchemaVersion;
        return state;
    }
}
