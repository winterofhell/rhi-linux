using System.Diagnostics;
using RhiLinux.Core;

namespace RhiLinux.Sources;

public sealed class BottlesGameSourceProvider : IGameSourceProvider
{
    public const string ProviderId = "bottles";
    public string Id => ProviderId;
    public string DisplayName => "Bottles";

    public Task<IReadOnlyList<GameSourceRoot>> DiscoverRootsAsync(
        GameSourceDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(string Path, SourceRootKind Kind)>
        {
            (Path.Combine(context.DataHome, "bottles", "bottles"), SourceRootKind.Xdg),
            (Path.Combine(context.HomeDirectory, ".local", "share", "bottles", "bottles"), SourceRootKind.Native),
            (SourceRootDiscovery.FlatpakData(context.HomeDirectory, "com.usebottles.bottles", "bottles", "bottles"), SourceRootKind.Flatpak)
        };

        return Task.FromResult(SourceRootDiscovery.ResolveCandidates(ProviderId, candidates, context.RootsForProvider(ProviderId), context.HomeDirectory));
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

        string[] bottleDirs;
        try
        {
            bottleDirs = Directory.GetDirectories(root.CanonicalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SourceRecordFactory.EmptyScan(
                ProviderId,
                root,
                [new(ProviderId, SourceDiagnosticCodes.SourceRootUnreadable, SourceDiagnosticSeverity.Warning,
                    "Bottles root could not be enumerated.", exception.Message, root.CanonicalPath)],
                started.Elapsed);
        }

        var allYamlFiles = bottleDirs
            .Select(dir => Path.Combine(dir, "bottle.yml"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var yamlFiles = context.IsTargeted
            ? allYamlFiles.Where(context.IncludesDocument).ToArray()
            : allYamlFiles;
        var fingerprint = SourceRootDiscovery.SourceFingerprint(allYamlFiles) + ":v1";
        if (!context.ForceFullScan && !context.IsTargeted &&
            context.PreviousFingerprints.TryGetValue(FingerprintKey(root), out var previous) &&
            string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return SourceRecordFactory.CachedScan(ProviderId, root, fingerprint, started.Elapsed, yamlFiles);
        }

        var diagnostics = new List<SourceDiagnostic>();
        var games = new List<SourceGameRecord>();
        var malformed = new List<SourceGameRecord>();
        var skipped = new List<SourceGameRecord>();

        foreach (var yamlPath in yamlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            ParseBottle(yamlPath, games, malformed, skipped, diagnostics);
        }

        if (yamlFiles.Length == 0)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMissing,
                SourceDiagnosticSeverity.Info,
                "No bottle.yml files were found.",
                root.CanonicalPath,
                root.CanonicalPath));
        }

        return new GameSourceScanResult(
            ProviderId, root, games, diagnostics, yamlFiles, malformed, skipped,
            fingerprint, started.Elapsed, false, true);
    }

    public static string FingerprintKey(GameSourceRoot root) => $"{ProviderId}:{root.CanonicalPath}";

    private static void ParseBottle(
        string yamlPath,
        List<SourceGameRecord> games,
        List<SourceGameRecord> malformed,
        List<SourceGameRecord> skipped,
        List<SourceDiagnostic> diagnostics)
    {
        var bottleRoot = Path.GetDirectoryName(yamlPath)!;
        var bottleName = Path.GetFileName(bottleRoot);
        string text;
        try
        {
            text = File.ReadAllText(yamlPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                $"bottle.yml for '{bottleName}' could not be read.",
                exception.Message,
                yamlPath));
            return;
        }

        if (!MinimalYaml.TryParse(text, out var mapping, out var error) || mapping is null)
        {
            diagnostics.Add(new(
                ProviderId,
                SourceDiagnosticCodes.SourceMetadataMalformed,
                SourceDiagnosticSeverity.Warning,
                $"bottle.yml for '{bottleName}' could not be parsed.",
                error,
                yamlPath));
            malformed.Add(new(
                ProviderId, GameStore.Unknown, GameLauncher.Bottles, bottleName, bottleName,
                bottleRoot, null, bottleRoot, null, GameBinaryPlatform.Windows, CompatibilityEnvironment.Wine,
                yamlPath, null, SourceRootDiscovery.GetMetadataTimestamp(yamlPath),
                new Dictionary<string, string>(), [], IsActionable: false,
                UnsupportedReason: "Malformed bottle.yml."));
            return;
        }

