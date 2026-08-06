using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Tests;

public sealed class StateMigrationTests
{
    [Fact]
    public async Task Schema1MigratesToSchema2WithInstallIdKeys()
    {
        using var temp = new TestDirectory();
        var path = temp.Combine("data", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var legacy = """
            {
              "schemaVersion": 1,
              "discoveredGames": [
                {
                  "appId": 42,
                  "name": "Fixture",
                  "steamRoot": "/virtual/steam",
                  "libraryRoot": "/virtual/steam",
                  "gameRoot": "/virtual/steam/steamapps/common/Fixture",
                  "protonPrefix": "/virtual/steam/steamapps/compatdata/42/pfx",
                  "executable": "/virtual/steam/steamapps/common/Fixture/Game.exe",
                  "deploymentDirectory": "/virtual/steam/steamapps/common/Fixture",
                  "confidence": "High",
                  "selectionReason": "fixture",
                  "engine": "Unknown",
                  "candidates": []
                }
              ],
              "overrides": {
                "42": { "executable": "Game.exe", "deploymentDirectory": "bin" }
              },
              "artifactReferencesByAppId": {
                "42": ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]
              },
              "transactions": [
                {
                  "id": "tx-1",
                  "appId": 42,
                  "action": "install",
                  "startedUtc": "2024-01-01T00:00:00Z",
                  "completedUtc": null,
                  "rolledBack": false,
                  "completedOperations": [],
                  "error": null
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, legacy);

        var loaded = await new JsonStateStore(path).LoadAsync();

        Assert.Equal(2, loaded.SchemaVersion);
        var game = Assert.Single(loaded.DiscoveredGames);
        Assert.Equal(42u, game.SteamAppId);
        Assert.False(string.IsNullOrWhiteSpace(game.InstallId));
        Assert.StartsWith("steam:42:", game.InstallId, StringComparison.Ordinal);
        Assert.True(loaded.Overrides.ContainsKey(GameInstallId.LegacySteam(42).Value));
        Assert.Equal("Game.exe", loaded.Overrides[GameInstallId.LegacySteam(42).Value].Executable);
        Assert.True(loaded.ArtifactReferencesByInstallId.ContainsKey(GameInstallId.LegacySteam(42).Value));
        var transaction = Assert.Single(loaded.Transactions);
        Assert.Equal(GameInstallId.LegacySteam(42).Value, transaction.InstallId);
        Assert.Equal(42u, transaction.SteamAppId);
        Assert.Contains(loaded.MigrationDiagnostics, item => item.Contains("schema 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Schema2RoundTripsWithoutMigrationDiagnostics()
    {
        using var temp = new TestDirectory();
        var store = new JsonStateStore(temp.Combine("data", "state.json"));
        var state = new ApplicationState
        {
            SchemaVersion = 2,
            DiscoveredGames =
            [
                new PersistedGameEntry(
                    GameInstallId.LegacySteam(10).Value,
                    "Modern",
                    GameStore.Steam,
                    GameLauncher.Steam,
                    "10",
                    10,
                    "/virtual/game",
                    "/virtual/game/Game.exe",
                    "/virtual/game",
                    "/virtual/pfx")
            ]
        };
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Empty(loaded.MigrationDiagnostics);
        Assert.Equal(GameInstallId.LegacySteam(10).Value, Assert.Single(loaded.DiscoveredGames).InstallId);
    }
}
