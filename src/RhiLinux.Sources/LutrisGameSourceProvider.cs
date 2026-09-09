using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class LutrisGameSourceProvider : IGameSourceProvider
{
    public const string ProviderId = "lutris";
    public string Id => ProviderId;
    public string DisplayName => "Lutris";

    public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(string Path, SourceRootKind Kind)>
        {
            (Path.Combine(context.DataHome, "lutris"), SourceRootKind.Xdg),
            (Path.Combine(context.HomeDirectory, ".local", "share", "lutris"), SourceRootKind.Native),
            (SourceRootDiscovery.FlatpakData(context.HomeDirectory, "net.lutris.Lutris", "lutris"), SourceRootKind.Flatpak)
        };

        var roots = SourceRootDiscovery.ResolveCandidates(ProviderId, candidates, context.RootsForProvider(ProviderId), context.HomeDirectory).ToList();
        foreach (var root in roots.Where(item => item.Exists && item.Readable && !item.Deduplicated).ToArray())
        {
            var configRoot = ResolveConfigRoot(context, root);
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["configRoot"] = configRoot,
                ["gamesYamlRoot"] = ResolveGamesYamlRoot(configRoot, root.CanonicalPath)
            };
            var pgaPath = ResolvePgaPath(configRoot, root.CanonicalPath);
            attributes["pgaPath"] = pgaPath;
            var index = roots.IndexOf(root);
            roots[index] = root with { Attributes = attributes };
        }

        return Task.FromResult<IReadOnlyList<GameSourceRoot>>(roots);
    }

    public async Task<GameSourceScanResult> ScanAsync(
        GameSourceRoot root,
        GameSourceScanContext context,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        if (!root.Exists || !root.Readable || root.Deduplicated)
        {
            var code = !root.Exists
                ? SourceDiagnosticCodes.SourceRootMissing
                : SourceDiagnosticCodes.SourceRootUnreadable;
            return SourceRecordFactory.EmptyScan(
                ProviderId,
                root,
                [new(ProviderId, code, SourceDiagnosticSeverity.Info, root.SkipReason ?? "Source root skipped.", root.CanonicalPath)],
                started.Elapsed);
        }

        var pgaPath = root.Attributes?.GetValueOrDefault("pgaPath") ?? Path.Combine(root.CanonicalPath, "pga.db");
        var gamesYamlRoot = root.Attributes?.GetValueOrDefault("gamesYamlRoot") ??
                            Path.Combine(root.CanonicalPath, "games");
        var metadataFiles = new List<string>();
        if (File.Exists(pgaPath)) metadataFiles.Add(pgaPath);
        var walPath = pgaPath + "-wal";
        if (File.Exists(walPath)) metadataFiles.Add(walPath);

        var fingerprint = SourceRootDiscovery.SourceFingerprint(pgaPath, walPath, gamesYamlRoot) + ":v1";
        if (!context.ForceFullScan && !context.IsTargeted &&
            context.PreviousFingerprints.TryGetValue(FingerprintKey(root), out var previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return SourceRecordFactory.CachedScan(ProviderId, root, fingerprint, started.Elapsed, metadataFiles);
        }

        var diagnostics = new List<SourceDiagnostic>();
        var games = new List<SourceGameRecord>();
        var malformed = new List<SourceGameRecord>();
        var skipped = new List<SourceGameRecord>();

        if (!File.Exists(pgaPath))
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMissing,
                SourceDiagnosticSeverity.Info,
                "Lutris pga.db was not found.",
                pgaPath,
                pgaPath));
            return new GameSourceScanResult(
                ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
                fingerprint, started.Elapsed, false, true);
        }

        try
        {
            var rows = await ReadInstalledGamesAsync(pgaPath, cancellationToken).ConfigureAwait(false);
            var targetedYaml = context.Documents?
                .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
                .Select(GameIdentity.NormalizePath)
                .ToHashSet(StringComparer.Ordinal);
            if (targetedYaml is { Count: > 0 } && !context.IncludesDocument(pgaPath))
                rows = rows.Where(row => MatchesTargetYaml(row, gamesYamlRoot, targetedYaml)).ToArray();
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = BuildRecord(row, gamesYamlRoot, pgaPath, diagnostics, malformed, skipped);
                if (record is not null)
                {
                    games.Add(record);
                    if (record.ConfigurationPath is { } configurationPath && File.Exists(configurationPath))
                        metadataFiles.Add(configurationPath);
                }
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceDatabaseLocked,
                SourceDiagnosticSeverity.Warning,
                "Lutris database is locked.",
                exception.Message,
                pgaPath));
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceDatabaseReadFailed,
                SourceDiagnosticSeverity.Warning,
                "Failed to read Lutris database.",
                exception.Message,
                pgaPath));
        }

        return new GameSourceScanResult(
            ProviderId, root, games, diagnostics, metadataFiles, malformed, skipped,
            fingerprint, started.Elapsed, false, true);
    }

    public static string FingerprintKey(GameSourceRoot root) => $"{ProviderId}:{root.CanonicalPath}";

    private static string ResolveConfigRoot(GameSourceDiscoveryContext context, GameSourceRoot dataRoot)
    {
        if (dataRoot.Kind == SourceRootKind.Flatpak)
            return SourceRootDiscovery.FlatpakConfig(context.HomeDirectory, "net.lutris.Lutris", "lutris");
        if (dataRoot.Kind == SourceRootKind.Custom)
            return dataRoot.CanonicalPath;
        var xdg = Path.Combine(context.ConfigHome, "lutris");
        if (Directory.Exists(xdg)) return GameIdentity.NormalizePath(xdg);
        var native = Path.Combine(context.HomeDirectory, ".config", "lutris");
        if (Directory.Exists(native)) return GameIdentity.NormalizePath(native);
        return dataRoot.CanonicalPath;
    }

    private static string ResolveGamesYamlRoot(string configRoot, string dataRoot)
    {
        var configGames = Path.Combine(configRoot, "games");
        if (Directory.Exists(configGames)) return configGames;
        var dataGames = Path.Combine(dataRoot, "games");
        return dataGames;
    }

    private static string ResolvePgaPath(string configRoot, string dataRoot)
    {
        var confPath = Path.Combine(configRoot, "lutris.conf");
        if (File.Exists(confPath))
        {
            try
            {
                foreach (var line in File.ReadLines(confPath))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("pga_path", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var value = parts[1].Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        var expanded = GameIdentity.NormalizePath(Environment.ExpandEnvironmentVariables(value));
                        if (File.Exists(expanded)) return expanded;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var dataDb = Path.Combine(dataRoot, "pga.db");
        if (File.Exists(dataDb)) return dataDb;
        return Path.Combine(configRoot, "pga.db");
    }

    private static async Task<IReadOnlyList<LutrisRow>> ReadInstalledGamesAsync(
        string pgaPath,
        CancellationToken cancellationToken)
    {
        SqliteException? lastBusy = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ReadInstalledGamesCoreAsync(pgaPath, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                lastBusy = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var snapshot = await CreateConsistentSnapshotAsync(pgaPath, cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadInstalledGamesCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(snapshot); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> CreateConsistentSnapshotAsync(string pgaPath, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), "rhi-linux-lutris-" + Guid.NewGuid().ToString("N") + ".db");
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = pgaPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        await using var source = new SqliteConnection(sourceBuilder.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var busy = source.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=2000;";
            await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temp,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        return temp;
    }

    private static async Task<IReadOnlyList<LutrisRow>> ReadInstalledGamesCoreAsync(
        string pgaPath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = pgaPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout=1000;";
            await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var columns = await LoadColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("id") || !columns.Contains("name"))
            throw new InvalidOperationException("Lutris games table schema is unsupported.");

        var select = BuildSelect(columns);
        await using var command = connection.CreateCommand();
        command.CommandText = select;
        var rows = new List<LutrisRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new LutrisRow(
                GetInt(reader, columns, "id"),
                GetString(reader, columns, "name") ?? $"lutris-{GetInt(reader, columns, "id")}",
                GetString(reader, columns, "slug"),
                GetString(reader, columns, "platform"),
                GetString(reader, columns, "runner"),
                GetString(reader, columns, "executable"),
                GetString(reader, columns, "directory"),
                GetInt(reader, columns, "installed") != 0,
                GetString(reader, columns, "configpath"),
                GetString(reader, columns, "service"),
                GetString(reader, columns, "service_id")));
        }

        return rows;
    }

    private static async Task<HashSet<string>> LoadColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(games);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static string BuildSelect(HashSet<string> columns)
    {
        string[] wanted =
        [
            "id", "name", "slug", "platform", "runner", "executable", "directory",
            "installed", "configpath", "service", "service_id"
        ];
        var selected = wanted.Where(columns.Contains).Select(name => $"\"{name}\"").ToArray();
        var where = columns.Contains("installed") ? " WHERE installed = 1" : string.Empty;
        return $"SELECT {string.Join(", ", selected)} FROM games{where};";
    }

    private static int GetInt(SqliteDataReader reader, HashSet<string> columns, string name)
    {
        if (!columns.Contains(name)) return 0;
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return 0;
        return Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static string? GetString(SqliteDataReader reader, HashSet<string> columns, string name)
    {
        if (!columns.Contains(name)) return null;
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal)) return null;
        return reader.GetValue(ordinal)?.ToString();
    }

    private static SourceGameRecord? BuildRecord(
        LutrisRow row,
        string gamesYamlRoot,
        string pgaPath,
        List<SourceDiagnostic> diagnostics,
        List<SourceGameRecord> malformed,
        List<SourceGameRecord> skipped)
    {
        if (!row.Installed) return null;

        string? executable = row.Executable;
        string? prefix = null;
        string? workingDir = null;
        string? yamlPath = null;
        if (!string.IsNullOrWhiteSpace(row.ConfigPath))
        {
            yamlPath = row.ConfigPath!.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                       row.ConfigPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                ? Path.IsPathRooted(row.ConfigPath)
                    ? row.ConfigPath
                    : Path.Combine(gamesYamlRoot, row.ConfigPath)
                : Path.Combine(gamesYamlRoot, row.ConfigPath + ".yml");

            if (File.Exists(yamlPath))
            {
                try
                {
                    var text = File.ReadAllText(yamlPath);
                    if (MinimalYaml.TryParse(text, out var mapping, out var error) && mapping is not null)
                    {
                        executable = MinimalYaml.GetString(mapping, "game", "exe") ?? executable;
                        workingDir = MinimalYaml.GetString(mapping, "game", "working_dir");
                        prefix = MinimalYaml.GetString(mapping, "game", "prefix") ??
                                 MinimalYaml.GetString(mapping, "wine", "prefix");
                    }
                    else
                    {
                        diagnostics.Add(new(
                            ProviderId,
                            SourceDiagnosticCodes.SourceMetadataMalformed,
                            SourceDiagnosticSeverity.Warning,
                            $"Lutris YAML for '{row.Name}' could not be parsed.",
                            error,
                            yamlPath,
                            row.Id.ToString()));
                        malformed.Add(Incomplete(row, pgaPath, yamlPath));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new(
                        ProviderId,
                        SourceDiagnosticCodes.SourceMetadataMalformed,
                        SourceDiagnosticSeverity.Warning,
                        $"Lutris YAML for '{row.Name}' could not be read.",
                        exception.Message,
                        yamlPath,
                        row.Id.ToString()));
                }
            }
            else
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceMetadataMissing,
                    SourceDiagnosticSeverity.Info,
                    $"Lutris YAML for '{row.Name}' was not found.",
                    yamlPath,
                    yamlPath,
                    row.Id.ToString()));
            }
        }

        var store = MapService(row.Service);
        var runner = row.Runner?.Trim().ToLowerInvariant() ?? string.Empty;
        var platform = SourceRecordFactory.ParsePlatform(row.Platform);
        if (platform == GameBinaryPlatform.Unknown)
        {
            platform = runner is "wine" or "proton" or "umu"
                ? GameBinaryPlatform.Windows
                : runner is "linux" or "native"
                    ? GameBinaryPlatform.Linux
                    : GameBinaryPlatform.Unknown;
        }

        var environment = runner switch
        {
            "wine" => CompatibilityEnvironment.Wine,
            "proton" => CompatibilityEnvironment.Proton,
            "umu" => CompatibilityEnvironment.Umu,
            "linux" or "native" => CompatibilityEnvironment.Native,
            _ => CompatibilityEnvironment.Unknown
        };

        var actionable = platform == GameBinaryPlatform.Windows && runner is "wine" or "proton" or "umu" or "";
        string? unsupported = null;
        var recordDiagnostics = new List<SourceDiagnostic>();
        if (!actionable)
        {
            unsupported = runner switch
            {
                "linux" or "native" => "Native Linux Lutris runner is not a Windows mod target.",
                "browser" or "dosbox" or "scummvm" or "emulator" => $"Lutris runner '{runner}' is unsupported for Windows mods.",
                _ when platform == GameBinaryPlatform.Linux => "Native Linux installation is not a Windows mod target.",
                _ => $"Lutris runner '{runner}' is not actionable for the Windows component stack."
            };
            recordDiagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.UnsupportedRunner,
                SourceDiagnosticSeverity.Info,
                unsupported,
                runner,
                yamlPath ?? pgaPath,
                row.ServiceId ?? row.Id.ToString()));
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(row.Slug)) attributes["slug"] = row.Slug!;
        if (!string.IsNullOrWhiteSpace(row.Service)) attributes["service"] = row.Service!;
        if (!string.IsNullOrWhiteSpace(row.Runner)) attributes["runner"] = row.Runner!;

        var externalId = row.ServiceId ?? row.Slug ?? row.Id.ToString();
        return SourceRecordFactory.Create(
            ProviderId,
            store,
            GameLauncher.Lutris,
            externalId,
            row.Name,
            row.Directory,
            executable,
            prefix,
            workingDir,
            platform == GameBinaryPlatform.Unknown && actionable ? GameBinaryPlatform.Windows : platform,
            environment,
            pgaPath,
            yamlPath,
            SourceRootDiscovery.GetMetadataTimestamp(yamlPath ?? pgaPath),
            attributes,
            recordDiagnostics,
            isActionable: actionable,
            unsupportedReason: unsupported);
    }

    private static bool MatchesTargetYaml(
        LutrisRow row,
        string gamesYamlRoot,
        IReadOnlySet<string> targetedYaml)
    {
        if (string.IsNullOrWhiteSpace(row.ConfigPath)) return false;
        var candidate = row.ConfigPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                        row.ConfigPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            ? Path.IsPathRooted(row.ConfigPath)
                ? row.ConfigPath
                : Path.Combine(gamesYamlRoot, row.ConfigPath)
            : Path.Combine(gamesYamlRoot, row.ConfigPath + ".yml");
        return targetedYaml.Contains(GameIdentity.NormalizePath(candidate));
    }

    private static GameStore MapService(string? service) =>
        service?.Trim().ToLowerInvariant() switch
        {
            "gog" => GameStore.Gog,
            "egs" or "epic" => GameStore.Epic,
            "amazon" => GameStore.Amazon,
            "steam" => GameStore.Steam,
            "itchio" or "itch" => GameStore.Other,
            _ => GameStore.Unknown
        };

    private static SourceGameRecord Incomplete(LutrisRow row, string pgaPath, string? yamlPath) =>
        new(
            ProviderId,
            GameStore.Unknown,
            GameLauncher.Lutris,
            row.Id.ToString(),
            row.Name,
            row.Directory,
            row.Executable,
            null,
            null,
            GameBinaryPlatform.Unknown,
            CompatibilityEnvironment.Unknown,
            pgaPath,
            yamlPath,
            SourceRootDiscovery.GetMetadataTimestamp(yamlPath ?? pgaPath),
            new Dictionary<string, string>(),
            [],
            IsActionable: false,
            UnsupportedReason: "Malformed Lutris YAML.");

    private sealed record LutrisRow(
        int Id,
        string Name,
        string? Slug,
        string? Platform,
        string? Runner,
        string? Executable,
        string? Directory,
        bool Installed,
        string? ConfigPath,
        string? Service,
        string? ServiceId);
}
