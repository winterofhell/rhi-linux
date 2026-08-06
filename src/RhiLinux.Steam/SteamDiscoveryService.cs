using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed record ScanOptions(
    bool UseLibraryIndex = true,
    bool ForceFullAnalysis = false,
    int AnalysisConcurrency = 0,
    ILibraryIndexStore? LibraryIndexStore = null);

public sealed record IncrementalScanMetrics(
    int ManifestsSeen,
    int CacheHits,
    int Analyzed,
    int Removed,
    long ElapsedMilliseconds,
    long AnalysisMilliseconds,
    int FilesVisited);

public sealed class SteamDiscoveryService
{
    private readonly GameAnalyzer gameAnalyzer;
    private readonly ILibraryIndexStore? defaultIndexStore;

    public SteamDiscoveryService(
        ExecutableDetector? executableDetector = null,
        GameAnalyzer? gameAnalyzer = null,
        ILibraryIndexStore? libraryIndexStore = null)
    {
        _ = executableDetector;
        this.gameAnalyzer = gameAnalyzer ?? new GameAnalyzer();
        defaultIndexStore = libraryIndexStore;
    }

    public IncrementalScanMetrics? LastMetrics { get; private set; }

    public Task<ScanResult> ScanAsync(
        IEnumerable<string>? explicitRoots = null,
        IReadOnlyDictionary<uint, GameOverride>? overrides = null,
        CancellationToken cancellationToken = default,
        bool includeDefaultRoots = false) =>
        ScanAsync(explicitRoots, overrides, cancellationToken, includeDefaultRoots, new ScanOptions());

    public async Task<ScanResult> ScanAsync(
        IEnumerable<string>? explicitRoots,
        IReadOnlyDictionary<uint, GameOverride>? overrides,
        CancellationToken cancellationToken,
        bool includeDefaultRoots,
        ScanOptions options)
    {
        var started = Environment.TickCount64;
        var warnings = new List<string>();
        var diagnostics = new List<SteamManifestDiagnostic>();
        var rootDiagnostics = new List<SteamRootDiagnostic>();
        var discoveredRoots = DiscoverRootCandidates(explicitRoots, includeDefaultRoots);
        var roots = new List<string>();
        var libraries = new Dictionary<string, (string Display, SteamRootSource Source)>(StringComparer.Ordinal);
        var manifestCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var includedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in discoveredRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidate.Exists)
            {
                rootDiagnostics.Add(candidate.ToDiagnostic(0, 0));
                continue;
            }
            if (!candidate.Readable)
            {
                warnings.Add($"Steam root is not readable ({candidate.Source}): {candidate.OriginalPath}");
                rootDiagnostics.Add(candidate.ToDiagnostic(0, 0));
                continue;
            }
            if (candidate.Deduplicated)
            {
                rootDiagnostics.Add(candidate.ToDiagnostic(0, 0));
                continue;
            }

            roots.Add(candidate.CanonicalPath);
            libraries[candidate.CanonicalPath] = (candidate.OriginalPath, candidate.Source);
            manifestCounts[candidate.CanonicalPath] = 0;
            includedCounts[candidate.CanonicalPath] = 0;

