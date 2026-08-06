using System.Text.Json;
using System.Text.Json.Serialization;

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
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new ApplicationState();
        await using var input = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement) &&
                            schemaElement.TryGetInt32(out var version)
            ? version
            : 1;

        if (schemaVersion > ApplicationState.CurrentSchemaVersion)
            throw new InvalidDataException("State was written by a newer RHI Linux version.");

        if (schemaVersion < ApplicationState.CurrentSchemaVersion)
            return MigrateFromLegacy(root, schemaVersion);

        var state = root.Deserialize<ApplicationState>(Options)
            ?? throw new InvalidDataException($"State file '{path}' is empty.");
        if (state.SchemaVersion != ApplicationState.CurrentSchemaVersion)
            state.SchemaVersion = ApplicationState.CurrentSchemaVersion;
        return state;
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
                await JsonSerializer.SerializeAsync(output, state, Options, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ApplicationState MigrateFromLegacy(JsonElement root, int schemaVersion)
    {
        var state = new ApplicationState
        {
            SchemaVersion = ApplicationState.CurrentSchemaVersion,
            LastScanUtc = root.TryGetProperty("lastScanUtc", out var lastScan) &&
                          lastScan.TryGetDateTimeOffset(out var lastScanUtc)
                ? lastScanUtc
                : default
        };

        if (schemaVersion == ApplicationState.CurrentSchemaVersion)
            return state;

        MigrateDiscoveredGames(root, state);
        MigrateOverrides(root, state);
        MigrateArtifactReferences(root, state);
        MigrateTransactions(root, state);

        if (state.MigrationDiagnostics.Count == 0)
            state.MigrationDiagnostics.Add($"Migrated application state from schema {schemaVersion} to {ApplicationState.CurrentSchemaVersion}.");

        return state;
    }

    private static void MigrateDiscoveredGames(JsonElement root, ApplicationState state)
    {
        if (!root.TryGetProperty("discoveredGames", out var games) || games.ValueKind != JsonValueKind.Array)
            return;

        foreach (var gameElement in games.EnumerateArray())
        {
            try
            {
                if (gameElement.TryGetProperty("installId", out var installIdElement) &&
                    installIdElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(installIdElement.GetString()) &&
                    gameElement.TryGetProperty("installRoot", out _))
                {
                    var entry = gameElement.Deserialize<PersistedGameEntry>(Options);
                    if (entry is not null)
                    {
                        state.DiscoveredGames.Add(entry);
                        continue;
                    }
                }

                var steamGame = gameElement.Deserialize<SteamGame>(Options);
                if (steamGame is null) continue;

                var highBitSynthetic = steamGame.AppId >= 0x80000000u;
                if (steamGame.Store != GameStore.Steam || steamGame.AppId == 0 || highBitSynthetic)
                {
                    if (highBitSynthetic &&
                        !string.IsNullOrWhiteSpace(steamGame.InstallId) &&
                        !steamGame.InstallId.All(char.IsAsciiHexDigit))
                    {
                        state.DiscoveredGames.Add(PersistedGameEntry.FromSteamGame(steamGame with
                        {
                            AppId = 0,
                            Store = steamGame.Store == GameStore.Steam ? GameStore.Unknown : steamGame.Store
                        }));
                        state.MigrationDiagnostics.Add(
                            $"Recovered non-Steam installation '{steamGame.Name}' from installId '{steamGame.InstallId}'.");
                        continue;
                    }

                    if (highBitSynthetic)
                    {
                        state.OrphanedLegacyEntries.Add(new(
                            "discoveredGame",
                            steamGame.AppId.ToString(),
                            "Legacy synthetic AppID could not be mapped to a launcher-neutral install identity.",
                            gameElement.GetRawText()));
                        state.MigrationDiagnostics.Add(
                            $"Orphaned synthetic AppID {steamGame.AppId} for '{steamGame.Name}'.");
                        continue;
                    }
                }

                var installId = string.IsNullOrWhiteSpace(steamGame.InstallId)
                    ? GameInstallId.FromSteam(steamGame.AppId, steamGame.GameRoot, steamGame.Executable).Value
                    : steamGame.InstallId!;
                state.DiscoveredGames.Add(PersistedGameEntry.FromSteamGame(steamGame with { InstallId = installId }));
            }
            catch (JsonException exception)
            {
                state.MigrationDiagnostics.Add($"Skipped malformed discovered game during migration: {exception.Message}");
            }
        }
    }

    private static void MigrateOverrides(JsonElement root, ApplicationState state)
    {
        if (!root.TryGetProperty("overrides", out var overrides) || overrides.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in overrides.EnumerateObject())
        {
            var value = property.Value.Deserialize<GameOverride>(Options);
            if (value is null) continue;

            if (uint.TryParse(property.Name, out var appId))
            {
                if (appId >= 0x80000000u)
                {
                    state.OrphanedLegacyEntries.Add(new(
                        "override",
                        property.Name,
                        "Legacy synthetic override key could not be mapped to an install identity.",
                        property.Value.GetRawText()));
                    state.MigrationDiagnostics.Add($"Orphaned override for synthetic AppID {appId}.");
                    continue;
                }

                var installId = GameInstallId.LegacySteam(appId).Value;
                state.Overrides[installId] = value;
                continue;
            }

            state.Overrides[property.Name] = value;
        }
    }

    private static void MigrateArtifactReferences(JsonElement root, ApplicationState state)
    {
        JsonElement references = default;
        var found = root.TryGetProperty("artifactReferencesByInstallId", out references) ||
                    root.TryGetProperty("artifactReferencesByAppId", out references);
        if (!found || references.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in references.EnumerateObject())
        {
            var hashes = property.Value.Deserialize<List<string>>(Options) ?? [];
            if (uint.TryParse(property.Name, out var appId))
            {
                if (appId >= 0x80000000u)
                {
                    state.OrphanedLegacyEntries.Add(new(
                        "artifactReference",
                        property.Name,
                        "Legacy synthetic artifact reference key could not be mapped.",
                        property.Value.GetRawText()));
                    continue;
                }

                state.ArtifactReferencesByInstallId[GameInstallId.LegacySteam(appId).Value] = hashes;
                continue;
            }

            state.ArtifactReferencesByInstallId[property.Name] = hashes;
        }
    }

    private static void MigrateTransactions(JsonElement root, ApplicationState state)
    {
        if (!root.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in transactions.EnumerateArray())
        {
            try
            {
                if (item.TryGetProperty("installId", out var installIdElement) &&
                    installIdElement.ValueKind == JsonValueKind.String)
                {
                    var modern = item.Deserialize<TransactionRecord>(Options);
                    if (modern is not null)
                        state.Transactions.Add(modern);
                    continue;
                }

                if (!item.TryGetProperty("appId", out var appIdElement) || !appIdElement.TryGetUInt32(out var appId))
                {
                    state.MigrationDiagnostics.Add("Skipped transaction without installId or appId.");
                    continue;
                }

                if (appId >= 0x80000000u)
                {
                    state.OrphanedLegacyEntries.Add(new(
                        "transaction",
                        appId.ToString(),
                        "Legacy synthetic transaction AppID could not be mapped.",
                        item.GetRawText()));
                    continue;
                }

                var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                var action = item.TryGetProperty("action", out var actionElement) ? actionElement.GetString() ?? "unknown" : "unknown";
                var started = item.TryGetProperty("startedUtc", out var startedElement) && startedElement.TryGetDateTimeOffset(out var startedUtc)
                    ? startedUtc
                    : DateTimeOffset.UtcNow;
                DateTimeOffset? completed = item.TryGetProperty("completedUtc", out var completedElement) &&
                                            completedElement.ValueKind != JsonValueKind.Null &&
                                            completedElement.TryGetDateTimeOffset(out var completedUtc)
                    ? completedUtc
                    : null;
                var rolledBack = item.TryGetProperty("rolledBack", out var rolledBackElement) && rolledBackElement.GetBoolean();
                var operations = item.TryGetProperty("completedOperations", out var opsElement)
                    ? opsElement.Deserialize<List<string>>(Options) ?? []
                    : [];
                var error = item.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
                state.Transactions.Add(new(
                    id,
                    GameInstallId.LegacySteam(appId).Value,
                    action,
                    started,
                    completed,
                    rolledBack,
                    operations,
                    error,
                    appId));
            }
            catch (JsonException exception)
            {
                state.MigrationDiagnostics.Add($"Skipped malformed transaction during migration: {exception.Message}");
            }
        }
    }
}
