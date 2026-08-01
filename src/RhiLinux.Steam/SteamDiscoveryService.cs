using RhiLinux.Core;

namespace RhiLinux.Steam;

public sealed class SteamDiscoveryService(ExecutableDetector executableDetector)
{
    public async Task<ScanResult> ScanAsync(
        IEnumerable<string>? explicitRoots = null,
        IReadOnlyDictionary<uint, GameOverride>? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var diagnostics = new List<SteamManifestDiagnostic>();
        var roots = DiscoverRoots(explicitRoots).ToList();
        var libraries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            libraries[Normalize(root)] = root;
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            try
            {
                var document = VdfParser.Parse(await File.ReadAllTextAsync(libraryFile, cancellationToken));
                var folders = document.GetObject("libraryfolders") ?? document;
                foreach (var entry in folders.Objects())
                {
                    var path = entry.Value.GetString("path");
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var normalized = Normalize(path);
                    if (Directory.Exists(Path.Combine(normalized, "steamapps"))) libraries[normalized] = normalized;
                }
            }
            catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
            { warnings.Add($"Could not parse {libraryFile}: {exception.Message}"); }
        }

        var discovered = new Dictionary<(uint AppId, string GameRoot),
            (SteamGame Game, DateTime LastWriteUtc, int DiagnosticIndex)>();
        foreach (var library in libraries.Values.Order(StringComparer.Ordinal))
        {
            var steamApps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { warnings.Add($"Could not enumerate {steamApps}: {exception.Message}"); continue; }
            foreach (var manifestPath in manifests)
            {
                uint? diagnosticAppId = null;
                string? diagnosticName = null;
                string? diagnosticInstallDir = null;
                string? diagnosticStateFlags = null;
                try
                {
                    var manifest = VdfParser.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken)).GetObject("AppState")
                        ?? throw new FormatException("Missing AppState object.");
                    if (!uint.TryParse(manifest.GetString("appid") ?? AppIdFromFile(manifestPath), out var appId)) throw new FormatException("Invalid AppID.");
                    diagnosticAppId = appId;
                    var name = manifest.GetString("name") ?? throw new FormatException("Missing name.");
                    diagnosticName = name;
                    var installDir = manifest.GetString("installdir") ?? throw new FormatException("Missing installdir.");
                    diagnosticInstallDir = installDir;
                    diagnosticStateFlags = manifest.GetString("StateFlags");
                    if (IsSteamTool(name, installDir))
                    {
                        diagnostics.Add(Diagnostic(manifestPath, appId, name, installDir, diagnosticStateFlags,
                            library, "Skipped", "Steam compatibility tool or shared runtime."));
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
                            library, "Skipped", missingReason));
                        continue;
                    }
                    var candidates = Directory.Exists(gameRoot) ? executableDetector.Rank(gameRoot, name) : [];
                    GameOverride? gameOverride = null;
                    if (overrides is not null) overrides.TryGetValue(appId, out gameOverride);
                    var selected = SelectCandidate(gameRoot, candidates, gameOverride?.Executable);
                    var deployment = selected is null
                        ? ResolveDeploymentWithoutExecutable(gameRoot, gameOverride?.DeploymentDirectory)
                        : ResolveDeployment(gameRoot, selected.Path, gameOverride?.DeploymentDirectory);
                    var root = roots.FirstOrDefault(r => IsWithin(r, manifestPath)) ?? library;
                    var nativeLinux = selected is null && Directory.Exists(gameRoot) && HasNativeLinuxExecutable(gameRoot);
                    var reason = installing ? "Installing: Steam has not completed this app yet." :
                        nativeLinux ? "Native Linux / unsupported: no Windows executable was detected." :
                        selected is null ? "No Windows executable found; a Proton prefix or manual executable may become available later." :
                        gameOverride?.Executable is not null ? "Persistent manual executable override." :
                        string.Join("; ", selected.Reasons.Take(4)) + ".";
                    var antiCheat = Directory.Exists(gameRoot) && HasAntiCheat(gameRoot);
                    var protonPrefix = Path.Combine(steamApps, "compatdata", appId.ToString(), "pfx");
                    var game = new SteamGame(appId, name, root, library, gameRoot,
                        protonPrefix, selected?.Path, deployment,
                        selected?.Confidence ?? DetectionConfidence.None, reason,
                        Directory.Exists(gameRoot) ? executableDetector.DetectEngine(gameRoot) : GameEngine.Unknown,
                        candidates, antiCheat, installing ? SteamInstallState.Installing : SteamInstallState.Installed,
                        nativeLinux, Directory.Exists(protonPrefix));
                    var disposition = installing ? "Installing" : "Included";
                    var diagnosticReason = installing ? "Included while Steam is still downloading or installing it." :
                        nativeLinux ? "Included as Native Linux / unsupported; no Windows executable was found." :
                        selected is null ? "Included; no Windows executable was found." : "Included as an installed Steam game.";
                    var diagnosticIndex = diagnostics.Count;
                    diagnostics.Add(Diagnostic(manifestPath, appId, name, installDir, diagnosticStateFlags,
                        library, disposition, diagnosticReason));
                    var key = (appId, Normalize(gameRoot));
                    if (discovered.TryGetValue(key, out var duplicate))
                    {
                        diagnostics[diagnosticIndex] = diagnostics[diagnosticIndex] with
                        {
                            Disposition = "Duplicate",
                            Reason = $"Duplicate AppID and canonical installation path; retained {diagnostics[duplicate.DiagnosticIndex].ManifestPath}."
                        };
                        continue;
                    }
                    discovered[key] = (game, File.GetLastWriteTimeUtc(manifestPath), diagnosticIndex);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not parse {manifestPath}: {exception.Message}");
                    diagnostics.Add(Diagnostic(manifestPath, diagnosticAppId, diagnosticName, diagnosticInstallDir,
                        diagnosticStateFlags, library, "Malformed", exception.Message));
                }
            }
        }
        var selectedEntries = discovered.Values
            .GroupBy(item => item.Game.AppId)
            .Select(group => group.OrderByDescending(item => item.LastWriteUtc)
                .ThenBy(item => Normalize(item.Game.GameRoot), StringComparer.Ordinal)
                .First().Game)
            .ToArray();
        var selectedIdentities = selectedEntries.Select(game => (game.AppId, Normalize(game.GameRoot))).ToHashSet();
        foreach (var entry in discovered.Where(entry => !selectedIdentities.Contains(entry.Key)))
        {
            var index = entry.Value.DiagnosticIndex;
            diagnostics[index] = diagnostics[index] with
            {
                Disposition = "Duplicate",
                Reason = "Duplicate AppID at another canonical installation path; a newer manifest was retained."
            };
        }
        var games = selectedEntries
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ScanResult(games, roots, libraries.Values.Order(StringComparer.Ordinal).ToList(), warnings, diagnostics);
    }

    public static IReadOnlyList<string> DiscoverRoots(IEnumerable<string>? explicitRoots = null)
    {
        var candidates = explicitRoots?.ToList() ?? DefaultRoots();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var normalized = Normalize(candidate);
            if (Directory.Exists(Path.Combine(normalized, "steamapps"))) result[normalized] = normalized;
        }
        return result.Values.Order(StringComparer.Ordinal).ToList();
    }

    private static List<string> DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();
        var overrideRoot = Environment.GetEnvironmentVariable("STEAM_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot)) roots.Add(overrideRoot);
        roots.AddRange([
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam")]);
        return roots;
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

    private static bool HasNativeLinuxExecutable(string root)
    {
        try
        {
            var magic = new byte[4];
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Read(magic) == magic.Length && magic.SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }))
                    return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static bool HasAntiCheat(string root)
    {
        var markers = new[] { "EasyAntiCheat", "BattlEye", "start_protected_game.exe" };
        return markers.Any(marker => Directory.Exists(Path.Combine(root, marker)) || File.Exists(Path.Combine(root, marker)));
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
