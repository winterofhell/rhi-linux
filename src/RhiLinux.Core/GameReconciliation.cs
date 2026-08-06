namespace RhiLinux.Core;

public sealed record ReconciliationResult(
    IReadOnlyList<InstalledGame> Games,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    int DuplicateSourceCount,
    int DuplicatePhysicalInstallCount);

public static class GameReconciliation
{
    private static readonly GameLauncher[] LauncherPriority =
    [
        GameLauncher.Manual,
        GameLauncher.Steam,
        GameLauncher.Heroic,
        GameLauncher.Legendary,
        GameLauncher.Lutris,
        GameLauncher.Bottles,
        GameLauncher.Minigalaxy
    ];

    public static ReconciliationResult Reconcile(IEnumerable<SourceGameRecord> records)
    {
        var ordered = records
            .OrderBy(record => record.ProviderId, StringComparer.Ordinal)
            .ThenBy(record => record.ExternalId, StringComparer.Ordinal)
            .ThenBy(record => record.MetadataPath, StringComparer.Ordinal)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var diagnostics = new List<SourceDiagnostic>();
        var groups = new List<List<SourceGameRecord>>();
        var duplicateSource = 0;
        var duplicatePhysical = 0;

        foreach (var record in ordered)
        {
            var matched = false;
            foreach (var group in groups)
            {
                if (!CanMerge(group, record, out var strength))
                    continue;

                if (strength == EvidenceStrength.Weak)
                    continue;

                if (group.Any(existing =>
                        existing.ProviderId == record.ProviderId &&
                        string.Equals(existing.ExternalId, record.ExternalId, StringComparison.Ordinal) &&
                        string.Equals(existing.MetadataPath, record.MetadataPath, StringComparison.Ordinal)))
                {
                    duplicateSource++;
                    diagnostics.Add(new(
                        record.ProviderId,
                        SourceDiagnosticCodes.DuplicateSourceRecord,
                        SourceDiagnosticSeverity.Info,
                        $"Skipped duplicate source record for '{record.Name}'.",
                        null,
                        record.MetadataPath,
                        record.ExternalId));
                    matched = true;
                    break;
                }

                if (strength == EvidenceStrength.Strong)
                {
                    duplicatePhysical++;
                    diagnostics.Add(new(
                        record.ProviderId,
                        SourceDiagnosticCodes.DuplicatePhysicalInstall,
                        SourceDiagnosticSeverity.Info,
                        $"Merged duplicate physical installation for '{record.Name}'.",
                        $"Evidence: strong identity overlap with {group[0].ProviderId}:{group[0].ExternalId}",
                        record.MetadataPath,
                        record.ExternalId));
                }

                group.Add(record);
                matched = true;
                break;
            }

            if (!matched)
                groups.Add([record]);
        }

        var games = groups.Select(group => MergeGroup(group, diagnostics)).ToList();
        return new ReconciliationResult(
            games
                .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.InstallId.Value, StringComparer.Ordinal)
                .ToArray(),
            diagnostics,
            duplicateSource,
            duplicatePhysical);
    }

    private enum EvidenceStrength { None, Weak, Medium, Strong }

    private static bool CanMerge(
        IReadOnlyList<SourceGameRecord> group,
        SourceGameRecord candidate,
        out EvidenceStrength strength)
    {
        strength = EvidenceStrength.None;
        foreach (var existing in group)
        {
            var current = ScoreEvidence(existing, candidate);
            if (current > strength)
                strength = current;
        }

        return strength is EvidenceStrength.Strong or EvidenceStrength.Medium;
    }

    private static EvidenceStrength ScoreEvidence(SourceGameRecord left, SourceGameRecord right)
    {
        var leftRoot = GameIdentity.NormalizePath(left.InstallRoot ?? string.Empty);
        var rightRoot = GameIdentity.NormalizePath(right.InstallRoot ?? string.Empty);
        var leftExe = GameIdentity.NormalizePath(left.ExecutableHint ?? string.Empty);
        var rightExe = GameIdentity.NormalizePath(right.ExecutableHint ?? string.Empty);

        var sameExe = !string.IsNullOrWhiteSpace(leftExe) &&
                      string.Equals(leftExe, rightExe, StringComparison.Ordinal);
        var sameRoot = !string.IsNullOrWhiteSpace(leftRoot) &&
                       string.Equals(leftRoot, rightRoot, StringComparison.Ordinal);
        var sameStoreId = left.Store != GameStore.Unknown &&
                          left.Store == right.Store &&
                          !string.IsNullOrWhiteSpace(left.ExternalId) &&
                          string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
        var sameSourceId = string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal) &&
                           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

        if (sameExe || sameRoot && sameStoreId || sameSourceId && sameRoot)
            return EvidenceStrength.Strong;

        var samePrefix = !string.IsNullOrWhiteSpace(left.PrefixHint) &&
                         string.Equals(
                             GameIdentity.NormalizePath(left.PrefixHint),
                             GameIdentity.NormalizePath(right.PrefixHint ?? string.Empty),
                             StringComparison.Ordinal);
        var nestedRoot = !string.IsNullOrWhiteSpace(leftRoot) &&
                         !string.IsNullOrWhiteSpace(rightRoot) &&
                         sameExe &&
                         (GameIdentity.IsPathInsideRoot(leftRoot, rightRoot) ||
                          GameIdentity.IsPathInsideRoot(rightRoot, leftRoot));
        var sameLauncherExternal = left.Launcher == right.Launcher &&
                                   !string.IsNullOrWhiteSpace(left.ExternalId) &&
                                   string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

        if (samePrefix && sameExe || nestedRoot || sameLauncherExternal && (sameRoot || sameExe))
            return EvidenceStrength.Medium;

        var sameTitle = !string.IsNullOrWhiteSpace(left.Name) &&
                        string.Equals(NormalizeTitle(left.Name), NormalizeTitle(right.Name), StringComparison.Ordinal);
        var sameFileName = !string.IsNullOrWhiteSpace(leftExe) &&
                           !string.IsNullOrWhiteSpace(rightExe) &&
                           string.Equals(Path.GetFileName(leftExe), Path.GetFileName(rightExe), StringComparison.OrdinalIgnoreCase);

        if (sameTitle || sameFileName)
            return EvidenceStrength.Weak;

        return EvidenceStrength.None;
    }

    private static InstalledGame MergeGroup(
        IReadOnlyList<SourceGameRecord> sources,
        List<SourceDiagnostic> diagnostics)
    {
        var orderedSources = sources
            .OrderBy(source => Array.IndexOf(LauncherPriority, source.Launcher) is var index and >= 0 ? index : 99)
            .ThenByDescending(QualityScore)
            .ThenBy(source => source.ProviderId, StringComparer.Ordinal)
            .ThenBy(source => source.ExternalId, StringComparer.Ordinal)
            .ThenBy(source => source.MetadataPath, StringComparer.Ordinal)
            .ToArray();

        var primary = orderedSources[0];
        var fieldSelections = new List<FieldSelectionEvidence>();

        var installRoot = SelectField(
            orderedSources,
            source => source.InstallRoot,
            "installRoot",
            fieldSelections,
            path => !string.IsNullOrWhiteSpace(path));
        installRoot = GameIdentity.NormalizePath(installRoot ?? primary.InstallRoot ?? string.Empty);

        var executable = SelectExecutable(orderedSources, installRoot, fieldSelections);
        var prefix = SelectPrefix(orderedSources, fieldSelections);
        var deployment = executable is not null
            ? Path.GetDirectoryName(executable) ?? installRoot
            : installRoot;

        var allDiagnostics = orderedSources.SelectMany(source => source.Diagnostics)
            .Concat(diagnostics.Where(diagnostic =>
                orderedSources.Any(source => source.ExternalId == diagnostic.ExternalId)))
            .DistinctBy(diagnostic => (diagnostic.ProviderId, diagnostic.Code, diagnostic.ExternalId, diagnostic.MetadataPath))
            .ToArray();

        var actionable = orderedSources.Any(source => source.IsActionable && source.Platform == GameBinaryPlatform.Windows);
        var unsupported = actionable
            ? null
            : orderedSources.Select(source => source.UnsupportedReason).FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
              ?? (primary.Platform == GameBinaryPlatform.Linux
                  ? "Native Linux installation is not a Windows mod target."
                  : "Installation is not actionable for the Windows component stack.");

        var requiresConfirmation = orderedSources.Any(source => source.RequiresConfirmation);
        var steamAppId = ResolveSteamAppId(primary);
        var installId = GameIdentity.CreateInstallId(
            primary.Store, primary.Launcher, primary.ExternalId, installRoot, executable);

        fieldSelections.Add(new(
            "primaryLauncher",
            primary.ProviderId,
            $"Selected by launcher priority and metadata quality ({QualityScore(primary)}).",
            DetectionConfidence.High));

        return new InstalledGame(
            installId,
            primary.Name,
            primary.Store,
            primary.Launcher,
            primary.ExternalId,
            steamAppId,
            installRoot,
            executable,
            prefix,
            deployment,
            primary.Platform,
            primary.Environment,
            orderedSources,
            null,
            allDiagnostics,
            executable is null ? DetectionConfidence.None : DetectionConfidence.High,
            GameEngine.Unknown,
            [],
            requiresConfirmation,
            actionable && primary.Platform == GameBinaryPlatform.Windows,
            unsupported,
            executable is null
                ? "No executable was provided by launcher metadata."
                : "Selected from launcher metadata.",
            fieldSelections,
            false);
    }

    private static uint? ResolveSteamAppId(SourceGameRecord primary)
    {
        if (primary.Store != GameStore.Steam)
            return null;

        if (uint.TryParse(primary.ExternalId, out var externalAppId) && externalAppId != 0)
            return externalAppId;

        if (primary.Attributes.TryGetValue("appId", out var raw) &&
            uint.TryParse(raw, out externalAppId) &&
            externalAppId != 0)
            return externalAppId;

        return null;
    }

    private static string? SelectExecutable(
        IReadOnlyList<SourceGameRecord> sources,
        string installRoot,
        List<FieldSelectionEvidence> evidence)
    {
        string? rejected = null;
        foreach (var launcher in LauncherPriority)
        {
            foreach (var source in sources.Where(source => source.Launcher == launcher))
            {
                var candidate = source.ExecutableHint;
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var resolved = GameIdentity.ResolveRelativePath(installRoot, candidate) ?? candidate;
                if (!string.IsNullOrWhiteSpace(installRoot) &&
                    Path.IsPathRooted(resolved) &&
                    !GameIdentity.IsPathInsideRoot(installRoot, resolved) &&
                    source.Attributes.GetValueOrDefault("allowOutsideRoot") != "true")
                {
                    rejected = $"{source.ProviderId}:{resolved}";
                    continue;
                }

                var normalized = GameIdentity.NormalizePath(resolved);
                evidence.Add(new(
                    "executable",
                    source.ProviderId,
                    "Selected from launcher metadata using launcher priority.",
                    DetectionConfidence.High,
                    rejected));
                return normalized;
            }
        }

        var fallback = sources
            .Select(source => source.ExecutableHint)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (fallback is not null)
        {
            evidence.Add(new(
                "executable",
                sources[0].ProviderId,
                "Fallback executable hint with lower confidence.",
                DetectionConfidence.Low,
                rejected));
        }

        return fallback;
    }

    private static string? SelectPrefix(
        IReadOnlyList<SourceGameRecord> sources,
        List<FieldSelectionEvidence> evidence)
    {
        foreach (var launcher in LauncherPriority)
        {
            var match = sources.FirstOrDefault(source =>
                source.Launcher == launcher && !string.IsNullOrWhiteSpace(source.PrefixHint));
            if (match is null) continue;
            var prefix = GameIdentity.NormalizePath(match.PrefixHint!);
            evidence.Add(new(
                "prefix",
                match.ProviderId,
                "Selected prefix from highest-priority launcher metadata.",
                DetectionConfidence.High));
            return prefix;
        }

        return null;
    }

    private static string? SelectField(
        IReadOnlyList<SourceGameRecord> sources,
        Func<SourceGameRecord, string?> selector,
        string field,
        List<FieldSelectionEvidence> evidence,
        Func<string?, bool> accept)
    {
        foreach (var source in sources)
        {
            var value = selector(source);
            if (!accept(value)) continue;
            evidence.Add(new(
                field,
                source.ProviderId,
                $"Selected {field} from ordered source metadata.",
                DetectionConfidence.High));
            return value;
        }

        return null;
    }

    private static int QualityScore(SourceGameRecord source)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(source.ExecutableHint)) score += 40;
        if (!string.IsNullOrWhiteSpace(source.PrefixHint)) score += 20;
        if (!string.IsNullOrWhiteSpace(source.InstallRoot) && Directory.Exists(source.InstallRoot)) score += 20;
        if (source.Platform == GameBinaryPlatform.Windows) score += 10;
        if (source.IsActionable) score += 5;
        if (source.RequiresConfirmation) score -= 10;
        return score;
    }

    private static string NormalizeTitle(string title) =>
        new string(title.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
