using Microsoft.Data.Sqlite;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class SqliteLibraryIndex : IAsyncDisposable
{
    public const int SchemaVersion = 1;
    private readonly string path;
    private SqliteConnection? connection;

    public SqliteLibraryIndex(string path)
    {
        this.path = path;
    }

    public string Path => path;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Library database path has no directory.");
        Directory.CreateDirectory(directory);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=3000; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> BeginGenerationAsync(bool forceScan, CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            INSERT INTO scan_generations(started_utc, status, force_scan)
            VALUES ($started, 'in_progress', $force);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$force", forceScan ? 1 : 0);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task CompleteGenerationAsync(
        long generation,
        IReadOnlyDictionary<string, string> fingerprints,
        IReadOnlyList<SourceGameRecord> records,
        IReadOnlyList<InstalledGame> games,
        IReadOnlyList<SourceDiagnostic> diagnostics,
        IReadOnlyDictionary<string, double> timings,
        CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            foreach (var (key, fingerprint) in fingerprints)
            {
                await using var upsert = db.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText =
                    """
                    INSERT INTO provider_state(provider_root_key, fingerprint, last_generation)
                    VALUES ($key, $fingerprint, $generation)
                    ON CONFLICT(provider_root_key) DO UPDATE SET
                      fingerprint=excluded.fingerprint,
                      last_generation=excluded.last_generation;
                    """;
                upsert.Parameters.AddWithValue("$key", key);
                upsert.Parameters.AddWithValue("$fingerprint", fingerprint);
                upsert.Parameters.AddWithValue("$generation", generation);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var clearRecords = db.CreateCommand())
            {
                clearRecords.Transaction = transaction;
                clearRecords.CommandText = "DELETE FROM source_records WHERE last_generation < $generation;";
                clearRecords.Parameters.AddWithValue("$generation", generation);
                await clearRecords.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var record in records)
            {
                await using var insert = db.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO source_records(
                      provider_id, external_id, name, install_root, executable_hint, prefix_hint,
                      store, launcher, platform, metadata_path, fingerprint, last_generation, payload_json)
                    VALUES (
                      $provider, $external, $name, $root, $exe, $prefix,
                      $store, $launcher, $platform, $metadata, $fingerprint, $generation, $payload)
                    ON CONFLICT(provider_id, external_id, metadata_path) DO UPDATE SET
                      name=excluded.name,
                      install_root=excluded.install_root,
                      executable_hint=excluded.executable_hint,
                      prefix_hint=excluded.prefix_hint,
                      store=excluded.store,
                      launcher=excluded.launcher,
                      platform=excluded.platform,
                      fingerprint=excluded.fingerprint,
                      last_generation=excluded.last_generation,
                      payload_json=excluded.payload_json;
                    """;
                insert.Parameters.AddWithValue("$provider", record.ProviderId);
                insert.Parameters.AddWithValue("$external", record.ExternalId);
                insert.Parameters.AddWithValue("$name", record.Name);
                insert.Parameters.AddWithValue("$root", (object?)record.InstallRoot ?? DBNull.Value);
                insert.Parameters.AddWithValue("$exe", (object?)record.ExecutableHint ?? DBNull.Value);
                insert.Parameters.AddWithValue("$prefix", (object?)record.PrefixHint ?? DBNull.Value);
                insert.Parameters.AddWithValue("$store", record.Store.ToString());
                insert.Parameters.AddWithValue("$launcher", record.Launcher.ToString());
                insert.Parameters.AddWithValue("$platform", record.Platform.ToString());
                insert.Parameters.AddWithValue("$metadata", record.MetadataPath);
                insert.Parameters.AddWithValue("$fingerprint", record.SourceFingerprint ?? string.Empty);
                insert.Parameters.AddWithValue("$generation", generation);
                insert.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(record));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var clearGames = db.CreateCommand())
            {
                clearGames.Transaction = transaction;
                clearGames.CommandText = "UPDATE game_installs SET active=0 WHERE last_generation < $generation;";
                clearGames.Parameters.AddWithValue("$generation", generation);
                await clearGames.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var game in games)
            {
                await using var insert = db.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO game_installs(
                      install_id, name, store, launcher, external_id, steam_app_id,
                      install_root, executable, prefix, deployment_directory,
                      platform, environment, confidence, engine, selection_reason,
                      actionable, stale, last_generation, active, payload_json)
                    VALUES (
                      $id, $name, $store, $launcher, $external, $steam,
                      $root, $exe, $prefix, $deployment,
                      $platform, $environment, $confidence, $engine, $reason,
                      $actionable, $stale, $generation, 1, $payload)
                    ON CONFLICT(install_id) DO UPDATE SET
                      name=excluded.name,
                      store=excluded.store,
                      launcher=excluded.launcher,
                      external_id=excluded.external_id,
                      steam_app_id=excluded.steam_app_id,
                      install_root=excluded.install_root,
                      executable=excluded.executable,
                      prefix=excluded.prefix,
                      deployment_directory=excluded.deployment_directory,
                      platform=excluded.platform,
                      environment=excluded.environment,
                      confidence=excluded.confidence,
                      engine=excluded.engine,
                      selection_reason=excluded.selection_reason,
                      actionable=excluded.actionable,
                      stale=excluded.stale,
                      last_generation=excluded.last_generation,
                      active=1,
                      payload_json=excluded.payload_json;
                    """;
                insert.Parameters.AddWithValue("$id", game.InstallId.Value);
                insert.Parameters.AddWithValue("$name", game.Name);
                insert.Parameters.AddWithValue("$store", game.Store.ToString());
                insert.Parameters.AddWithValue("$launcher", game.PrimaryLauncher.ToString());
                insert.Parameters.AddWithValue("$external", (object?)game.ExternalId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$steam", (object?)game.SteamAppId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$root", game.CanonicalInstallRoot);
                insert.Parameters.AddWithValue("$exe", (object?)game.Executable ?? DBNull.Value);
                insert.Parameters.AddWithValue("$prefix", (object?)game.Prefix ?? DBNull.Value);
                insert.Parameters.AddWithValue("$deployment", (object?)game.DeploymentDirectory ?? DBNull.Value);
                insert.Parameters.AddWithValue("$platform", game.Platform.ToString());
                insert.Parameters.AddWithValue("$environment", game.Environment.ToString());
                insert.Parameters.AddWithValue("$confidence", game.Confidence.ToString());
                insert.Parameters.AddWithValue("$engine", game.Engine.ToString());
                insert.Parameters.AddWithValue("$reason", game.SelectionReason);
                insert.Parameters.AddWithValue("$actionable", game.IsActionable ? 1 : 0);
                insert.Parameters.AddWithValue("$stale", game.IsStale ? 1 : 0);
                insert.Parameters.AddWithValue("$generation", generation);
                insert.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(PersistedGameEntry.FromInstalledGame(game)));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var diagnostic in diagnostics.Take(500))
            {
                await using var insert = db.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO source_diagnostics(provider_id, code, severity, message, detail, metadata_path, external_id, generation)
                    VALUES ($provider, $code, $severity, $message, $detail, $metadata, $external, $generation);
                    """;
                insert.Parameters.AddWithValue("$provider", diagnostic.ProviderId);
                insert.Parameters.AddWithValue("$code", diagnostic.Code);
                insert.Parameters.AddWithValue("$severity", diagnostic.Severity.ToString());
                insert.Parameters.AddWithValue("$message", diagnostic.Message);
                insert.Parameters.AddWithValue("$detail", (object?)diagnostic.TechnicalDetail ?? DBNull.Value);
                insert.Parameters.AddWithValue("$metadata", (object?)diagnostic.MetadataPath ?? DBNull.Value);
                insert.Parameters.AddWithValue("$external", (object?)diagnostic.ExternalId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$generation", generation);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var complete = db.CreateCommand())
            {
                complete.Transaction = transaction;
                complete.CommandText =
                    """
                    UPDATE scan_generations
                    SET completed_utc=$completed, status='completed', timings_json=$timings
                    WHERE id=$generation;
                    UPDATE schema_info SET active_generation=$generation WHERE id=1;
                    """;
                complete.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
                complete.Parameters.AddWithValue("$timings", System.Text.Json.JsonSerializer.Serialize(timings));
                complete.Parameters.AddWithValue("$generation", generation);
                await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await MarkGenerationFailedAsync(generation, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkGenerationFailedAsync(long generation, CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_generations
            SET completed_utc=$completed, status='failed'
            WHERE id=$generation AND status='in_progress';
            """;
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$generation", generation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> LoadFingerprintsAsync(CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT provider_root_key, fingerprint FROM provider_state;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public async Task<IReadOnlyList<SourceGameRecord>> LoadCachedRecordsAsync(CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        var records = new List<SourceGameRecord>();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            SELECT payload_json FROM source_records
            WHERE last_generation = (SELECT active_generation FROM schema_info WHERE id=1);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var record = System.Text.Json.JsonSerializer.Deserialize<SourceGameRecord>(json);
            if (record is not null) records.Add(record);
        }

        return records;
    }

    public async Task ImportJsonSourceIndexAsync(string jsonPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(jsonPath)) return;
        var importedMarker = jsonPath + ".imported";
        if (File.Exists(importedMarker)) return;

        var store = new JsonSourceIndexStore(jsonPath);
        var document = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document.Fingerprints.Count == 0 && document.Records.Count == 0)
            return;

        var generation = await BeginGenerationAsync(false, cancellationToken).ConfigureAwait(false);
        await CompleteGenerationAsync(
            generation,
            document.Fingerprints,
            document.Records,
            [],
            document.Diagnostics,
            document.TimingsMilliseconds,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(importedMarker, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task QuarantineIfCorruptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var db = RequireConnection();
            await using var command = db.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch (SqliteException)
        {
        }

        await DisposeAsync().ConfigureAwait(false);
        var quarantine = path + ".corrupt." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (File.Exists(path))
            File.Move(path, quarantine, true);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var db = RequireConnection();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_info (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              schema_version INTEGER NOT NULL,
              active_generation INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS scan_generations (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              started_utc TEXT NOT NULL,
              completed_utc TEXT,
              status TEXT NOT NULL,
              force_scan INTEGER NOT NULL DEFAULT 0,
              timings_json TEXT
            );
            CREATE TABLE IF NOT EXISTS provider_state (
              provider_root_key TEXT PRIMARY KEY,
              fingerprint TEXT NOT NULL,
              last_generation INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS source_records (
              provider_id TEXT NOT NULL,
              external_id TEXT NOT NULL,
              name TEXT NOT NULL,
              install_root TEXT,
              executable_hint TEXT,
              prefix_hint TEXT,
              store TEXT NOT NULL,
              launcher TEXT NOT NULL,
              platform TEXT NOT NULL,
              metadata_path TEXT NOT NULL,
              fingerprint TEXT NOT NULL,
              last_generation INTEGER NOT NULL,
              payload_json TEXT NOT NULL,
              PRIMARY KEY (provider_id, external_id, metadata_path)
            );
            CREATE TABLE IF NOT EXISTS game_installs (
              install_id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              store TEXT NOT NULL,
              launcher TEXT NOT NULL,
              external_id TEXT,
              steam_app_id INTEGER,
              install_root TEXT NOT NULL,
              executable TEXT,
              prefix TEXT,
              deployment_directory TEXT,
              platform TEXT NOT NULL,
              environment TEXT NOT NULL,
              confidence TEXT NOT NULL,
              engine TEXT NOT NULL,
              selection_reason TEXT NOT NULL,
              actionable INTEGER NOT NULL,
              stale INTEGER NOT NULL,
              last_generation INTEGER NOT NULL,
              active INTEGER NOT NULL,
              payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS source_diagnostics (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              provider_id TEXT NOT NULL,
              code TEXT NOT NULL,
              severity TEXT NOT NULL,
              message TEXT NOT NULL,
              detail TEXT,
              metadata_path TEXT,
              external_id TEXT,
              generation INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_source_records_generation ON source_records(last_generation);
            CREATE INDEX IF NOT EXISTS idx_game_installs_generation ON game_installs(last_generation);
            INSERT OR IGNORE INTO schema_info(id, schema_version, active_generation)
            VALUES (1, $version, 0);
            """;
        command.Parameters.AddWithValue("$version", SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection RequireConnection() =>
        connection ?? throw new InvalidOperationException("Library database is not open.");

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            connection = null;
        }
    }
}
