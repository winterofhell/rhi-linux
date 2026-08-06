using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Gui;

public sealed class UiPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 820;
    public double SidebarWidth { get; set; } = 310;
    public uint? SelectedAppId { get; set; }
    public string? SelectedInstallId { get; set; }
    public string Theme { get; set; } = "System";
    public string SearchText { get; set; } = string.Empty;
    public int CacheLimitMiB { get; set; } = 5 * 1024;
    public bool ReduceMotion { get; set; }
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public string AdditionalSteamLibrary { get; set; } = string.Empty;
    public string LibraryFilter { get; set; } = "All";
    public bool WatchSteamLibraries { get; set; } = true;
    public bool ScanAllSources { get; set; } = true;
    public bool EnableHeroic { get; set; } = true;
    public bool EnableLegendary { get; set; } = true;
    public bool EnableLutris { get; set; } = true;
    public bool EnableBottles { get; set; } = true;
    public bool EnableMinigalaxy { get; set; } = true;
    public bool AutomaticallyEvaluateReadiness { get; set; } = true;
    public bool ShowUnsupportedNativeGames { get; set; } = true;
    public bool WarnBeforeAntiCheatDeployments { get; set; } = true;
    public bool PreferExistingManagedVersions { get; set; } = true;
    public bool RefreshArtifactMetadataOnStartup { get; set; } = true;
}

public interface IUiPreferencesStore
{
    Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default);
}

public sealed class JsonUiPreferencesStore(string path) : IUiPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new UiPreferences();
        await using var input = File.OpenRead(path);
        var preferences = await JsonSerializer.DeserializeAsync<UiPreferences>(input, Options, cancellationToken)
            .ConfigureAwait(false);
        return preferences is { SchemaVersion: 1 } ? preferences : new UiPreferences();
    }

    public async Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Preferences path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(output, preferences, Options, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
