using System.Text.Json;
using Microsoft.Data.Sqlite;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class SqliteLibraryIndex : IAsyncDisposable
{
    public const int SchemaVersion = 2;
    private readonly string path;
    private SqliteConnection? connection;
    public bool RebuiltCorruptDatabase { get; private set; }
    public string? QuarantinedDatabasePath { get; private set; }

    public SqliteLibraryIndex(string path)
    {
        this.path = path;
    }

    public string Path => path;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (connection is not null) return;
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Library database path has no directory.");
        Directory.CreateDirectory(directory);
        await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await RecoverInterruptedGenerationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26 && File.Exists(path))
        {
            await DisposeAsync().ConfigureAwait(false);
            QuarantineDatabase();
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await RecoverInterruptedGenerationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=3000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT schema_version FROM schema_info WHERE id=1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<long> GetActiveGenerationAsync(CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT active_generation FROM schema_info WHERE id=1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<int> GetReconciliationVersionAsync(CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT reconciliation_version FROM schema_info WHERE id=1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
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
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task CompleteGenerationAsync(
        long generation,
        IReadOnlyDictionary<string, string> fingerprints,
        IReadOnlyList<SourceGameRecord> records,
        IReadOnlyList<InstalledGame> games,
        IReadOnlyList<SourceDiagnostic> diagnostics,
        IReadOnlyDictionary<string, double> timings,
        CancellationToken cancellationToken = default)
    {
        var documents = records
            .Where(record => !string.IsNullOrWhiteSpace(record.MetadataPath))
            .Select(record => SourceDocumentEntry.FromPath(
                record.ProviderId,
                ResolveRootId(record),
                record.MetadataPath,
                1,
                generation,
                record.Attributes.GetValueOrDefault("stale") == "true"))
            .DistinctBy(document => document.DocumentId)
            .ToArray();
        var relationships = BuildRelationships(games, generation);
        return CompleteGenerationAsync(
            generation,
            fingerprints,
            records,
            games,
            diagnostics,
            timings,
            documents,
            relationships,
            [],
            GameReconciliation.RulesVersion,
            cancellationToken);
    }

    public async Task CompleteGenerationAsync(
        long generation,
        IReadOnlyDictionary<string, string> fingerprints,
        IReadOnlyList<SourceGameRecord> records,
        IReadOnlyList<InstalledGame> games,
        IReadOnlyList<SourceDiagnostic> diagnostics,
        IReadOnlyDictionary<string, double> timings,
        IReadOnlyList<SourceDocumentEntry> documents,
        IReadOnlyList<GameSourceRelationship> relationships,
        IReadOnlyList<PersistedGameAnalysis> analyses,
        int reconciliationVersion,
        CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await ReplaceProviderStateAsync(db, transaction, generation, fingerprints, cancellationToken)
                .ConfigureAwait(false);
            await ReplaceSourceRecordsAsync(db, transaction, generation, records, cancellationToken)
                .ConfigureAwait(false);
            await ReplaceSourceDocumentsAsync(db, transaction, generation, documents, cancellationToken)
                .ConfigureAwait(false);
            await ReplaceGamesAsync(db, transaction, generation, games, cancellationToken)
                .ConfigureAwait(false);
            await ReplaceRelationshipsAsync(db, transaction, generation, relationships, cancellationToken)
                .ConfigureAwait(false);
            await RemoveOrphanedAnalysesAsync(db, transaction, games, cancellationToken).ConfigureAwait(false);
            await UpsertAnalysesAsync(db, transaction, analyses, cancellationToken).ConfigureAwait(false);
            await ReplaceDiagnosticsAsync(db, transaction, generation, diagnostics, cancellationToken)
                .ConfigureAwait(false);

            await using var complete = db.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText =
                """
                UPDATE scan_generations
                SET completed_utc=$completed, status='completed', timings_json=$timings
                WHERE id=$generation;
                UPDATE schema_info
                SET active_generation=$generation, reconciliation_version=$reconciliation
                WHERE id=1;
                """;
            complete.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
            complete.Parameters.AddWithValue("$timings", JsonSerializer.Serialize(timings));
            complete.Parameters.AddWithValue("$generation", generation);
            complete.Parameters.AddWithValue("$reconciliation", reconciliationVersion);
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await MarkGenerationFailedAsync(generation, CancellationToken.None).ConfigureAwait(false);
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
            WHERE last_generation = (SELECT active_generation FROM schema_info WHERE id=1)
            ORDER BY provider_id, external_id, metadata_path;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = JsonSerializer.Deserialize<SourceGameRecord>(reader.GetString(0));
            if (record is not null) records.Add(record);
        }
        return records;
    }

    public async Task<IReadOnlyList<InstalledGame>> LoadCachedGamesAsync(CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        var games = new List<InstalledGame>();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            SELECT payload_json FROM game_installs
            WHERE active=1 AND last_generation=(SELECT active_generation FROM schema_info WHERE id=1)
            ORDER BY name, install_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var persisted = JsonSerializer.Deserialize<PersistedGameEntry>(reader.GetString(0));
            if (persisted is not null) games.Add(InstalledGame.FromPersistedGameEntry(persisted));
        }
        return games;
    }

    public async Task<IReadOnlyList<SourceDocumentEntry>> LoadSourceDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        var documents = new List<SourceDocumentEntry>();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            SELECT document_id, provider_id, root_id, canonical_path, size, mtime_utc_ticks,
                   device, inode, fast_fingerprint, parser_version, last_parse_generation, stale
            FROM source_documents
            WHERE last_generation=(SELECT active_generation FROM schema_info WHERE id=1)
            ORDER BY provider_id, root_id, canonical_path;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetString(8), reader.GetInt32(9), reader.GetInt64(10), reader.GetInt32(11) != 0));
        }
        return documents;
    }

    public async Task<IReadOnlyList<GameSourceRelationship>> LoadGameRelationshipsAsync(
        CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        var relationships = new List<GameSourceRelationship>();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            SELECT install_id, provider_id, external_id, metadata_path, source_priority,
                   confidence, evidence_json, reconciliation_generation
            FROM game_sources
            WHERE reconciliation_generation=(SELECT active_generation FROM schema_info WHERE id=1)
            ORDER BY install_id, source_priority, provider_id, external_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var evidence = JsonSerializer.Deserialize<IReadOnlyList<FieldSelectionEvidence>>(reader.GetString(6)) ?? [];
            relationships.Add(new(
                new(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), Enum.Parse<DetectionConfidence>(reader.GetString(5)), evidence, reader.GetInt64(7)));
        }
        return relationships;
    }

    public async Task<IReadOnlyDictionary<GameInstallId, PersistedGameAnalysis>> LoadAnalysisAsync(
        CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        var analyses = new Dictionary<GameInstallId, PersistedGameAnalysis>();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT payload_json FROM game_analysis ORDER BY install_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var analysis = JsonSerializer.Deserialize<PersistedGameAnalysis>(reader.GetString(0));
            if (analysis is not null) analyses[analysis.InstallId] = analysis;
        }
        return analyses;
    }

    public async Task<IReadOnlyList<SourceDiagnostic>> LoadDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        var db = RequireConnection();
        var diagnostics = new List<SourceDiagnostic>();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            SELECT provider_id, code, severity, message, detail, metadata_path, external_id
            FROM source_diagnostics
            WHERE generation=(SELECT active_generation FROM schema_info WHERE id=1)
            ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            diagnostics.Add(new(
                reader.GetString(0), reader.GetString(1), Enum.Parse<SourceDiagnosticSeverity>(reader.GetString(2)),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return diagnostics;
    }

    public async Task ImportJsonSourceIndexAsync(string jsonPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(jsonPath)) return;
        var db = RequireConnection();
        await using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT json_imported_utc FROM schema_info WHERE id=1;";
            if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string)
                return;
        }

        var document = await new JsonSourceIndexStore(jsonPath).LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document.Fingerprints.Count == 0 && document.Records.Count == 0) return;
        var generation = await BeginGenerationAsync(false, cancellationToken).ConfigureAwait(false);
        await CompleteGenerationAsync(
            generation,
            document.Fingerprints,
            document.Records,
            [],
            document.Diagnostics,
            document.TimingsMilliseconds,
            cancellationToken).ConfigureAwait(false);
        await using var mark = db.CreateCommand();
        mark.CommandText = "UPDATE schema_info SET json_imported_utc=$imported WHERE id=1 AND json_imported_utc IS NULL;";
        mark.Parameters.AddWithValue("$imported", DateTimeOffset.UtcNow.ToString("O"));
        await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task QuarantineIfCorruptAsync(CancellationToken cancellationToken = default)
    {
        var corrupt = false;
        try
        {
            var db = RequireConnection();
            await using var command = db.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            corrupt = !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            corrupt = true;
        }
        if (!corrupt) return;

        await DisposeAsync().ConfigureAwait(false);
        QuarantineDatabase();
        await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private void QuarantineDatabase()
    {
        var quarantine = path + ".corrupt." + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (File.Exists(path)) File.Move(path, quarantine, true);
        RebuiltCorruptDatabase = true;
        QuarantinedDatabasePath = quarantine;
        DeleteSidecar(path + "-wal");
        DeleteSidecar(path + "-shm");
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var db = RequireConnection();
        await using var exists = db.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_info';";
        var hasSchema = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
        if (!hasSchema)
        {
            await CreateSchemaV2Async(cancellationToken).ConfigureAwait(false);
            return;
        }

        var version = await GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false);
        if (version > SchemaVersion)
            throw new InvalidDataException(
                $"Library database schema version {version} is newer than supported version {SchemaVersion}.");
        if (version < 1)
            throw new InvalidDataException($"Library database schema version {version} is invalid.");
        if (version == 1)
            await MigrateV1ToV2Async(cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateSchemaV2Async(CancellationToken cancellationToken)
    {
        var db = RequireConnection();
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaV2Sql +
            """
            INSERT INTO schema_info(
              id, schema_version, active_generation, reconciliation_version, json_imported_utc)
            VALUES (1, 2, 0, 2, NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateV1ToV2Async(CancellationToken cancellationToken)
    {
        var db = RequireConnection();
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                ALTER TABLE schema_info ADD COLUMN reconciliation_version INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE schema_info ADD COLUMN json_imported_utc TEXT;
                CREATE TABLE source_documents (
                  document_id TEXT PRIMARY KEY,
                  provider_id TEXT NOT NULL,
                  root_id TEXT NOT NULL,
                  canonical_path TEXT NOT NULL,
                  size INTEGER NOT NULL,
                  mtime_utc_ticks INTEGER NOT NULL,
                  device INTEGER,
                  inode INTEGER,
                  fast_fingerprint TEXT NOT NULL,
                  parser_version INTEGER NOT NULL,
                  last_parse_generation INTEGER NOT NULL,
                  stale INTEGER NOT NULL,
                  last_generation INTEGER NOT NULL
                );
                CREATE TABLE game_sources (
                  install_id TEXT NOT NULL,
                  provider_id TEXT NOT NULL,
                  external_id TEXT NOT NULL,
                  metadata_path TEXT NOT NULL,
                  source_priority INTEGER NOT NULL,
                  confidence TEXT NOT NULL,
                  evidence_json TEXT NOT NULL,
                  reconciliation_generation INTEGER NOT NULL,
                  PRIMARY KEY (install_id, provider_id, external_id, metadata_path)
                );
                CREATE TABLE game_analysis (
                  install_id TEXT PRIMARY KEY,
                  analyzer_version INTEGER NOT NULL,
                  input_fingerprint TEXT NOT NULL,
                  selected_executable TEXT,
                  candidate_executables_json TEXT NOT NULL,
                  engine TEXT NOT NULL,
                  anti_cheat_json TEXT,
                  component_markers_json TEXT,
                  files_visited INTEGER NOT NULL,
                  directories_visited INTEGER NOT NULL,
                  duration_ms INTEGER NOT NULL,
                  generation INTEGER NOT NULL,
                  payload_json TEXT NOT NULL
                );
                CREATE INDEX idx_source_documents_scope ON source_documents(provider_id, root_id, canonical_path);
                CREATE INDEX idx_game_sources_record ON game_sources(provider_id, external_id, metadata_path);
                UPDATE schema_info SET schema_version=2 WHERE id=1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RecoverInterruptedGenerationsAsync(CancellationToken cancellationToken)
    {
        var db = RequireConnection();
        await using var command = db.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_generations
            SET completed_utc=$completed, status='failed'
            WHERE status='in_progress';
            """;
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceProviderStateAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long generation,
        IReadOnlyDictionary<string, string> fingerprints,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, transaction, "DELETE FROM provider_state;", cancellationToken).ConfigureAwait(false);
        foreach (var (key, fingerprint) in fingerprints.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO provider_state(provider_root_key, fingerprint, last_generation) VALUES ($key, $fingerprint, $generation);";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$generation", generation);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceSourceRecordsAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long generation,
        IReadOnlyList<SourceGameRecord> records,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, transaction, "DELETE FROM source_records;", cancellationToken).ConfigureAwait(false);
        foreach (var record in records.OrderBy(SourceRecordKey, StringComparer.Ordinal))
        {
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO source_records(
                  provider_id, external_id, name, install_root, executable_hint, prefix_hint,
                  store, launcher, platform, metadata_path, fingerprint, last_generation, payload_json)
                VALUES ($provider, $external, $name, $root, $exe, $prefix,
                  $store, $launcher, $platform, $metadata, $fingerprint, $generation, $payload);
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
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceSourceDocumentsAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long generation,
        IReadOnlyList<SourceDocumentEntry> documents,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, transaction, "DELETE FROM source_documents;", cancellationToken).ConfigureAwait(false);
        foreach (var document in documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
        {
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO source_documents(
                  document_id, provider_id, root_id, canonical_path, size, mtime_utc_ticks,
                  device, inode, fast_fingerprint, parser_version, last_parse_generation, stale, last_generation)
                VALUES ($id, $provider, $root, $path, $size, $mtime,
                  $device, $inode, $fingerprint, $parser, $parsed, $stale, $generation);
                """;
            insert.Parameters.AddWithValue("$id", document.DocumentId);
            insert.Parameters.AddWithValue("$provider", document.ProviderId);
            insert.Parameters.AddWithValue("$root", document.RootId);
            insert.Parameters.AddWithValue("$path", document.CanonicalPath);
            insert.Parameters.AddWithValue("$size", document.Size);
            insert.Parameters.AddWithValue("$mtime", document.MtimeUtcTicks);
            insert.Parameters.AddWithValue("$device", (object?)document.Device ?? DBNull.Value);
            insert.Parameters.AddWithValue("$inode", (object?)document.Inode ?? DBNull.Value);
            insert.Parameters.AddWithValue("$fingerprint", document.FastFingerprint);
            insert.Parameters.AddWithValue("$parser", document.ParserVersion);
            insert.Parameters.AddWithValue("$parsed", document.LastSuccessfulParseGeneration);
            insert.Parameters.AddWithValue("$stale", document.IsStale ? 1 : 0);
            insert.Parameters.AddWithValue("$generation", generation);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceGamesAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long generation,
        IReadOnlyList<InstalledGame> games,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, transaction, "UPDATE game_installs SET active=0;", cancellationToken).ConfigureAwait(false);
        foreach (var game in games.OrderBy(item => item.InstallId))
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
                VALUES ($id, $name, $store, $launcher, $external, $steam,
                  $root, $exe, $prefix, $deployment,
                  $platform, $environment, $confidence, $engine, $reason,
                  $actionable, $stale, $generation, 1, $payload)
                ON CONFLICT(install_id) DO UPDATE SET
                  name=excluded.name, store=excluded.store, launcher=excluded.launcher,
                  external_id=excluded.external_id, steam_app_id=excluded.steam_app_id,
                  install_root=excluded.install_root, executable=excluded.executable,
                  prefix=excluded.prefix, deployment_directory=excluded.deployment_directory,
                  platform=excluded.platform, environment=excluded.environment,
                  confidence=excluded.confidence, engine=excluded.engine,
                  selection_reason=excluded.selection_reason, actionable=excluded.actionable,
                  stale=excluded.stale, last_generation=excluded.last_generation,
                  active=1, payload_json=excluded.payload_json;
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
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(PersistedGameEntry.FromInstalledGame(game)));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceRelationshipsAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long generation,
        IReadOnlyList<GameSourceRelationship> relationships,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, transaction, "DELETE FROM game_sources;", cancellationToken).ConfigureAwait(false);
        foreach (var relationship in relationships.OrderBy(item => item.InstallId).ThenBy(item => item.SourcePriority))
        {
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO game_sources(
                  install_id, provider_id, external_id, metadata_path, source_priority,
                  confidence, evidence_json, reconciliation_generation)
                VALUES ($install, $provider, $external, $metadata, $priority,
                  $confidence, $evidence, $generation);
                """;
            insert.Parameters.AddWithValue("$install", relationship.InstallId.Value);
            insert.Parameters.AddWithValue("$provider", relationship.ProviderId);
            insert.Parameters.AddWithValue("$external", relationship.ExternalId);
            insert.Parameters.AddWithValue("$metadata", relationship.MetadataPath);
            insert.Parameters.AddWithValue("$priority", relationship.SourcePriority);
            insert.Parameters.AddWithValue("$confidence", relationship.Confidence.ToString());
            insert.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(relationship.SelectedFieldEvidence));
            insert.Parameters.AddWithValue("$generation", generation);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertAnalysesAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        IReadOnlyList<PersistedGameAnalysis> analyses,
        CancellationToken cancellationToken)
    {
        foreach (var analysis in analyses.OrderBy(item => item.InstallId))
        {
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO game_analysis(
                  install_id, analyzer_version, input_fingerprint, selected_executable,
                  candidate_executables_json, engine, anti_cheat_json, component_markers_json,
                  files_visited, directories_visited, duration_ms, generation, payload_json)
                VALUES ($install, $version, $fingerprint, $executable,
                  $candidates, $engine, $anticheat, $components,
                  $files, $directories, $duration, $generation, $payload)
                ON CONFLICT(install_id) DO UPDATE SET
                  analyzer_version=excluded.analyzer_version,
                  input_fingerprint=excluded.input_fingerprint,
                  selected_executable=excluded.selected_executable,
                  candidate_executables_json=excluded.candidate_executables_json,
                  engine=excluded.engine,
                  anti_cheat_json=excluded.anti_cheat_json,
                  component_markers_json=excluded.component_markers_json,
                  files_visited=excluded.files_visited,
                  directories_visited=excluded.directories_visited,
                  duration_ms=excluded.duration_ms,
                  generation=excluded.generation,
                  payload_json=excluded.payload_json;
                """;
            insert.Parameters.AddWithValue("$install", analysis.InstallId.Value);
            insert.Parameters.AddWithValue("$version", analysis.AnalyzerVersion);
            insert.Parameters.AddWithValue("$fingerprint", analysis.InputFingerprint);
            insert.Parameters.AddWithValue("$executable", (object?)analysis.SelectedExecutable ?? DBNull.Value);
            insert.Parameters.AddWithValue("$candidates", JsonSerializer.Serialize(analysis.CandidateExecutables));
            insert.Parameters.AddWithValue("$engine", analysis.Engine.ToString());
            insert.Parameters.AddWithValue("$anticheat", analysis.Fingerprint is null
                ? DBNull.Value : JsonSerializer.Serialize(analysis.Fingerprint.AntiCheat));
            insert.Parameters.AddWithValue("$components", analysis.Fingerprint is null
                ? DBNull.Value : JsonSerializer.Serialize(analysis.Fingerprint.Components));
            insert.Parameters.AddWithValue("$files", analysis.FilesVisited);
            insert.Parameters.AddWithValue("$directories", analysis.DirectoriesVisited);
            insert.Parameters.AddWithValue("$duration", analysis.DurationMilliseconds);
            insert.Parameters.AddWithValue("$generation", analysis.Generation);
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(analysis));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RemoveOrphanedAnalysesAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        IReadOnlyList<InstalledGame> games,
        CancellationToken cancellationToken)
    {
        await using var delete = db.CreateCommand();
        delete.Transaction = transaction;
        if (games.Count == 0)
        {
            delete.CommandText = "DELETE FROM game_analysis;";
        }
        else
        {
            var parameters = games.Select((game, index) => new
            {
                Name = $"$install{index}",
                Value = game.InstallId.Value
            }).ToArray();
            delete.CommandText = $"DELETE FROM game_analysis WHERE install_id NOT IN ({string.Join(",", parameters.Select(item => item.Name))});";
            foreach (var parameter in parameters)
                delete.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceDiagnosticsAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        long generation,
        IReadOnlyList<SourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(db, transaction, "DELETE FROM source_diagnostics;", cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in diagnostics.Take(500))
        {
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO source_diagnostics(
                  provider_id, code, severity, message, detail, metadata_path, external_id, generation)
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
    }

    private static async Task ExecuteAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<GameSourceRelationship> BuildRelationships(
        IReadOnlyList<InstalledGame> games,
        long generation)
    {
        var relationships = new List<GameSourceRelationship>();
        foreach (var game in games)
        {
            for (var index = 0; index < game.Sources.Count; index++)
            {
                var source = game.Sources[index];
                relationships.Add(new(
                    game.InstallId,
                    source.ProviderId,
                    source.ExternalId,
                    source.MetadataPath,
                    index,
                    game.Confidence,
                    game.FieldSelections ?? [],
                    generation));
            }
        }
        return relationships;
    }

    private static string ResolveRootId(SourceGameRecord record)
    {
        var metadata = GameIdentity.NormalizePath(record.MetadataPath);
        var root = GameIdentity.NormalizePath(record.InstallRoot ?? string.Empty);
        if (root.Length > 0 && metadata.StartsWith(root, StringComparison.Ordinal)) return root;
        return System.IO.Path.GetDirectoryName(metadata) ?? metadata;
    }

    private static string SourceRecordKey(SourceGameRecord record) =>
        $"{record.ProviderId}|{record.ExternalId}|{GameIdentity.NormalizePath(record.MetadataPath)}";

    private static void DeleteSidecar(string sidecar)
    {
        try
        {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SqliteConnection RequireConnection() =>
        connection ?? throw new InvalidOperationException("Library database is not open.");

    public async ValueTask DisposeAsync()
    {
        if (connection is null) return;
        await connection.DisposeAsync().ConfigureAwait(false);
        connection = null;
    }

    private const string SchemaV2Sql =
        """
        CREATE TABLE schema_info (
          id INTEGER PRIMARY KEY CHECK (id = 1),
          schema_version INTEGER NOT NULL,
          active_generation INTEGER NOT NULL DEFAULT 0,
          reconciliation_version INTEGER NOT NULL,
          json_imported_utc TEXT
        );
        CREATE TABLE scan_generations (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          started_utc TEXT NOT NULL,
          completed_utc TEXT,
          status TEXT NOT NULL,
          force_scan INTEGER NOT NULL DEFAULT 0,
          timings_json TEXT
        );
        CREATE TABLE provider_state (
          provider_root_key TEXT PRIMARY KEY,
          fingerprint TEXT NOT NULL,
          last_generation INTEGER NOT NULL
        );
        CREATE TABLE source_records (
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
        CREATE TABLE source_documents (
          document_id TEXT PRIMARY KEY,
          provider_id TEXT NOT NULL,
          root_id TEXT NOT NULL,
          canonical_path TEXT NOT NULL,
          size INTEGER NOT NULL,
          mtime_utc_ticks INTEGER NOT NULL,
          device INTEGER,
          inode INTEGER,
          fast_fingerprint TEXT NOT NULL,
          parser_version INTEGER NOT NULL,
          last_parse_generation INTEGER NOT NULL,
          stale INTEGER NOT NULL,
          last_generation INTEGER NOT NULL
        );
        CREATE TABLE game_installs (
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
        CREATE TABLE game_sources (
          install_id TEXT NOT NULL,
          provider_id TEXT NOT NULL,
          external_id TEXT NOT NULL,
          metadata_path TEXT NOT NULL,
          source_priority INTEGER NOT NULL,
          confidence TEXT NOT NULL,
          evidence_json TEXT NOT NULL,
          reconciliation_generation INTEGER NOT NULL,
          PRIMARY KEY (install_id, provider_id, external_id, metadata_path)
        );
        CREATE TABLE game_analysis (
          install_id TEXT PRIMARY KEY,
          analyzer_version INTEGER NOT NULL,
          input_fingerprint TEXT NOT NULL,
          selected_executable TEXT,
          candidate_executables_json TEXT NOT NULL,
          engine TEXT NOT NULL,
          anti_cheat_json TEXT,
          component_markers_json TEXT,
          files_visited INTEGER NOT NULL,
          directories_visited INTEGER NOT NULL,
          duration_ms INTEGER NOT NULL,
          generation INTEGER NOT NULL,
          payload_json TEXT NOT NULL
        );
        CREATE TABLE source_diagnostics (
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
        CREATE INDEX idx_source_records_generation ON source_records(last_generation);
        CREATE INDEX idx_source_documents_scope ON source_documents(provider_id, root_id, canonical_path);
        CREATE INDEX idx_game_installs_generation ON game_installs(last_generation);
        CREATE INDEX idx_game_sources_record ON game_sources(provider_id, external_id, metadata_path);
        """;
}
