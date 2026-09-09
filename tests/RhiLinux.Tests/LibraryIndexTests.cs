using RhiLinux.Core;
using RhiLinux.Sources;
using Microsoft.Data.Sqlite;

namespace RhiLinux.Tests;

public sealed class LibraryIndexTests
{
    [Fact]
    public void SourceFingerprintStaysStableAcrossProcesses()
    {
        Assert.Equal("6c612924e4cfe41f", SourceRootDiscovery.SourceFingerprint("missing/one", "missing/two"));
    }

    [Fact]
    public void StableHashDoesNotDependOnTheRuntimeStringHashSeed()
    {
        Assert.Equal(-3750763034362895579L, StableHash.Ordinal(string.Empty));
        Assert.Equal(StableHash.OrdinalIgnoreCase("DXGI"), StableHash.OrdinalIgnoreCase("dxgi"));
        Assert.NotEqual(StableHash.Ordinal("DXGI"), StableHash.Ordinal("dxgi"));
    }

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

    [Fact]
    public async Task VersionOneSchemaMigratesTransactionallyToVersionTwo()
    {
        using var temp = new TestDirectory();
        var dbPath = temp.Combine("library.db");
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_info (
                  id INTEGER PRIMARY KEY CHECK (id = 1),
                  schema_version INTEGER NOT NULL,
                  active_generation INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO schema_info(id, schema_version, active_generation) VALUES (1, 1, 0);
                CREATE TABLE scan_generations (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  started_utc TEXT NOT NULL,
                  completed_utc TEXT,
                  status TEXT NOT NULL,
                  force_scan INTEGER NOT NULL DEFAULT 0,
                  timings_json TEXT
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var index = new SqliteLibraryIndex(dbPath);
        await index.OpenAsync();

        Assert.Equal(2, await index.GetSchemaVersionAsync());
        Assert.Equal(1, await index.GetReconciliationVersionAsync());
        Assert.Empty(await index.LoadSourceDocumentsAsync());
        Assert.Empty(await index.LoadGameRelationshipsAsync());
        Assert.Empty(await index.LoadAnalysisAsync());
    }

    [Fact]
    public async Task FutureSchemaVersionIsRejectedWithoutModification()
    {
        using var temp = new TestDirectory();
        var dbPath = temp.Combine("library.db");
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_info (
                  id INTEGER PRIMARY KEY CHECK (id = 1),
                  schema_version INTEGER NOT NULL,
                  active_generation INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO schema_info(id, schema_version, active_generation) VALUES (1, 99, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var index = new SqliteLibraryIndex(dbPath);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => index.OpenAsync());

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedMigrationRollsBackSchemaChanges()
    {
        using var temp = new TestDirectory();
        var dbPath = temp.Combine("library.db");
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_info (
                  id INTEGER PRIMARY KEY CHECK (id = 1),
                  schema_version INTEGER NOT NULL,
                  active_generation INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO schema_info(id, schema_version, active_generation) VALUES (1, 1, 0);
                CREATE TABLE scan_generations (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  started_utc TEXT NOT NULL,
                  completed_utc TEXT,
                  status TEXT NOT NULL,
                  force_scan INTEGER NOT NULL DEFAULT 0,
                  timings_json TEXT
                );
                CREATE TABLE source_documents(document_id TEXT PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var index = new SqliteLibraryIndex(dbPath))
            await Assert.ThrowsAsync<SqliteException>(() => index.OpenAsync());

        await using var verify = new SqliteConnection($"Data Source={dbPath}");
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "SELECT schema_version FROM schema_info WHERE id=1;";
        Assert.Equal(1L, await version.ExecuteScalarAsync());
        await using var columns = verify.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('schema_info') WHERE name='reconciliation_version';";
        Assert.Null(await columns.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CorruptDatabaseIsQuarantinedAndRecreated()
    {
        using var temp = new TestDirectory();
        var dbPath = temp.Combine("library.db");
        await File.WriteAllBytesAsync(dbPath, [0x52, 0x48, 0x49, 0x00, 0x01]);

        await using var index = new SqliteLibraryIndex(dbPath);
        await index.OpenAsync();

        Assert.Equal(SqliteLibraryIndex.SchemaVersion, await index.GetSchemaVersionAsync());
        Assert.Single(Directory.GetFiles(temp.Path, "library.db.corrupt.*"));
    }

    [Fact]
    public async Task OpeningDatabaseRecoversInterruptedGenerationWithoutReplacingActiveData()
    {
        using var temp = new TestDirectory();
        var dbPath = temp.Combine("library.db");
        long active;
        await using (var index = new SqliteLibraryIndex(dbPath))
        {
            await index.OpenAsync();
            active = await index.BeginGenerationAsync(false);
            await index.CompleteGenerationAsync(
                active,
                new Dictionary<string, string> { ["heroic:/fixture"] = "stable" },
                [Record("stable", "Stable", "/fixture/stable.json")],
                [], [], new Dictionary<string, double>());
            _ = await index.BeginGenerationAsync(false);
        }

        await using var reopened = new SqliteLibraryIndex(dbPath);
        await reopened.OpenAsync();

        Assert.Equal(active, await reopened.GetActiveGenerationAsync());
        Assert.Equal("Stable", Assert.Single(await reopened.LoadCachedRecordsAsync()).Name);
    }

    [Fact]
    public async Task JsonSourceIndexImportsOnlyOnceAndRemainsAvailable()
    {
        using var temp = new TestDirectory();
        var jsonPath = temp.Combine("source-index.json");
        var store = new JsonSourceIndexStore(jsonPath);
        await store.SaveAsync(new SourceIndexDocument
        {
            Fingerprints = new Dictionary<string, string> { ["heroic:/fixture"] = "one" },
            Records = [Record("one", "First", "/fixture/one.json")]
        });

        await using var index = new SqliteLibraryIndex(temp.Combine("library.db"));
        await index.OpenAsync();
        await index.ImportJsonSourceIndexAsync(jsonPath);
        await store.SaveAsync(new SourceIndexDocument
        {
            Fingerprints = new Dictionary<string, string> { ["heroic:/fixture"] = "two" },
            Records = [Record("two", "Second", "/fixture/two.json")]
        });
        await index.ImportJsonSourceIndexAsync(jsonPath);

        Assert.True(File.Exists(jsonPath));
        Assert.Equal("First", Assert.Single(await index.LoadCachedRecordsAsync()).Name);
        Assert.Equal("one", (await index.LoadFingerprintsAsync())["heroic:/fixture"]);
    }

    [Fact]
    public async Task VersionTwoPersistsDocumentsRelationshipsAndAnalysis()
    {
        using var temp = new TestDirectory();
        var metadata = temp.File("metadata/game.json", "{}");
        var root = temp.Directory("game");
        var record = Record("one", "Fixture", metadata) with { InstallRoot = root };
        var game = Assert.Single(GameReconciliation.Reconcile([record]).Games);
        var generationValue = 1L;
        var document = SourceDocumentEntry.FromPath("heroic", temp.Path, metadata, 4, generationValue);
        var relationship = new GameSourceRelationship(
            game.InstallId, "heroic", record.ExternalId, metadata, 0,
            DetectionConfidence.High, game.FieldSelections ?? [], generationValue);
        var analysis = new PersistedGameAnalysis(
            game.InstallId, 1, "analysis-input", game.Executable, [], game.Engine, null,
            7, 3, 12, game, generationValue);

        await using var index = new SqliteLibraryIndex(temp.Combine("library.db"));
        await index.OpenAsync();
        var generation = await index.BeginGenerationAsync(false);
        await index.CompleteGenerationAsync(
            generation,
            new Dictionary<string, string> { [$"heroic:{temp.Path}"] = "fingerprint" },
            [record],
            [game],
            [],
            new Dictionary<string, double>(),
            [document with { LastSuccessfulParseGeneration = generation }],
            [relationship with { ReconciliationGeneration = generation }],
            [analysis with { Generation = generation }],
            GameReconciliation.RulesVersion);

        Assert.Equal(4, Assert.Single(await index.LoadSourceDocumentsAsync()).ParserVersion);
        Assert.Equal(game.InstallId, Assert.Single(await index.LoadGameRelationshipsAsync()).InstallId);
        Assert.Equal("analysis-input", (await index.LoadAnalysisAsync())[game.InstallId].InputFingerprint);
    }

    private static SourceGameRecord Record(string id, string name, string metadataPath) => new(
        "heroic", GameStore.Epic, GameLauncher.Heroic, id, name,
        $"/games/{id}", null, null, null, GameBinaryPlatform.Windows,
        CompatibilityEnvironment.Wine, metadataPath, null, DateTimeOffset.UnixEpoch,
        new Dictionary<string, string>(), []);
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