            var libraryFile = Path.Combine(candidate.CanonicalPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            try
            {
                var document = VdfParser.Parse(await File.ReadAllTextAsync(libraryFile, cancellationToken));
                foreach (var libraryPath in EnumerateLibraryPaths(document))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var original = libraryPath;
                    var normalized = Normalize(original);
                    if (libraries.ContainsKey(normalized))
                    {
                        rootDiagnostics.Add(new(
                            original, normalized, SteamRootSource.SteamLibrary, true,
                            CanReadDirectory(Path.Combine(normalized, "steamapps")), true, 0, 0,
                            "Deduplicated Steam library path."));
                        continue;
                    }

                    var steamApps = Path.Combine(normalized, "steamapps");
                    var exists = Directory.Exists(steamApps);
                    var readable = exists && CanReadDirectory(steamApps);
                    if (!exists)
                    {
                        warnings.Add($"Steam library is unavailable: {original}");
                        rootDiagnostics.Add(new(original, normalized, SteamRootSource.SteamLibrary, false, false, false,
                            0, 0, "Library path from libraryfolders.vdf is missing or unmounted."));
                        continue;
                    }
                    if (!readable)
                    {
                        warnings.Add($"Steam library is not readable (permission may be required): {original}");
                        rootDiagnostics.Add(new(original, normalized, SteamRootSource.SteamLibrary, true, false, false,
                            0, 0, "Filesystem permission may be required to read this Steam library."));
                        continue;
                    }

                    libraries[normalized] = (original, SteamRootSource.SteamLibrary);
                    manifestCounts[normalized] = 0;
                    includedCounts[normalized] = 0;
                }
            }
            catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not parse {libraryFile}: {exception.Message}");
            }
        }

        var indexStore = options.LibraryIndexStore ?? defaultIndexStore;
        LibraryIndexDocument? index = null;
        Dictionary<string, IndexedGameEntry> indexByKey = new(StringComparer.Ordinal);
        if (options.UseLibraryIndex && indexStore is not null && !options.ForceFullAnalysis)
        {
            index = await indexStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in index.Games)
            {
                if (string.IsNullOrWhiteSpace(entry.CanonicalRoot) || string.IsNullOrWhiteSpace(entry.StoreGameId))
                    continue;
                indexByKey[$"{entry.Store}:{entry.StoreGameId}:{Normalize(entry.CanonicalRoot)}"] = entry;
            }
        }

        var discovered = new Dictionary<(uint AppId, string GameRoot),
            (SteamGame Game, DateTime LastWriteUtc, int DiagnosticIndex, IndexedGameEntry? IndexEntry, bool FromCache)>();
        var cacheHits = 0;
        var analyzed = 0;
        var filesVisited = 0;
        var analysisMs = 0L;
        var pendingAnalysis = new List<(
            uint AppId,
            string Name,
            string Root,
            string LibraryKey,
            string GameRoot,
            string ManifestPath,
            long ManifestSize,
            long ManifestMtimeTicks,
            bool Installing,
            string? DiagnosticStateFlags,
            string? DiagnosticInstallDir,
            int DiagnosticIndex)>();

        foreach (var library in libraries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(library.Key, "steamapps");
            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").Order(StringComparer.Ordinal).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not enumerate {steamApps}: {exception.Message}");
                continue;
            }

            foreach (var manifestPath in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                manifestCounts[library.Key] = manifestCounts.GetValueOrDefault(library.Key) + 1;
                uint? diagnosticAppId = null;
                string? diagnosticName = null;
                string? diagnosticInstallDir = null;
                string? diagnosticStateFlags = null;
                try
                {
                    var manifest = VdfParser.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken)).GetObject("AppState")
                        ?? throw new FormatException("Missing AppState object.");
                    if (!uint.TryParse(manifest.GetString("appid") ?? AppIdFromFile(manifestPath), out var appId))
                        throw new FormatException("Invalid AppID.");
                    diagnosticAppId = appId;
                    var name = manifest.GetString("name") ?? throw new FormatException("Missing name.");
                    diagnosticName = name;
                    var installDir = manifest.GetString("installdir") ?? throw new FormatException("Missing installdir.");
                    diagnosticInstallDir = installDir;
                    diagnosticStateFlags = manifest.GetString("StateFlags");
                    if (IsSteamTool(name, installDir))
                    {
                        diagnostics.Add(Diagnostic(manifestPath, appId, name, installDir, diagnosticStateFlags,
                            library.Key, "Skipped", "Steam compatibility tool or shared runtime."));
                        continue;
                    }
                    var commonRoot = Path.Combine(steamApps, "common");
                    var gameRoot = Path.GetFullPath(Path.Combine(commonRoot, installDir));
                    if (!IsStrictlyWithin(commonRoot, gameRoot))
                        throw new InvalidDataException("The install directory escapes the Steam library's common directory.");
                    var installing = diagnosticStateFlags is not null &&
                        ulong.TryParse(diagnosticStateFlags, out var stateFlags) && (stateFlags & 4UL) == 0;
                    if (!Directory.Exists(gameRoot) && !installing)
                    {
                        var missingReason = $"Install directory is missing: {gameRoot}";
                        warnings.Add($"App {appId} {missingReason}");
                        diagnostics.Add(Diagnostic(manifestPath, appId, name, installDir, diagnosticStateFlags,
                            library.Key, "Skipped", missingReason));
                        continue;
                    }

                    var manifestInfo = new FileInfo(manifestPath);
                    var manifestSize = manifestInfo.Exists ? manifestInfo.Length : 0;
                    var manifestMtime = manifestInfo.Exists ? manifestInfo.LastWriteTimeUtc.Ticks : 0;
                    var root = roots.FirstOrDefault(r => IsWithin(r, manifestPath)) ?? library.Key;
                    var key = $"steam:{appId}:{Normalize(gameRoot)}";
                    var diagnosticIndex = diagnostics.Count;
                    var disposition = installing ? "Installing" : "Included";
                    var diagnosticReason = installing ? "Included while Steam is still downloading or installing it." :
                        "Included as an installed Steam game.";
                    diagnostics.Add(Diagnostic(manifestPath, appId, name, installDir, diagnosticStateFlags,
                        library.Key, disposition, diagnosticReason));

                    if (!options.ForceFullAnalysis &&
                        indexByKey.TryGetValue(key, out var cached) &&
                        cached.ManifestSize == manifestSize &&
                        cached.ManifestMtimeUtcTicks == manifestMtime &&
                        cached.AnalyzerSchemaVersion == GameAnalyzerVersions.SchemaVersion &&
                        cached.Game is not null &&
                        Directory.Exists(gameRoot) &&
                        cached.DirectoryFingerprintHash == GameAnalyzer.ComputeDirectoryFingerprintHash(gameRoot))
                    {
                        var reused = ApplyOverrides(cached.Game, gameRoot, overrides);
                        var gameKey = (appId, Normalize(gameRoot));
                        if (discovered.TryGetValue(gameKey, out var duplicate))
                        {
                            diagnostics[diagnosticIndex] = diagnostics[diagnosticIndex] with
                            {
                                Disposition = "Duplicate",
                                Reason = $"Duplicate AppID and canonical installation path; retained {diagnostics[duplicate.DiagnosticIndex].ManifestPath}."
                            };
                            continue;
                        }

                        discovered[gameKey] = (
                            reused,
                            File.GetLastWriteTimeUtc(manifestPath),
                            diagnosticIndex,
                            cached with { LastVerifiedUtc = DateTimeOffset.UtcNow },
                            true);
                        cacheHits++;
                        includedCounts[library.Key] = includedCounts.GetValueOrDefault(library.Key) + 1;
                        continue;
                    }

                    pendingAnalysis.Add((
                        appId, name, root, library.Key, gameRoot, manifestPath, manifestSize, manifestMtime,
                        installing, diagnosticStateFlags, installDir, diagnosticIndex));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not parse {manifestPath}: {exception.Message}");
                    diagnostics.Add(Diagnostic(manifestPath, diagnosticAppId, diagnosticName, diagnosticInstallDir,
                        diagnosticStateFlags, library.Key, "Malformed", exception.Message));
                }
            }
        }

        var concurrency = options.AnalysisConcurrency > 0
            ? options.AnalysisConcurrency
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var analysisTasks = pendingAnalysis.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var analysisStarted = Environment.TickCount64;
                GameOverride? gameOverride = null;
                overrides?.TryGetValue(item.AppId, out gameOverride);
                SteamGame game;
                IndexedGameEntry indexEntry;
                if (!Directory.Exists(item.GameRoot))
                {
                    var protonPrefix = Path.Combine(Path.Combine(item.LibraryKey, "steamapps"), "compatdata", item.AppId.ToString(), "pfx");
                    game = new SteamGame(
                        item.AppId, item.Name, item.Root, item.LibraryKey, item.GameRoot, protonPrefix,
                        gameOverride?.Executable, gameOverride?.DeploymentDirectory ?? item.GameRoot,
                        DetectionConfidence.None,
                        item.Installing ? "Installing: Steam has not completed this app yet." : "Install directory is missing.",
                        GameEngine.Unknown, [],
                        RequiresConfirmation: false,
                        InstallState: item.Installing ? SteamInstallState.Installing : SteamInstallState.Installed,
                        IsNativeLinux: false,
                        HasProtonPrefix: Directory.Exists(protonPrefix));
                    indexEntry = ToIndexEntry(game, item.ManifestPath, item.ManifestSize, item.ManifestMtimeTicks, 0, null);
                }
                else
                {
                    var analysis = await gameAnalyzer.AnalyzeAsync(
                        new GameInstall("steam", item.AppId.ToString(), item.Name, item.GameRoot, item.ManifestPath, item.ManifestSize, item.ManifestMtimeTicks),
                        new GameAnalysisOptions(),
                        cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref filesVisited, analysis.Fingerprint.FilesVisited);
                    Interlocked.Add(ref analysisMs, Environment.TickCount64 - analysisStarted);
                    Interlocked.Increment(ref analyzed);

                    var candidates = analysis.Fingerprint.Executables
                        .Select(x => new ExecutableCandidate(x.Path, x.Score, x.Confidence, x.Architecture, x.Size, x.Reasons))
                        .ToArray();
                    var selected = SelectCandidate(item.GameRoot, candidates, gameOverride?.Executable);
                    if (selected is null && analysis.PrimaryExecutable is null && candidates.Length > 0 &&
                        candidates[0].Confidence is DetectionConfidence.Low or DetectionConfidence.None)
                        selected = candidates[0];
                    else if (selected is null)
                        selected = analysis.PrimaryExecutable is null
                            ? candidates.FirstOrDefault()
                            : candidates.FirstOrDefault(x => x.Path == analysis.PrimaryExecutable.Path) ?? candidates.FirstOrDefault();

                    var deployment = selected is null
                        ? ResolveDeploymentWithoutExecutable(item.GameRoot, gameOverride?.DeploymentDirectory)
                        : ResolveDeployment(item.GameRoot, selected.Path, gameOverride?.DeploymentDirectory);
                    var nativeLinux = selected is null && analysis.Fingerprint.HasNativeLinuxExecutable;
                    var antiCheat = analysis.Fingerprint.AntiCheat.RequiresConfirmation;
                    var protonPrefix = Path.Combine(item.LibraryKey, "steamapps", "compatdata", item.AppId.ToString(), "pfx");
                    var reason = item.Installing ? "Installing: Steam has not completed this app yet." :
                        nativeLinux ? "Native Linux / unsupported: no Windows executable was detected." :
                        selected is null ? "No Windows executable found; a Proton prefix or manual executable may become available later." :
                        gameOverride?.Executable is not null ? "Selected because a persistent manual executable override is configured." :
                        analysis.PrimaryExecutable is null && candidates.Length > 1
                            ? $"Low confidence executable ranking; showing {Math.Min(5, candidates.Length)} candidates."
                            : FormatSelectionReason(selected);
                    game = new SteamGame(
                        item.AppId, item.Name, item.Root, item.LibraryKey, item.GameRoot, protonPrefix,
                        selected?.Path, deployment, selected?.Confidence ?? DetectionConfidence.None, reason,
                        analysis.Fingerprint.Engine, candidates, antiCheat,
                        item.Installing ? SteamInstallState.Installing : SteamInstallState.Installed,
                        nativeLinux, Directory.Exists(protonPrefix),
                        Platform: nativeLinux ? GameBinaryPlatform.Linux : GameBinaryPlatform.Windows,
                        Environment: nativeLinux ? CompatibilityEnvironment.Native : CompatibilityEnvironment.Proton,
                        IsActionable: !nativeLinux,
                        UnsupportedReason: nativeLinux
                            ? "Native Linux / unsupported: no Windows executable was detected."
                            : null);
                    indexEntry = ToIndexEntry(
                        game, item.ManifestPath, item.ManifestSize, item.ManifestMtimeTicks,
                        analysis.Fingerprint.DirectoryFingerprint.SampleHash, analysis.Fingerprint);
                }

                return (item, game, indexEntry);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var analyzedGames = await Task.WhenAll(analysisTasks).ConfigureAwait(false);
        foreach (var (item, game, indexEntry) in analyzedGames)
        {
            var gameKey = (item.AppId, Normalize(item.GameRoot));
            if (discovered.TryGetValue(gameKey, out var duplicate))
            {
                diagnostics[item.DiagnosticIndex] = diagnostics[item.DiagnosticIndex] with
                {
                    Disposition = "Duplicate",
                    Reason = $"Duplicate AppID and canonical installation path; retained {diagnostics[duplicate.DiagnosticIndex].ManifestPath}."
                };
                continue;
            }

            var diagnosticReason = item.Installing ? "Included while Steam is still downloading or installing it." :
                game.IsNativeLinux ? "Included as Native Linux / unsupported; no Windows executable was found." :
                game.Executable is null ? "Included; no Windows executable was found." : "Included as an installed Steam game.";
            diagnostics[item.DiagnosticIndex] = diagnostics[item.DiagnosticIndex] with { Reason = diagnosticReason };
            discovered[gameKey] = (game, File.GetLastWriteTimeUtc(item.ManifestPath), item.DiagnosticIndex, indexEntry, false);
            includedCounts[item.LibraryKey] = includedCounts.GetValueOrDefault(item.LibraryKey) + 1;
        }

        foreach (var library in libraries)
        {
            rootDiagnostics.Add(new(
                library.Value.Display,
                library.Key,
                library.Value.Source,
                true,
                true,
                false,
                manifestCounts.GetValueOrDefault(library.Key),
                includedCounts.GetValueOrDefault(library.Key),
                null));
        }

        var selectedEntries = discovered.Values
            .GroupBy(item => item.Game.AppId)
            .Select(group => group.OrderByDescending(item => item.LastWriteUtc)
                .ThenBy(item => Normalize(item.Game.GameRoot), StringComparer.Ordinal)
                .First())
            .ToArray();
        var selectedIdentities = selectedEntries.Select(entry => (entry.Game.AppId, Normalize(entry.Game.GameRoot))).ToHashSet();
        foreach (var entry in discovered.Where(entry => !selectedIdentities.Contains(entry.Key)))
        {
            var diagnosticIndex = entry.Value.DiagnosticIndex;
            diagnostics[diagnosticIndex] = diagnostics[diagnosticIndex] with
            {
                Disposition = "Duplicate",
                Reason = "Duplicate AppID at another canonical installation path; a newer manifest was retained."
            };
        }

        var games = selectedEntries
            .Select(entry => InstalledGame.FromSteamGame(entry.Game))
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var removed = 0;
        if (indexStore is not null && options.UseLibraryIndex)
        {
            var nextIndex = new LibraryIndexDocument
            {
                ScanGeneration = (index?.ScanGeneration ?? 0) + 1,
                SteamRoots = roots,
                Libraries = libraries.Keys.Order(StringComparer.Ordinal).ToList(),
                Warnings = warnings.ToList(),
                Games = selectedEntries.Select(entry =>
                {
                    var indexed = (entry.IndexEntry ?? ToIndexEntry(
                        entry.Game, null, 0, 0,
                        entry.FromCache ? entry.IndexEntry?.DirectoryFingerprintHash ?? 0 : GameAnalyzer.ComputeDirectoryFingerprintHash(entry.Game.GameRoot),
                        null)) with
                    {
                        LastVerifiedUtc = DateTimeOffset.UtcNow
                    };
                    indexed.Game = ApplyOverrides(entry.Game, entry.Game.GameRoot, overrides);
                    return indexed;
                }).ToList()
            };
            removed = Math.Max(0, (index?.Games.Count ?? 0) - nextIndex.Games.Count);
            if (!cancellationToken.IsCancellationRequested)
                await indexStore.SaveAsync(nextIndex, CancellationToken.None).ConfigureAwait(false);
        }

        LastMetrics = new IncrementalScanMetrics(
            manifestCounts.Values.Sum(),
            cacheHits,
            analyzed,
            removed,
            Environment.TickCount64 - started,
            analysisMs,
            filesVisited);

        return new ScanResult(
            games,
            roots,
            libraries.Keys.Order(StringComparer.Ordinal).ToList(),
            warnings,
            diagnostics,
            rootDiagnostics);
    }

    public static IReadOnlyList<string> DiscoverRoots(
        IEnumerable<string>? explicitRoots = null,
        bool includeDefaultRoots = false) =>
        DiscoverRootCandidates(explicitRoots, includeDefaultRoots)
            .Where(candidate => candidate is { Exists: true, Readable: true, Deduplicated: false })
            .Select(candidate => candidate.CanonicalPath)
            .ToArray();

    public static IReadOnlyList<SteamRootCandidate> DiscoverRootCandidates(
        IEnumerable<string>? explicitRoots = null,
        bool includeDefaultRoots = false)
    {
        var ordered = new List<(string Original, SteamRootSource Source)>();
        if (explicitRoots is not null)
        {
            foreach (var root in explicitRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
                ordered.Add((root, SteamRootSource.Explicit));
        }

        if (explicitRoots is null || includeDefaultRoots)
        {
            foreach (var root in DefaultRootCandidates())
                ordered.Add(root);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SteamRootCandidate>();
        foreach (var (original, source) in ordered)
        {
            var expanded = Environment.ExpandEnvironmentVariables(original);
            string canonical;
            try { canonical = Normalize(expanded); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                result.Add(new(original, expanded, source, false, false, false,
                    $"Path could not be normalized: {exception.Message}"));
                continue;
            }

            var steamApps = Path.Combine(canonical, "steamapps");
            var exists = Directory.Exists(steamApps);
            var readable = exists && CanReadDirectory(steamApps);
            var deduplicated = exists && !seen.Add(canonical);
            var skip = !exists ? "Steam root does not contain steamapps." :
                !readable ? "Filesystem permission may be required to read this Steam root." :
                deduplicated ? "Deduplicated canonical Steam root." : null;
            result.Add(new(original, canonical, source, exists, readable, deduplicated, skip));
        }
        return result;
    }

    private static IndexedGameEntry ToIndexEntry(
        SteamGame game,
        string? manifestPath,
        long manifestSize,
        long manifestMtime,
        long directoryHash,
        GameFingerprint? fingerprint) =>
        new()
        {
            Store = "steam",
            StoreGameId = game.AppId.ToString(),
            CanonicalRoot = Normalize(game.GameRoot),
            ManifestPath = manifestPath,
            ManifestSize = manifestSize,
            ManifestMtimeUtcTicks = manifestMtime,
            DirectoryFingerprintHash = directoryHash,
            AnalyzerSchemaVersion = GameAnalyzerVersions.SchemaVersion,
            IndexedUtc = DateTimeOffset.UtcNow,
            LastVerifiedUtc = DateTimeOffset.UtcNow,
            SelectedExecutable = game.Executable,
            Engine = game.Engine,
            Confidence = game.Confidence,
            HasAntiCheat = game.RequiresConfirmation,
            IsNativeLinux = game.IsNativeLinux,
            HasProtonPrefix = game.HasProtonPrefix,
            ProtonPrefix = game.ProtonPrefix,
            Game = game
        };

    private static SteamGame ApplyOverrides(
        SteamGame game,
        string gameRoot,
        IReadOnlyDictionary<uint, GameOverride>? overrides)
    {
        if (overrides is null || !overrides.TryGetValue(game.AppId, out var gameOverride))
            return game;
        var selected = SelectCandidate(gameRoot, game.Candidates, gameOverride.Executable);
        var deployment = selected is null
            ? ResolveDeploymentWithoutExecutable(gameRoot, gameOverride.DeploymentDirectory)
            : ResolveDeployment(gameRoot, selected.Path, gameOverride.DeploymentDirectory);
        return game with
        {
            Executable = selected?.Path ?? game.Executable,
            DeploymentDirectory = deployment,
            Confidence = selected?.Confidence ?? game.Confidence,
            SelectionReason = gameOverride.Executable is not null
                ? "Selected because a persistent manual executable override is configured."
                : game.SelectionReason
        };
    }

    private static IEnumerable<(string Path, SteamRootSource Source)> DefaultRootCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdgData))
            xdgData = Path.Combine(home, ".local", "share");

        var overrideRoot = Environment.GetEnvironmentVariable("STEAM_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            yield return (overrideRoot, SteamRootSource.Environment);

        yield return (Path.Combine(xdgData, "Steam"), SteamRootSource.Xdg);
        yield return (Path.Combine(home, ".local", "share", "Steam"), SteamRootSource.Native);
        yield return (Path.Combine(home, ".steam", "steam"), SteamRootSource.Native);
        yield return (Path.Combine(home, ".steam", "root"), SteamRootSource.Native);
        yield return (Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"), SteamRootSource.Flatpak);
        yield return (Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"), SteamRootSource.Snap);
        yield return (Path.Combine(home, "snap", "steam", "current", ".local", "share", "Steam"), SteamRootSource.Snap);
    }

    private static IEnumerable<string> EnumerateLibraryPaths(VdfObject document)
    {
        var folders = document.GetObject("libraryfolders") ?? document.GetObject("LibraryFolders") ?? document;
        foreach (var entry in folders.Values)
        {
            if (entry.Value is VdfObject nested)
            {
                var path = nested.GetString("path");
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
                continue;
            }
            if (entry.Value is string legacyPath &&
                !entry.Key.Equals("TimeNextStatsReport", StringComparison.OrdinalIgnoreCase) &&
                !entry.Key.Equals("ContentStatsID", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(legacyPath) &&
                !legacyPath.All(char.IsDigit))
                yield return legacyPath;
        }
    }

    private static bool CanReadDirectory(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Any();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ExecutableCandidate? SelectCandidate(string root, IReadOnlyList<ExecutableCandidate> candidates, string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return candidates.FirstOrDefault();
        var full = Path.GetFullPath(Path.IsPathRooted(overridePath) ? overridePath : Path.Combine(root, overridePath));
        if (!IsWithin(root, full) || !File.Exists(full) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Executable override must identify an existing .exe inside the game root.");
        return candidates.FirstOrDefault(x => x.Path == full) ?? new ExecutableCandidate(full, 100, DetectionConfidence.High, PeReader.ReadArchitecture(full), new FileInfo(full).Length, ["manual override"]);
    }

    private static string ResolveDeployment(string root, string executable, string? overridePath)
    {
        var value = string.IsNullOrWhiteSpace(overridePath) ? Path.GetDirectoryName(executable)! :
            Path.GetFullPath(Path.IsPathRooted(overridePath) ? overridePath : Path.Combine(root, overridePath));
        if (!IsWithin(root, value)) throw new InvalidDataException("Deployment override must stay inside the game root.");
        return value;
    }

    private static string ResolveDeploymentWithoutExecutable(string root, string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return root;
        var value = Path.GetFullPath(Path.IsPathRooted(overridePath) ? overridePath : Path.Combine(root, overridePath));
        if (!IsWithin(root, value)) throw new InvalidDataException("Deployment override must stay inside the game root.");
        return value;
    }

    private static string FormatSelectionReason(ExecutableCandidate selected)
    {
        var architecture = selected.Architecture switch
        {
            PeArchitecture.X64 => "64-bit",
            PeArchitecture.X86 => "32-bit",
            PeArchitecture.Arm64 => "ARM64",
            _ => "detected"
        };
        var location = selected.Reasons.Any(reason =>
            reason.Contains("Win64", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Binaries", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("game directory", StringComparison.OrdinalIgnoreCase))
            ? " in the expected game directory"
            : string.Empty;
        return $"Selected because it is the primary {architecture} game executable{location}.";
    }

    private static bool IsSteamTool(string name, string installDirectory) =>
        name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) ||
        installDirectory.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase);
    private static string AppIdFromFile(string path) => Path.GetFileNameWithoutExtension(path).Replace("appmanifest_", string.Empty, StringComparison.Ordinal);
    private static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(path.Replace("\\\\", "\\", StringComparison.Ordinal))));
        try { return new DirectoryInfo(full).ResolveLinkTarget(true)?.FullName ?? full; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return full; }
    }

    private static SteamManifestDiagnostic Diagnostic(
        string manifestPath,
        uint? appId,
        string? name,
        string? installDirectory,
        string? stateFlags,
        string libraryRoot,
        string disposition,
        string reason) => new(Path.GetFullPath(manifestPath), appId, name, installDirectory, stateFlags,
            Normalize(libraryRoot), disposition, reason);
    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Normalize(root) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.Ordinal) || Normalize(root) == Path.TrimEndingDirectorySeparator(normalizedPath);
    }

    private static bool IsStrictlyWithin(string root, string path) =>
        Path.GetFullPath(path).StartsWith(Normalize(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

public sealed record SteamRootCandidate(
    string OriginalPath,
    string CanonicalPath,
    SteamRootSource Source,
    bool Exists,
    bool Readable,
    bool Deduplicated,
    string? SkipReason)
{
    public SteamRootDiagnostic ToDiagnostic(int manifestsFound, int gamesIncluded) =>
        new(OriginalPath, CanonicalPath, Source, Exists, Readable, Deduplicated, manifestsFound, gamesIncluded, SkipReason);
}