        var environment = MinimalYaml.GetString(mapping, "Environment") ??
                          MinimalYaml.GetString(mapping, "environment");
        var programs = MinimalYaml.GetMapping(mapping, "External_Programs") ??
                       MinimalYaml.GetMapping(mapping, "External_programs");
        if (programs is null || programs.Children.Count == 0)
            return;

        var modified = SourceRootDiscovery.GetMetadataTimestamp(yamlPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (programId, node) in programs.Children)
        {
            if (node is not YamlNode.Mapping program)
                continue;

            var name = MinimalYaml.GetString(program, "name") ?? programId;
            var executable = MinimalYaml.GetString(program, "executable") ??
                             MinimalYaml.GetString(program, "path");
            var folder = MinimalYaml.GetString(program, "folder");
            var id = MinimalYaml.GetString(program, "id") ?? programId;

            if (string.IsNullOrWhiteSpace(executable))
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.SourceRecordIncomplete,
                    SourceDiagnosticSeverity.Info,
                    $"Bottles program '{name}' has no executable.",
                    null,
                    yamlPath,
                    id));
                continue;
            }

            var resolved = ResolveExecutable(bottleRoot, executable);
            var installRoot = !string.IsNullOrWhiteSpace(folder)
                ? ResolveFolder(bottleRoot, folder!)
                : Path.GetDirectoryName(resolved) ?? bottleRoot;

            var key = $"{resolved}|{name}";
            if (!seen.Add(key))
            {
                diagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.DuplicateSourceRecord,
                    SourceDiagnosticSeverity.Info,
                    $"Duplicate Bottles program '{name}'.",
                    null,
                    yamlPath,
                    id));
                continue;
            }

            var lowConfidence = SourceRecordFactory.LooksLikeLowConfidenceProgram(name, resolved);
            var isGaming = string.Equals(environment, "Gaming", StringComparison.OrdinalIgnoreCase);
            var requiresConfirmation = lowConfidence || !isGaming;
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bottle"] = bottleName,
                ["programId"] = id
            };
            if (!string.IsNullOrWhiteSpace(environment))
                attributes["environment"] = environment!;

            var recordDiagnostics = new List<SourceDiagnostic>();
            if (lowConfidence)
            {
                recordDiagnostics.Add(new(
                    ProviderId,
                    SourceDiagnosticCodes.LowConfidenceProgram,
                    SourceDiagnosticSeverity.Warning,
                    $"Bottles program '{name}' looks like a setup, uninstaller, or launcher.",
                    resolved,
                    yamlPath,
                    id));
            }

            var record = SourceRecordFactory.Create(
                ProviderId,
                GameStore.Unknown,
                GameLauncher.Bottles,
                id,
                name,
                installRoot,
                resolved,
                bottleRoot,
                folder is null ? null : ResolveFolder(bottleRoot, folder),
                GameBinaryPlatform.Windows,
                CompatibilityEnvironment.Wine,
                yamlPath,
                yamlPath,
                modified,
                attributes,
                recordDiagnostics,
                requiresConfirmation: requiresConfirmation,
                isActionable: !lowConfidence,
                unsupportedReason: lowConfidence
                    ? "Low-confidence Bottles program requires confirmation."
                    : null,
                allowOutsideRoot: true);

            if (lowConfidence)
                skipped.Add(record);
            else
                games.Add(record);
        }
    }

    private static string ResolveExecutable(string bottleRoot, string executable)
    {
        var trimmed = executable.Trim();
        if (trimmed.Contains(":\\", StringComparison.Ordinal) ||
            trimmed.Contains(":/", StringComparison.Ordinal) ||
            trimmed.StartsWith("/drive_c/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("drive_c/", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
        {
            return GameIdentity.MapWindowsPath(bottleRoot, trimmed);
        }

        if (Path.IsPathRooted(trimmed))
            return GameIdentity.NormalizePath(trimmed);

        return GameIdentity.NormalizePath(Path.Combine(bottleRoot, trimmed));
    }

    private static string ResolveFolder(string bottleRoot, string folder)
    {
        var trimmed = folder.Trim();
        if (trimmed.Contains(":\\", StringComparison.Ordinal) ||
            trimmed.Contains(":/", StringComparison.Ordinal) ||
            (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
            return GameIdentity.MapWindowsPath(bottleRoot, trimmed);
        if (Path.IsPathRooted(trimmed))
            return GameIdentity.NormalizePath(trimmed);
        return GameIdentity.NormalizePath(Path.Combine(bottleRoot, trimmed));
    }
}
