namespace RhiLinux.Core;

public sealed record ReconciliationResult(
    IReadOnlyList<InstalledGame> Games,
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    int DuplicateSourceCount,
    int DuplicatePhysicalInstallCount);

public static class GameReconciliation
{
    public const int RulesVersion = 2;

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
        var groups = new List<List<NormalizedRecord>>();
        var groupIndex = new ReconciliationGroupIndex();
        var duplicateSource = 0;
        var duplicatePhysical = 0;

        foreach (var normalized in ordered.Select(NormalizedRecord.From))
        {
            var record = normalized.Record;
            var matched = false;
            if (groupIndex.FindFirst(normalized) is { } index)
            {
                var group = groups[index];
                _ = CanMerge(group, normalized, out var strength);

                if (group.Any(existing =>
                        existing.Record.ProviderId == record.ProviderId &&
                        string.Equals(existing.Record.ExternalId, record.ExternalId, StringComparison.Ordinal) &&
                        string.Equals(existing.Record.MetadataPath, record.MetadataPath, StringComparison.Ordinal)))
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
                }
                else
                {
                    if (strength == EvidenceStrength.Strong)
                    {
                        duplicatePhysical++;
                        diagnostics.Add(new(
                            record.ProviderId,
                            SourceDiagnosticCodes.DuplicatePhysicalInstall,
                            SourceDiagnosticSeverity.Info,
                            $"Merged duplicate physical installation for '{record.Name}'.",
                            $"Evidence: strong identity overlap with {group[0].Record.ProviderId}:{group[0].Record.ExternalId}",
                            record.MetadataPath,
                            record.ExternalId));
                    }

                    group.Add(normalized);
                    groupIndex.Add(index, normalized);
                    matched = true;
                }
            }

