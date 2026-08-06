using RhiLinux.Core;
using RhiLinux.Sources;

namespace RhiLinux.Tests;

public sealed class LibraryIndexTests
{
    [Fact]
    public async Task CompleteGenerationPersistsFingerprintsAndRecords()
    {
        using var temp = new TestDirectory();
        var dbPath = temp.Combine("library.db");
        await using var index = new SqliteLibraryIndex(dbPath);
        await index.OpenAsync();

        var generation = await index.BeginGenerationAsync(false);
        var record = new SourceGameRecord(
            "heroic",
            GameStore.Gog,
            GameLauncher.Heroic,
            "1423049311",
            "Cyberpunk 2077",
            "/games/Cyberpunk",
            "/games/Cyberpunk/bin/x64/Cyberpunk2077.exe",
            "/prefixes/Cyberpunk",
            null,
            GameBinaryPlatform.Windows,
            CompatibilityEnvironment.Wine,
            "/heroic/gog_store/installed.json",
            null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(),
            []);
        await index.CompleteGenerationAsync(
            generation,
            new Dictionary<string, string> { ["heroic:/heroic"] = "fp1" },
            [record],
            [],
            [],
            new Dictionary<string, double>(),
            CancellationToken.None);

        var fingerprints = await index.LoadFingerprintsAsync();
        var records = await index.LoadCachedRecordsAsync();
        Assert.Equal("fp1", fingerprints["heroic:/heroic"]);
        Assert.Equal("1423049311", Assert.Single(records).ExternalId);
    }

    [Fact]
    public async Task InterruptedGenerationKeepsPreviousActiveRecords()
    {
        using var temp = new TestDirectory();
        var dbPath = temp.Combine("library.db");
        await using var index = new SqliteLibraryIndex(dbPath);
        await index.OpenAsync();

        var first = await index.BeginGenerationAsync(false);
        await index.CompleteGenerationAsync(
            first,
            new Dictionary<string, string> { ["heroic:/heroic"] = "good" },
            [
                new SourceGameRecord(
                    "heroic", GameStore.Gog, GameLauncher.Heroic, "1", "Good",
                    "/games/Good", null, null, null, GameBinaryPlatform.Windows,
                    CompatibilityEnvironment.Wine, "/meta.json", null, DateTimeOffset.UtcNow,
                    new Dictionary<string, string>(), [])
            ],
            [],
            [],
            new Dictionary<string, double>(),
            CancellationToken.None);

        var interrupted = await index.BeginGenerationAsync(true);
        await index.MarkGenerationFailedAsync(interrupted);
        var fingerprints = await index.LoadFingerprintsAsync();
        var records = await index.LoadCachedRecordsAsync();
        Assert.Equal("good", fingerprints["heroic:/heroic"]);
        Assert.Equal("Good", Assert.Single(records).Name);
    }
}

public sealed class LibraryCollectionDiffTests
{
    [Fact]
    public void ApplyUpdatesAddsAndRemovesByInstallId()
    {
        var current = new List<InstalledGame>
        {
            Game("a", "Alpha"),
            Game("b", "Bravo")
        };
        var incoming = new[]
        {
            Game("b", "Bravo Updated"),
            Game("c", "Charlie")
        };

        var result = LibraryCollectionDiff.Apply(current, incoming);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Removed);
        Assert.Equal(2, current.Count);
        Assert.Contains(current, game => game.InstallId.Value == "b" && game.Name == "Bravo Updated");
        Assert.Contains(current, game => game.InstallId.Value == "c");
        Assert.DoesNotContain(current, game => game.InstallId.Value == "a");
    }

    private static InstalledGame Game(string id, string name) => new(
        new GameInstallId(id),
        name,
        GameStore.Other,
        GameLauncher.Manual,
        id,
        null,
        $"/games/{id}",
        null,
        null,
        null,
        GameBinaryPlatform.Windows,
        CompatibilityEnvironment.Wine,
        [],
        null,
        []);
}

public sealed class CliJsonIdentityTests
{
    [Fact]
    public void InstallJsonUsesNullableSteamAppId()
    {
        var steam = InstalledGame.FromSteamGame(new SteamGame(
            1091500, "Cyberpunk 2077", "/steam", "/steam", "/games/cp", "/pfx",
            "/games/cp/game.exe", "/games/cp", DetectionConfidence.High, "fixture", GameEngine.Unknown, []));
        var heroic = new InstalledGame(
            GameInstallId.Create(GameStore.Gog, GameLauncher.Heroic, "1423049311", "/games/cp", "/games/cp/game.exe"),
            "Cyberpunk 2077",
            GameStore.Gog,
            GameLauncher.Heroic,
            "1423049311",
            null,
            "/games/cp",
            "/games/cp/game.exe",
            null,
            "/games/cp",
            GameBinaryPlatform.Windows,
            CompatibilityEnvironment.Wine,
            [],
            null,
            []);

        Assert.Equal(1091500u, steam.SteamAppId);
        Assert.Null(heroic.SteamAppId);
        Assert.StartsWith("steam:1091500:", steam.EffectiveInstallId, StringComparison.Ordinal);
        Assert.StartsWith("heroic:gog:1423049311:", heroic.EffectiveInstallId, StringComparison.Ordinal);
        Assert.DoesNotContain(heroic.EffectiveInstallId, "0x", StringComparison.OrdinalIgnoreCase);
    }
}
