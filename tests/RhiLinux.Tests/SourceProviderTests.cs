using Microsoft.Data.Sqlite;
using RhiLinux.Core;
using RhiLinux.Sources;
using RhiLinux.Steam;

namespace RhiLinux.Tests;

public sealed class SourceProviderTests
{
    private static string FixturesRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", "sources"));

    [Fact]
    public async Task Heroic_ParsesEpicGogAmazonAndGamesConfig()
    {
        using var temp = new TestDirectory();
        var games = temp.Directory("games");
        var heroic = MaterializeHeroic(temp, games);
        CreatePe(temp, "games/Heroic/Control/Control_DX12.exe");
        CreatePe(temp, "games/Heroic/Cyberpunk 2077/bin/x64/Cyberpunk2077.exe");
        CreatePe(temp, "games/Heroic/New World/NewWorld.exe");
        CreatePe(temp, "games/Heroic/ThirdParty/EpicGamesLauncher.exe");
        temp.Directory("games", "Heroic", "Prefixes", "Control");
        temp.Directory("games", "Heroic", "Prefixes", "Cyberpunk");

        var provider = new HeroicGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });
        var roots = await provider.DiscoverRootsAsync(context);
        var root = Assert.Single(roots, item => item.Exists && !item.Deduplicated);
        Assert.Equal(heroic, root.CanonicalPath);

        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        Assert.Contains(result.Games, game => game.Store == GameStore.Epic && game.Name == "Control");
        Assert.Contains(result.Games, game => game.Store == GameStore.Gog && game.Name == "Cyberpunk 2077");
        Assert.Contains(result.Games, game => game.Store == GameStore.Amazon && game.Name == "New World");
        var control = result.Games.Single(game => game.ExternalId == "Control");
        Assert.Contains("Prefixes/Control", control.PrefixHint, StringComparison.Ordinal);
        Assert.EndsWith("Control_DX12.exe", control.ExecutableHint, StringComparison.Ordinal);
        Assert.Contains(result.SkippedRecords, game => game.Diagnostics.Any(d => d.Code == SourceDiagnosticCodes.DlcSkipped));
        Assert.Contains(result.Games, game => game.ExternalId == "ThirdPartyManaged" && game.RequiresConfirmation);
        Assert.Contains(result.Games, game => game.ExternalId == "MissingRootGame" &&
            game.Diagnostics.Any(d => d.Code == SourceDiagnosticCodes.InstallRootMissing));
        Assert.DoesNotContain(result.MetadataFiles, path => path.Contains("auth", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.MetadataFiles, path => path.Contains("current_user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Heroic_MalformedJsonProducesDiagnostic()
    {
        using var temp = new TestDirectory();
        var heroic = temp.Directory("config", "heroic", "legendaryConfig", "legendary");
        await File.WriteAllTextAsync(Path.Combine(heroic, "installed.json"), "{ not json");
        var provider = new HeroicGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });
        var root = Assert.Single(await provider.DiscoverRootsAsync(context), item => item.Exists);
        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        Assert.Contains(result.Diagnostics, d => d.Code == SourceDiagnosticCodes.SourceMetadataMalformed);
    }

    [Fact]
    public async Task Legendary_ParsesInstalledAndRespectsEnvironmentOverride()
    {
        using var temp = new TestDirectory();
        var legendary = MaterializeTree(temp, "legendary-env", Path.Combine(FixturesRoot, "legendary"));
        RewritePlaceholders(legendary, temp.Combine("games"), null);
        CreatePe(temp, "games/Heroic/Control/Control_DX12.exe");
        CreatePe(temp, "games/Legendary/Hades/x64/Hades.exe");

        var provider = new LegendaryGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["LEGENDARY_CONFIG_PATH"] = legendary,
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "unused-config"),
            ["HOME"] = temp.Path
        });
        var roots = await provider.DiscoverRootsAsync(context);
        var root = Assert.Single(roots, item => item.Exists && item.Kind == SourceRootKind.Environment);
        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        Assert.Equal(2, result.Games.Count);
        Assert.Contains(result.Games, game => game.Name == "Hades" && game.ExecutableHint!.EndsWith("Hades.exe", StringComparison.Ordinal));
        Assert.All(result.Games, game => Assert.Equal(GameLauncher.Legendary, game.Launcher));
    }

    [Fact]
    public async Task Lutris_ReadsSqliteYamlAndClassifiesRunners()
    {
        using var temp = new TestDirectory();
        var data = temp.Directory("data", "lutris");
        var config = temp.Directory("config", "lutris");
        var gamesYaml = temp.Directory("config", "lutris", "games");
        CopyFile(Path.Combine(FixturesRoot, "lutris", "games", "control-wine.yml"), Path.Combine(gamesYaml, "control-wine.yml"));
        CopyFile(Path.Combine(FixturesRoot, "lutris", "games", "native-linux.yml"), Path.Combine(gamesYaml, "native-linux.yml"));
        CopyFile(Path.Combine(FixturesRoot, "lutris", "games", "gog-game.yml"), Path.Combine(gamesYaml, "gog-game.yml"));
        CopyFile(Path.Combine(FixturesRoot, "lutris", "games", "malformed.yml"), Path.Combine(gamesYaml, "malformed.yml"));
        RewritePlaceholders(config, temp.Combine("games"), null);
        CreatePe(temp, "games/Lutris/Control/bin/Control_DX12.exe");
        CreatePe(temp, "games/Lutris/GogGame/Relative.exe");
        temp.Directory("games", "Lutris", "Prefixes", "Control");
        temp.Directory("games", "Lutris", "Prefixes", "GogGame");
        temp.Directory("games", "Lutris", "Control");
        temp.Directory("games", "Lutris", "Native");
        temp.File("games/Lutris/Native/start.sh", "#!/bin/sh\n");

        var pga = Path.Combine(data, "pga.db");
        await CreateLutrisDbAsync(pga, includeOptionalColumns: true);
        await File.WriteAllTextAsync(Path.Combine(config, "lutris.conf"), $"[lutris]\npga_path = {pga}\n");

        var provider = new LutrisGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_DATA_HOME"] = Path.Combine(temp.Path, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });
        var root = Assert.Single(await provider.DiscoverRootsAsync(context), item => item.Exists && !item.Deduplicated);
        Assert.Equal(pga, root.Attributes!["pgaPath"]);

        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        Assert.Contains(result.Games, game => game.Name == "Control" && game.Store == GameStore.Epic && game.IsActionable);
        Assert.Contains(result.Games, game => game.Name == "GOG Game" && game.Store == GameStore.Gog);
        Assert.Contains(result.Games, game => game.Name == "Native Linux" && !game.IsActionable);
        Assert.Contains(result.Games, game => game.Name == "Unsupported Runner" && !game.IsActionable);
        Assert.DoesNotContain(result.Games, game => game.Name == "Uninstalled");
        Assert.Contains(result.Diagnostics, d => d.Code == SourceDiagnosticCodes.SourceMetadataMalformed);
    }

    [Fact]
    public async Task Lutris_SupportsOldSchemaWithoutOptionalColumns()
    {
        using var temp = new TestDirectory();
        var data = temp.Directory("data", "lutris");
        temp.Directory("config", "lutris", "games");
        var pga = Path.Combine(data, "pga.db");
        await CreateLutrisDbAsync(pga, includeOptionalColumns: false);
        CreatePe(temp, "games/Lutris/Control/bin/Control_DX12.exe");

        var provider = new LutrisGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_DATA_HOME"] = Path.Combine(temp.Path, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });
        var root = Assert.Single(await provider.DiscoverRootsAsync(context), item => item.Exists);
        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        Assert.Contains(result.Games, game => game.Name == "Control");
    }

    [Fact]
    public async Task Bottles_ParsesProgramsAndFiltersLowConfidence()
    {
        using var temp = new TestDirectory();
        var bottlesRoot = MaterializeTree(temp, Path.Combine("data", "bottles", "bottles"), Path.Combine(FixturesRoot, "bottles"));
        foreach (var bottle in Directory.GetDirectories(bottlesRoot))
            RewritePlaceholders(bottle, temp.Combine("games"), bottle);

        CreatePe(temp, "data/bottles/bottles/GamingBottle/drive_c/Games/Control/Control_DX12.exe");
        CreatePe(temp, "data/bottles/bottles/GamingBottle/drive_c/Games/Absolute/Absolute.exe");
        CreatePe(temp, "data/bottles/bottles/GamingBottle/drive_c/Games/Relative/Relative.exe");
        CreatePe(temp, "data/bottles/bottles/GamingBottle/drive_c/Games/Control/setup.exe");

        var provider = new BottlesGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_DATA_HOME"] = Path.Combine(temp.Path, "data"),
            ["HOME"] = temp.Path
        });
        var root = Assert.Single(await provider.DiscoverRootsAsync(context), item => item.Exists);
        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        Assert.Contains(result.Games, game => game.Name == "Control" && game.ExecutableHint!.Contains("Control_DX12.exe", StringComparison.Ordinal));
        Assert.Contains(result.Games, game => game.Name == "Absolute Game");
        Assert.Contains(result.Games, game => game.Name == "Relative Game");
        Assert.Contains(result.SkippedRecords, game => game.Name == "Game Setup");
        Assert.Contains(result.SkippedRecords, game => game.Name == "Wine Configuration");
        Assert.Contains(result.Games.Concat(result.SkippedRecords), game =>
            game.Name == "Missing Game" && game.Diagnostics.Any(d => d.Code == SourceDiagnosticCodes.ExecutableMissing));
    }

    [Fact]
    public async Task Minigalaxy_ParsesWindowsAndNativeWithoutScanningInstallDir()
    {
        using var temp = new TestDirectory();
        var mini = MaterializeTree(temp, Path.Combine("config", "minigalaxy"), Path.Combine(FixturesRoot, "minigalaxy"));
        RewritePlaceholders(mini, temp.Combine("games"), null);
        CreatePe(temp, "games/GOG Games/The Witcher 3/bin/x64/witcher3.exe");
        temp.Directory("games", "GOG Games", "Native GOG Title");
        temp.File("games/GOG Games/Native GOG Title/start.sh", "#!/bin/sh\n");
        temp.Directory("games", "GOG Games", "Untracked Folder");
        temp.File("games/GOG Games/Untracked Folder/secret.exe", "not scanned");

        var provider = new MinigalaxyGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });
        var root = Assert.Single(await provider.DiscoverRootsAsync(context), item => item.Exists);
        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        Assert.Contains(result.Games, game => game.Name == "The Witcher 3" && game.IsActionable);
        Assert.Contains(result.Games, game => game.Name == "Native GOG Title" && !game.IsActionable);
        Assert.DoesNotContain(result.Games, game => game.Name.Contains("Untracked", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == SourceDiagnosticCodes.SourceMetadataMalformed);
        Assert.Contains(result.Games, game => game.Attributes.ContainsKey("unknownGogId"));
    }

    [Fact]
    public async Task Manual_ReadsJsonList()
    {
        using var temp = new TestDirectory();
        var manualFile = Path.Combine(temp.Directory("config", "rhi-linux"), "manual-games.json");
        var content = await File.ReadAllTextAsync(Path.Combine(FixturesRoot, "manual", "manual-games.json"));
        content = content.Replace("@GAMES@", temp.Combine("games"), StringComparison.Ordinal);
        await File.WriteAllTextAsync(manualFile, content);
        CreatePe(temp, "games/Heroic/Control/Control_DX12.exe");
        temp.Directory("games", "Heroic", "Prefixes", "Control");

        var provider = new ManualGameSourceProvider();
        var context = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });
        var root = Assert.Single(await provider.DiscoverRootsAsync(context), item => item.Exists);
        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        var game = Assert.Single(result.Games);
        Assert.Equal(GameLauncher.Manual, game.Launcher);
        Assert.Equal(GameStore.Epic, game.Store);
        Assert.Equal("manual-control", game.ExternalId);
    }

    [Fact]
    public async Task Reconciliation_MergesHeroicAndLegendaryDuplicates()
    {
        using var temp = new TestDirectory();
        var gamesRoot = temp.Directory("games");
        MaterializeHeroic(temp, gamesRoot);
        var legendary = MaterializeTree(temp, Path.Combine("config", "legendary"), Path.Combine(FixturesRoot, "legendary"));
        RewritePlaceholders(legendary, gamesRoot, null);
        CreatePe(temp, "games/Heroic/Control/Control_DX12.exe");
        CreatePe(temp, "games/Heroic/Cyberpunk 2077/bin/x64/Cyberpunk2077.exe");
        CreatePe(temp, "games/Heroic/New World/NewWorld.exe");
        CreatePe(temp, "games/Legendary/Hades/x64/Hades.exe");
        CreatePe(temp, "games/Heroic/ThirdParty/EpicGamesLauncher.exe");
        temp.Directory("games", "Heroic", "Prefixes", "Control");
        temp.Directory("games", "Heroic", "Prefixes", "Cyberpunk");

        var indexPath = temp.Combine("source-index.json");
        var service = new MultiSourceLibraryService(
            [
                new HeroicGameSourceProvider(),
                new LegendaryGameSourceProvider()
            ],
            analyzer: new GameAnalyzer(),
            sourceIndexStore: new JsonSourceIndexStore(indexPath));

        var discovery = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });
        var result = await service.ScanAsync(new MultiSourceScanRequest(
            DiscoveryContext: discovery,
            EnabledProviders: new HashSet<string>(StringComparer.Ordinal) { "heroic", "legendary" },
            ForceFullScan: true,
            AnalyzeChangedWindowsInstalls: false,
            SourceIndexPath: indexPath));

        var merged = Assert.Single(result.Games, game =>
            game.Sources.Any(source => source.ExternalId == "Control"));
        Assert.True(merged.Sources.Count >= 2);
        Assert.Contains(merged.Sources, source => source.Launcher == GameLauncher.Heroic);
        Assert.Contains(merged.Sources, source => source.Launcher == GameLauncher.Legendary);
        Assert.True(result.Reconciliation.DuplicatePhysicalInstallCount >= 1);
    }

    [Fact]
    public async Task Incremental_FingerprintSkipsUnchangedProvider()
    {
        using var temp = new TestDirectory();
        var legendary = MaterializeTree(temp, Path.Combine("config", "legendary"), Path.Combine(FixturesRoot, "legendary"));
        RewritePlaceholders(legendary, temp.Combine("games"), null);
        CreatePe(temp, "games/Heroic/Control/Control_DX12.exe");
        CreatePe(temp, "games/Legendary/Hades/x64/Hades.exe");

        var indexPath = temp.Combine("source-index.json");
        var provider = new LegendaryGameSourceProvider();
        var service = new MultiSourceLibraryService(
            [provider],
            analyzer: new GameAnalyzer(),
            sourceIndexStore: new JsonSourceIndexStore(indexPath));
        var discovery = SourceRootDiscovery.CreateContext(temp.Path, new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(temp.Path, "config"),
            ["HOME"] = temp.Path
        });

        var first = await service.ScanAsync(new MultiSourceScanRequest(
            DiscoveryContext: discovery,
            EnabledProviders: new HashSet<string> { "legendary" },
            ForceFullScan: true,
            AnalyzeChangedWindowsInstalls: false,
            SourceIndexPath: indexPath));
        Assert.False(first.UsedCache);
        Assert.NotEmpty(first.Games);

        var second = await service.ScanAsync(new MultiSourceScanRequest(
            DiscoveryContext: discovery,
            EnabledProviders: new HashSet<string> { "legendary" },
            ForceFullScan: false,
            AnalyzeChangedWindowsInstalls: false,
            SourceIndexPath: indexPath));
        Assert.True(second.ProviderResults.All(result => result.FromCache));
        Assert.Equal(first.Games.Count, second.Games.Count);
    }

    [Fact]
    public async Task SteamGameSourceProvider_EmitsSourceRecords()
    {
        using var temp = new TestDirectory();
        var steam = temp.Directory("steam");
        AddSteamGame(temp, "steam", 42, "Fixture Adventure", "Fixture", "Fixture.exe");

        var provider = new SteamGameSourceProvider(new SteamDiscoveryService(new ExecutableDetector()));
        var root = new GameSourceRoot(
            "steam", steam, steam, GameIdentity.NormalizePath(steam), SourceRootKind.Custom, true, true, false, null);
        var result = await provider.ScanAsync(root, new GameSourceScanContext(new Dictionary<string, string>(), true, 1));
        var game = Assert.Single(result.Games);
        Assert.Equal(GameStore.Steam, game.Store);
        Assert.Equal("42", game.ExternalId);
        Assert.Equal(GameLauncher.Steam, game.Launcher);
    }

    [Fact]
    public void GameReconciliation_KeepsDistinctInstallRootsSeparate()
    {
        var left = new SourceGameRecord(
            "heroic", GameStore.Epic, GameLauncher.Heroic, "same", "Same Title",
            "/games/a", "/games/a/game.exe", null, null, GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine,
            "/meta/a", null, DateTimeOffset.UtcNow, new Dictionary<string, string>(), []);
        var right = new SourceGameRecord(
            "legendary", GameStore.Epic, GameLauncher.Legendary, "same", "Same Title",
            "/games/b", "/games/b/game.exe", null, null, GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine,
            "/meta/b", null, DateTimeOffset.UtcNow, new Dictionary<string, string>(), []);
        var result = GameReconciliation.Reconcile([left, right]);
        Assert.Equal(2, result.Games.Count);
    }

    private static string MaterializeHeroic(TestDirectory temp, string gamesRoot)
    {
        var heroic = MaterializeTree(temp, Path.Combine("config", "heroic"), Path.Combine(FixturesRoot, "heroic"));
        RewritePlaceholders(heroic, gamesRoot, null);
        return heroic;
    }

    private static string MaterializeTree(TestDirectory temp, string relativeTarget, string sourceRoot)
    {
        var target = temp.Directory(relativeTarget.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        CopyDirectory(sourceRoot, target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(target, relative), true);
        }
    }

    private static void CopyFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, true);
    }

    private static void RewritePlaceholders(string root, string gamesRoot, string? bottleRoot)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            text = text.Replace("@GAMES@", gamesRoot, StringComparison.Ordinal);
            if (bottleRoot is not null)
                text = text.Replace("@BOTTLE@", bottleRoot, StringComparison.Ordinal);
            text = text.Replace("@PGA@", Path.Combine(root, "pga.db"), StringComparison.Ordinal);
            File.WriteAllText(file, text);
        }
    }

    private static void CreatePe(TestDirectory temp, string relative) => temp.Pe(relative);

    private static async Task CreateLutrisDbAsync(string path, bool includeOptionalColumns)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var sql = includeOptionalColumns
            ? """
              CREATE TABLE games (
                id INTEGER PRIMARY KEY,
                name TEXT,
                slug TEXT,
                platform TEXT,
                runner TEXT,
                executable TEXT,
                directory TEXT,
                installed INTEGER,
                configpath TEXT,
                service TEXT,
                service_id TEXT
              );
              """
            : """
              CREATE TABLE games (
                id INTEGER PRIMARY KEY,
                name TEXT,
                runner TEXT,
                executable TEXT,
                directory TEXT,
                installed INTEGER,
                configpath TEXT
              );
              """;
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = sql;
            await create.ExecuteNonQueryAsync();
        }

        await InsertLutrisRow(connection, includeOptionalColumns, 1, "Control", "control", "Windows", "wine",
            "bin/Control_DX12.exe", Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "games", "Lutris", "Control"),
            1, "control-wine", "egs", "Control");
        if (includeOptionalColumns)
        {
            await InsertLutrisRow(connection, true, 2, "GOG Game", "gog-game", "Windows", "wine",
                "Relative.exe", Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "games", "Lutris", "GogGame"),
                1, "gog-game", "gog", "12345");
            await InsertLutrisRow(connection, true, 3, "Native Linux", "native-linux", "Linux", "linux",
                "./start.sh", Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "games", "Lutris", "Native"),
                1, "native-linux", null, null);
            await InsertLutrisRow(connection, true, 4, "Unsupported Runner", "dos-game", "Windows", "dosbox",
                "game.exe", Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "games", "Lutris", "Dos"),
                1, null, null, null);
            await InsertLutrisRow(connection, true, 5, "Uninstalled", "gone", "Windows", "wine",
                "gone.exe", Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "games", "Lutris", "Gone"),
                0, null, null, null);
            await InsertLutrisRow(connection, true, 6, "Malformed Yaml", "malformed", "Windows", "wine",
                "broken.exe", Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "games", "Lutris", "Broken"),
                1, "malformed", "amazon", "amz-1");
        }
    }

    private static async Task InsertLutrisRow(
        SqliteConnection connection,
        bool full,
        int id,
        string name,
        string slug,
        string platform,
        string runner,
        string executable,
        string directory,
        int installed,
        string? configpath,
        string? service,
        string? serviceId)
    {
        directory = Path.GetFullPath(directory);
        await using var command = connection.CreateCommand();
        if (full)
        {
            command.CommandText =
                """
                INSERT INTO games (id, name, slug, platform, runner, executable, directory, installed, configpath, service, service_id)
                VALUES ($id, $name, $slug, $platform, $runner, $executable, $directory, $installed, $configpath, $service, $service_id);
                """;
            command.Parameters.AddWithValue("$slug", slug);
            command.Parameters.AddWithValue("$platform", platform);
            command.Parameters.AddWithValue("$service", (object?)service ?? DBNull.Value);
            command.Parameters.AddWithValue("$service_id", (object?)serviceId ?? DBNull.Value);
        }
        else
        {
            command.CommandText =
                """
                INSERT INTO games (id, name, runner, executable, directory, installed, configpath)
                VALUES ($id, $name, $runner, $executable, $directory, $installed, $configpath);
                """;
        }

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$runner", runner);
        command.Parameters.AddWithValue("$executable", executable);
        command.Parameters.AddWithValue("$directory", directory);
        command.Parameters.AddWithValue("$installed", installed);
        command.Parameters.AddWithValue("$configpath", (object?)configpath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddSteamGame(TestDirectory temp, string steamRelative, uint appId, string name, string folder, string exe)
    {
        var manifest =
            "\"AppState\"\n{\n\t\"appid\"\t\t\"" + appId + "\"\n\t\"name\"\t\t\"" + name +
            "\"\n\t\"installdir\"\t\t\"" + folder + "\"\n\t\"StateFlags\"\t\t\"4\"\n}\n";
        temp.File($"{steamRelative}/steamapps/appmanifest_{appId}.acf", manifest);
        var steamPath = temp.Combine(steamRelative).Replace("\\", "\\\\", StringComparison.Ordinal);
        temp.File($"{steamRelative}/steamapps/libraryfolders.vdf",
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steamPath +
            "\"\n\t\t\"apps\"\n\t\t{\n\t\t}\n\t}\n}\n");
        temp.Pe($"{steamRelative}/steamapps/common/{folder}/{exe}");
    }
}