            if (!matched)
            {
                groupIndex.Add(groups.Count, normalized);
                groups.Add([normalized]);
            }
        }

        var games = groups
            .Select(group => MergeGroup(group.Select(item => item.Record).ToArray(), diagnostics))
            .ToList();
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

    private sealed record NormalizedRecord(
        SourceGameRecord Record,
        string InstallRoot,
        string Executable,
        string Prefix,
        string Title,
        string ExecutableFileName)
    {
        public static NormalizedRecord From(SourceGameRecord record)
        {
            var executable = GameIdentity.NormalizePath(record.ExecutableHint ?? string.Empty);
            return new(
                record,
                GameIdentity.NormalizePath(record.InstallRoot ?? string.Empty),
                executable,
                GameIdentity.NormalizePath(record.PrefixHint ?? string.Empty),
                string.IsNullOrWhiteSpace(record.Name) ? string.Empty : NormalizeTitle(record.Name),
                executable.Length == 0 ? string.Empty : Path.GetFileName(executable));
        }
    }

    private sealed class ReconciliationGroupIndex
    {
        private readonly Dictionary<string, int> executables = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Root, GameStore Store, string ExternalId), int> storeInstalls = [];
        private readonly Dictionary<(string Root, string ProviderId, string ExternalId), int> sourceInstalls = [];
        private readonly Dictionary<(string Root, GameLauncher Launcher, string ExternalId), int> launcherInstalls = [];

        public int? FindFirst(NormalizedRecord record)
        {
            int? result = null;
            FindEarlier(executables, record.Executable, ref result, record.Executable.Length > 0);
            FindEarlier(
                storeInstalls,
                (record.InstallRoot, record.Record.Store, record.Record.ExternalId),
                ref result,
                record.InstallRoot.Length > 0 && record.Record.Store != GameStore.Unknown &&
                !string.IsNullOrWhiteSpace(record.Record.ExternalId));
            FindEarlier(
                sourceInstalls,
                (record.InstallRoot, record.Record.ProviderId, record.Record.ExternalId),
                ref result,
                record.InstallRoot.Length > 0);
            FindEarlier(
                launcherInstalls,
                (record.InstallRoot, record.Record.Launcher, record.Record.ExternalId),
                ref result,
                record.InstallRoot.Length > 0 && !string.IsNullOrWhiteSpace(record.Record.ExternalId));
            return result;
        }

        public void Add(int group, NormalizedRecord record)
        {
            Register(executables, record.Executable, group, record.Executable.Length > 0);
            Register(
                storeInstalls,
                (record.InstallRoot, record.Record.Store, record.Record.ExternalId),
                group,
                record.InstallRoot.Length > 0 && record.Record.Store != GameStore.Unknown &&
                !string.IsNullOrWhiteSpace(record.Record.ExternalId));
            Register(
                sourceInstalls,
                (record.InstallRoot, record.Record.ProviderId, record.Record.ExternalId),
                group,
                record.InstallRoot.Length > 0);
            Register(
                launcherInstalls,
                (record.InstallRoot, record.Record.Launcher, record.Record.ExternalId),
                group,
                record.InstallRoot.Length > 0 && !string.IsNullOrWhiteSpace(record.Record.ExternalId));
        }

        private static void FindEarlier<TKey>(
            IReadOnlyDictionary<TKey, int> index,
            TKey key,
            ref int? result,
            bool enabled)
            where TKey : notnull
        {
            if (enabled && index.TryGetValue(key, out var group) && (result is null || group < result))
                result = group;
        }

        private static void Register<TKey>(
            IDictionary<TKey, int> index,
            TKey key,
            int group,
            bool enabled)
            where TKey : notnull
        {
            if (!enabled) return;
            if (!index.TryGetValue(key, out var existing) || group < existing)
                index[key] = group;
        }
    }

    private static bool CanMerge(
        IReadOnlyList<NormalizedRecord> group,
        NormalizedRecord candidate,
        out EvidenceStrength strength)
    {
        strength = EvidenceStrength.None;
        foreach (var existing in group)
        {
            var current = ScoreEvidence(existing, candidate);
            if (current > strength)
                strength = current;
            if (strength == EvidenceStrength.Strong) break;
        }

        return strength is EvidenceStrength.Strong or EvidenceStrength.Medium;
    }

    private static EvidenceStrength ScoreEvidence(NormalizedRecord left, NormalizedRecord right)
    {
        var leftRoot = left.InstallRoot;
        var rightRoot = right.InstallRoot;
        var leftExe = left.Executable;
        var rightExe = right.Executable;

        var sameExe = leftExe.Length > 0 && string.Equals(leftExe, rightExe, StringComparison.Ordinal);
        var sameRoot = leftRoot.Length > 0 && string.Equals(leftRoot, rightRoot, StringComparison.Ordinal);
        var sameStoreId = left.Record.Store != GameStore.Unknown &&
                          left.Record.Store == right.Record.Store &&
                          !string.IsNullOrWhiteSpace(left.Record.ExternalId) &&
                          string.Equals(left.Record.ExternalId, right.Record.ExternalId, StringComparison.Ordinal);
        var sameSourceId = string.Equals(left.Record.ProviderId, right.Record.ProviderId, StringComparison.Ordinal) &&
                           string.Equals(left.Record.ExternalId, right.Record.ExternalId, StringComparison.Ordinal);

        if (sameExe || sameRoot && sameStoreId || sameSourceId && sameRoot)
            return EvidenceStrength.Strong;

        var sameLauncherExternal = left.Record.Launcher == right.Record.Launcher &&
                                   !string.IsNullOrWhiteSpace(left.Record.ExternalId) &&
                                   string.Equals(left.Record.ExternalId, right.Record.ExternalId, StringComparison.Ordinal);

        if (sameLauncherExternal && sameRoot)
            return EvidenceStrength.Medium;

        var sameTitle = left.Title.Length > 0 &&
                        string.Equals(left.Title, right.Title, StringComparison.Ordinal);
        var sameFileName = left.ExecutableFileName.Length > 0 && right.ExecutableFileName.Length > 0 &&
                           string.Equals(left.ExecutableFileName, right.ExecutableFileName, StringComparison.OrdinalIgnoreCase);

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
