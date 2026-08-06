using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed class ComponentDetector(GameProfileCatalog? profiles = null, ProxyDiagnosticsService? proxyDiagnostics = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly StackDetector stackDetector = new(profiles, proxyDiagnostics);

    public Task<IReadOnlyList<ComponentStatus>> DetectAsync(SteamGame game, CancellationToken cancellationToken = default) =>
        DetectAsync(game.ToDeploymentTarget(), cancellationToken);

    public Task<IReadOnlyList<ComponentStatus>> DetectAsync(InstalledGame game, CancellationToken cancellationToken = default) =>
        DetectAsync(game.ToDeploymentTarget(), cancellationToken);

    public async Task<IReadOnlyList<ComponentStatus>> DetectAsync(DeploymentTarget game, CancellationToken cancellationToken = default)
    {
        var snapshot = await stackDetector.DetectAsync(game, cancellationToken: cancellationToken);
        return StackDetector.ToComponentStatuses(snapshot);
    }

    public Task<StackSnapshot> DetectStackAsync(
        SteamGame game,
        long generation = 0,
        CancellationToken cancellationToken = default) =>
        DetectStackAsync(game.ToDeploymentTarget(), generation, cancellationToken);

    public Task<StackSnapshot> DetectStackAsync(
        InstalledGame game,
        long generation = 0,
        CancellationToken cancellationToken = default) =>
        DetectStackAsync(game.ToDeploymentTarget(), generation, cancellationToken);

    public Task<StackSnapshot> DetectStackAsync(
        DeploymentTarget game,
        long generation = 0,
        CancellationToken cancellationToken = default) =>
        stackDetector.DetectAsync(game, generation, cancellationToken);

    public static async Task<GameManifest> LoadManifestAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(gameRoot, ".rhi-linux", "manifest.json");
        if (!File.Exists(path)) return new GameManifest();
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<GameManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The ownership manifest is empty.");
        if (manifest.SchemaVersion is < 0 or > 2)
            throw new InvalidDataException($"Unsupported ownership manifest schema {manifest.SchemaVersion}.");
        if (manifest.SchemaVersion < 2)
        {
            if (manifest.SchemaVersion == 0)
            {
                manifest.Files ??= [];
                manifest.ConfigurationPatches ??= [];
                manifest.TransactionIds ??= [];
            }
            if (string.IsNullOrWhiteSpace(manifest.InstallId) && manifest.SteamAppId is { } steamAppId)
                manifest.InstallId = GameInstallId.LegacySteam(steamAppId).Value;
            manifest.SchemaVersion = 2;
            manifest.MetadataMigrated = true;
        }
        return manifest;
    }
}
