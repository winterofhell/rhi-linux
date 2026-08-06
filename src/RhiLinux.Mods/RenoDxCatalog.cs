using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum RenoDxCatalogSection
{
    ExactGame,
    GenericUnreal,
    GenericUnity,
    Deprecated,
    Related,
    ManualOnly
}

public enum RenoDxSourceType
{
    Snapshot,
    Nexus,
    Discussion,
    Discord,
    GenericAddon,
    Unknown
}

public enum RenoDxMatchType
{
    ExactAppId,
    BuiltInProfile,
    ExactNormalizedTitle,
    VerifiedAlias,
    ExactExecutable,
    ExactArtifactSlug,
    HighConfidenceTitle,
    EngineFallback,
    Ambiguous,
    NoMatch,
    ManualOnly,
    Superseded
}

public enum RenoDxCompatibilityState
{
    CheckingLocalGameData,
    CheckingCatalog,
    ExactAddonAvailable,
    ExactAddonAvailableFromOfficialSnapshotRelease,
    ExactAddonAvailableFromOfficialDiscussion,
    ExactAddonAvailableForAnotherExecutable,
    GenericUnrealAddonAvailable,
    GenericUnityAddonAvailable,
    ListedManualDownloadRequired,
    OfficialPageAvailableNoDirectAddon,
    DirectAddonFoundNotYetValidated,
    MultipleOfficialFilesRequireConfirmation,
    AddonAvailableArchitectureUnconfirmed,
    AddonAvailableExecutableSelectionRequired,
    AddonArchitectureMismatch,
    InProgress,
    AmbiguousMatch,
    MetadataUnavailable,
    OfflineCatalogInUse,
    OfficialSourceTemporarilyUnavailable,
    OfficialSourceChanged,
    UnsafeArtifactRejected,
    NotListed,
    UnsupportedEngineOrApi,
    AntiCheatOrSafetyRestriction,
    Unsupported,
    SupersededByGenericAddon,
    NoAddonFound
}

public sealed record RenoDxCatalogEntry(
    string CanonicalName,
    string NormalizedCanonicalName,
    IReadOnlyList<string> AlternateNames,
    string? Maintainer,
    Uri? Addon32Url,
    Uri? Addon64Url,
    string? ArtifactFileName,
    string? ArtifactSlug,
    PeArchitecture ArchitectureAvailability,
    RenoDxWikiStatus Status,
    RenoDxCatalogSection SourceSection,
    RenoDxSourceType SourceType,
    bool DirectAutomaticDownloadAvailable,
    string? StatusNote,
    DateTimeOffset? MetadataTimestamp,
    bool OfficialCatalogOrigin,
    string? SupersededBy)
{
    public Uri? OfficialPageUrl { get; init; }
    public Uri? DiscussionUrl { get; init; }
    public string? ExpectedAddonFileName { get; init; }
    public string? DeploymentRelativeDirectory { get; init; }
    public IReadOnlyList<string> CompatibilityWarnings { get; init; } = [];
    public string? MinimumReshadeVersion { get; init; }

    public Uri? GetAddonUri(PeArchitecture architecture) => architecture switch
    {
        PeArchitecture.X86 => Addon32Url,
        PeArchitecture.X64 => Addon64Url,
        _ => null
    };

    public bool HasArchitecture(PeArchitecture architecture) => architecture switch
    {
        PeArchitecture.X86 => Addon32Url is not null,
        PeArchitecture.X64 => Addon64Url is not null,
        _ => false
    };
}

public sealed record RenoDxMatchEvidence(string Kind, string Detail, double Weight);

public sealed record RenoDxMatchCandidate(
    RenoDxCatalogEntry Entry,
    RenoDxMatchType MatchType,
    double Confidence,
    IReadOnlyList<RenoDxMatchEvidence> Evidence);

public sealed record RenoDxMatchResult(
    RenoDxMatchType MatchType,
    double Confidence,
    RenoDxCatalogEntry? Entry,
    ArtifactSelection? Selection,
    RenoDxCompatibilityState Compatibility,
    IReadOnlyList<RenoDxMatchEvidence> Evidence,
    IReadOnlyList<RenoDxMatchCandidate> AmbiguousCandidates,
    string? SuggestedExecutable,
    PeArchitecture SelectedArchitecture,
    PeArchitecture AddonArchitecture,
    string? ReasonCode,
    bool UsedEngineFallback,
    string? RejectedReason)
{
    public static RenoDxMatchResult None(RenoDxCompatibilityState state, string reasonCode, string? detail = null) =>
        new(RenoDxMatchType.NoMatch, 0, null, null, state, detail is null ? [] : [new("reason", detail, 0)],
            [], null, PeArchitecture.Unknown, PeArchitecture.Unknown, reasonCode, false, detail);
}

public sealed class RenoDxCatalogIndex
{
    public const int SchemaVersion = 1;
    private readonly Dictionary<string, RenoDxCatalogEntry> byNormalizedName;
    private readonly Dictionary<string, RenoDxCatalogEntry> byAlias;
    private readonly Dictionary<string, RenoDxCatalogEntry> byExecutable;
    private readonly Dictionary<string, RenoDxCatalogEntry> byArtifactSlug;
    private readonly Dictionary<uint, RenoDxCatalogEntry> byAppId;
    private readonly RenoDxCatalogEntry? unrealFallback;
    private readonly RenoDxCatalogEntry? unityFallback;
    private readonly Dictionary<string, RenoDxCatalogEntry> verifiedUnreal;
    private readonly Dictionary<string, RenoDxCatalogEntry> verifiedUnity;

    public RenoDxCatalogIndex(
        IReadOnlyList<RenoDxCatalogEntry> entries,
        DateTimeOffset? sourceTimestamp,
        string? etag,
        RenoDxWikiFetchState fetchState,
        bool isCached,
        TimeSpan? cacheAge)
    {
        Entries = entries;
        SourceTimestamp = sourceTimestamp;
        ETag = etag;
        FetchState = fetchState;
        IsCached = isCached;
        CacheAge = cacheAge;
        byNormalizedName = new(StringComparer.Ordinal);
        byAlias = new(StringComparer.Ordinal);
        byExecutable = new(StringComparer.OrdinalIgnoreCase);
        byArtifactSlug = new(StringComparer.OrdinalIgnoreCase);
        byAppId = new();
        verifiedUnreal = new(StringComparer.Ordinal);
        verifiedUnity = new(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            switch (entry.SourceSection)
            {
                case RenoDxCatalogSection.GenericUnreal when entry.CanonicalName.Equals("Unreal Engine", StringComparison.OrdinalIgnoreCase):
                    unrealFallback ??= entry;
                    continue;
                case RenoDxCatalogSection.GenericUnity when entry.CanonicalName.Equals("Unity Engine", StringComparison.OrdinalIgnoreCase):
                    unityFallback ??= entry;
                    continue;
                case RenoDxCatalogSection.GenericUnreal:
                    IndexName(verifiedUnreal, entry);
                    continue;
                case RenoDxCatalogSection.GenericUnity:
                    IndexName(verifiedUnity, entry);
                    continue;
                case RenoDxCatalogSection.Related:
                    continue;
                case RenoDxCatalogSection.Deprecated:
                case RenoDxCatalogSection.ExactGame:
                case RenoDxCatalogSection.ManualOnly:
                    break;
            }

            IndexName(byNormalizedName, entry);
            foreach (var form in RenoDxIdentity.BuildForms(entry.CanonicalName))
            {
                var normalized = RenoDxIdentity.NormalizeKey(form);
                if (normalized.Length == 0 || normalized == entry.NormalizedCanonicalName) continue;
                byAlias.TryAdd(normalized, entry);
            }
            foreach (var alias in entry.AlternateNames)
            {
                var normalized = RenoDxIdentity.NormalizeKey(alias);
                if (normalized.Length == 0) continue;
                byAlias.TryAdd(normalized, entry);
            }
            if (entry.ArtifactSlug is { } slug)
                byArtifactSlug.TryAdd(slug, entry);
            if (entry.ArtifactFileName is { } fileName)
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                if (!string.IsNullOrWhiteSpace(stem))
                    byExecutable.TryAdd(stem + ".exe", entry);
            }
        }
    }

    public IReadOnlyList<RenoDxCatalogEntry> Entries { get; }
    public DateTimeOffset? SourceTimestamp { get; }
    public string? ETag { get; }
    public RenoDxWikiFetchState FetchState { get; }
    public bool IsCached { get; }
    public TimeSpan? CacheAge { get; }
    public RenoDxCatalogEntry? UnrealFallback => unrealFallback;
    public RenoDxCatalogEntry? UnityFallback => unityFallback;

    public void AddAppId(uint appId, RenoDxCatalogEntry entry) => byAppId[appId] = entry;
    public void AddAlias(string alias, RenoDxCatalogEntry entry)
    {
        var key = RenoDxIdentity.NormalizeKey(alias);
        if (key.Length > 0) byAlias[key] = entry;
    }
    public void AddExecutable(string executableFileName, RenoDxCatalogEntry entry) =>
        byExecutable[executableFileName] = entry;

    public bool TryGetByAppId(uint appId, out RenoDxCatalogEntry entry) => byAppId.TryGetValue(appId, out entry!);
    public bool TryGetByNormalizedName(string normalized, out RenoDxCatalogEntry entry) =>
        byNormalizedName.TryGetValue(normalized, out entry!);
    public bool TryGetByAlias(string normalized, out RenoDxCatalogEntry entry) =>
        byAlias.TryGetValue(normalized, out entry!);
    public bool TryGetByExecutable(string executableFileName, out RenoDxCatalogEntry entry) =>
        byExecutable.TryGetValue(executableFileName, out entry!);
    public bool TryGetByArtifactSlug(string slug, out RenoDxCatalogEntry entry) =>
        byArtifactSlug.TryGetValue(slug, out entry!);
    public bool IsVerifiedEngineGame(GameEngine engine, string normalizedName) => engine switch
    {
        GameEngine.Unreal => verifiedUnreal.ContainsKey(normalizedName),
        GameEngine.Unity => verifiedUnity.ContainsKey(normalizedName),
        _ => false
    };

    private static void IndexName(Dictionary<string, RenoDxCatalogEntry> map, RenoDxCatalogEntry entry)
    {
        if (entry.NormalizedCanonicalName.Length == 0) return;
        _ = map.TryAdd(entry.NormalizedCanonicalName, entry);
    }
}

public static class RenoDxIdentity
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Punctuation = new(@"[\p{P}\p{S}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DxQualifier = new(
        @"\s*[\(\[]?\s*dx\s*(?:11|12)\s*(?:only)?[\)\]]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingOnly = new(
        @"\s+only\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly char[] TrademarkMarks = ['™', '®', '©'];
    private static readonly (string Roman, string Digit)[] RomanDigits =
    [
        (" iii", " 3"), (" ii", " 2"), (" iv", " 4"), (" vi", " 6"), (" vii", " 7"), (" viii", " 8"),
        (" ix", " 9"), (" xi", " 11"), (" xii", " 12"), (" i", " 1"), (" v", " 5"), (" x", " 10")
    ];

    public static IReadOnlyList<string> BuildForms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var forms = new List<string>();
        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            if (!forms.Contains(candidate, StringComparer.Ordinal)) forms.Add(candidate);
        }

        var original = value.Trim();
        Add(original);
        var withoutMarks = StripMarks(original).Normalize(NormalizationForm.FormC);
        Add(withoutMarks);
        var caseFolded = withoutMarks.ToLowerInvariant();
        Add(caseFolded);
        var apostropheNormalized = caseFolded.Replace('’', '\'').Replace('ʻ', '\'').Replace('ʼ', '\'')
            .Replace("'", string.Empty);
        Add(apostropheNormalized);
        var dashNormalized = apostropheNormalized.Replace('–', '-').Replace('—', '-').Replace('−', '-');
        Add(dashNormalized);
        var colonNormalized = dashNormalized.Replace('：', ':');
        Add(colonNormalized);
        var punctAsSpaces = Punctuation.Replace(colonNormalized, " ");
        Add(Collapse(punctAsSpaces));
        Add(Collapse(colonNormalized));
        Add(NormalizeKey(original));
        var withoutThe = RemoveLeadingThe(NormalizeKey(original));
        Add(withoutThe);
        var withoutDx = Collapse(DxQualifier.Replace(withoutThe, " "));
        withoutDx = Collapse(TrailingOnly.Replace(withoutDx, " "));
        Add(withoutDx);
        Add(ExpandRomanDigits(withoutThe));
        Add(ExpandRomanDigits(withoutDx));
        return forms;
    }

    private static string ExpandRomanDigits(string value)
    {
        var current = $" {value} ";
        foreach (var pair in RomanDigits)
            current = current.Replace(pair.Roman, pair.Digit, StringComparison.Ordinal);
        return Collapse(current);
    }

    public static string NormalizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = StripMarks(value).Normalize(NormalizationForm.FormC).ToLowerInvariant();
        normalized = normalized.Replace('’', '\'').Replace('ʻ', '\'').Replace('ʼ', '\'')
            .Replace("'", string.Empty);
        normalized = normalized.Replace('–', '-').Replace('—', '-').Replace('−', '-').Replace('：', ':');
        normalized = Punctuation.Replace(normalized, " ");
        return Collapse(normalized);
    }

    public static string ArtifactSlug(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase))
            stem = stem["renodx-".Length..];
        return stem.ToLowerInvariant();
    }

    public static bool LooksLikeEditionCollision(string left, string right)
    {
        var a = NormalizeKey(left);
        var b = NormalizeKey(right);
        if (a == b) return false;
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    private static string StripMarks(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (TrademarkMarks.Contains(character)) continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string Collapse(string value) => Whitespace.Replace(value, " ").Trim();

    private static string RemoveLeadingThe(string value) =>
        value.StartsWith("the ", StringComparison.Ordinal) ? value[4..] : value;
}

public sealed class RenoDxGameMatcher
{
    private const double HighConfidenceThreshold = 0.92;
    private const double AmbiguityGap = 0.08;

    public RenoDxMatchResult Resolve(
        SteamGame game,
        GameProfileMatch profile,
        RenoDxCatalogIndex catalog,
        RemoteManifestCatalog? remote) =>
        Resolve(game.ToDeploymentTarget(), profile, catalog, remote);

    public RenoDxMatchResult Resolve(
        DeploymentTarget game,
        GameProfileMatch profile,
        RenoDxCatalogIndex catalog,
        RemoteManifestCatalog? remote)
    {
        if (game.RequiresConfirmation)
            return RenoDxMatchResult.None(RenoDxCompatibilityState.AntiCheatOrSafetyRestriction,
                "anti-cheat", "Anti-cheat files require an explicit compatibility decision.");
        if (game.IsNativeLinux || game.Executable is null ||
            !Path.GetExtension(game.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return RenoDxMatchResult.None(RenoDxCompatibilityState.UnsupportedEngineOrApi,
                "native-or-missing-exe", "No Windows game executable was selected.");

        ApplyRemoteIndexes(catalog, remote);
        var architectureDecision = DecideArchitecture(game, null);
        var identityForms = RenoDxIdentity.BuildForms(game.Name)
            .Concat(game.Executable is null ? [] : RenoDxIdentity.BuildForms(Path.GetFileNameWithoutExtension(game.Executable)))
            .Concat(RenoDxIdentity.BuildForms(Path.GetFileName(game.GameRoot.TrimEnd(Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var normalizedTitle = RenoDxIdentity.NormalizeKey(game.Name);

        if (remote is not null && game.SteamAppId is { } steamAppId)
        {
            var appIdNames = remote.SteamAppIds.Where(entry => entry.Value == steamAppId).Select(entry => entry.Key).ToArray();
            if (appIdNames.Length > 1)
                return Ambiguous("remote-appid-ambiguous",
                    appIdNames.Select(name => CandidateFromName(catalog, name, RenoDxMatchType.ExactAppId, 1.0,
                        new RenoDxMatchEvidence("steam-appid", $"Remote manifest maps AppID {steamAppId} to '{name}'", 1))).Where(x => x is not null).Cast<RenoDxMatchCandidate>().ToArray());
            if (appIdNames.Length == 1 &&
                TryFinish(catalog, appIdNames[0], RenoDxMatchType.ExactAppId, 1.0, game, architectureDecision,
                    [new("steam-appid", $"Remote manifest Steam AppID {steamAppId}", 1)], remote) is { } appIdMatch)
                return appIdMatch;
        }

        if (profile.ExactAppId && profile.Profile.RenoDx is { } builtInExact)
            return FromBuiltIn(game, profile, builtInExact, RenoDxMatchType.BuiltInProfile, architectureDecision,
                [new("built-in-appid", $"Built-in exact AppID profile '{profile.Profile.Id}'", 1)]);

        if (catalog.TryGetByNormalizedName(normalizedTitle, out var exactTitle))
            return FinishEntry(catalog, exactTitle, RenoDxMatchType.ExactNormalizedTitle, 1.0, game, architectureDecision,
                [new("normalized-title", $"Exact normalized Steam title '{game.Name}'", 1)], remote, false);

        foreach (var form in identityForms)
        {
            var key = RenoDxIdentity.NormalizeKey(form);
            if (key.Length == 0) continue;
            if (catalog.TryGetByNormalizedName(key, out var formTitle) &&
                key != normalizedTitle)
                return FinishEntry(catalog, formTitle, RenoDxMatchType.ExactNormalizedTitle, 0.99, game, architectureDecision,
                    [new("normalized-title-form", $"Normalized identity form '{form}' → '{formTitle.CanonicalName}'", 0.99)],
                    remote, false);
            if (catalog.TryGetByAlias(key, out var aliasEntry))
                return FinishEntry(catalog, aliasEntry, RenoDxMatchType.VerifiedAlias, 0.98, game, architectureDecision,
                    [new("alias", $"Verified alias '{form}' → '{aliasEntry.CanonicalName}'", 0.98)], remote, false);
        }

        if (game.Executable is not null)
        {
            var executableName = Path.GetFileName(game.Executable);
            if (remote is not null)
            {
                var executableMatches = remote.LaunchExecutables
                    .Where(entry => entry.Value.Equals(executableName, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (executableMatches.Length > 1)
                    return Ambiguous("remote-executable-ambiguous",
                        executableMatches.Select(name => CandidateFromName(catalog, name, RenoDxMatchType.ExactExecutable, 0.97,
                            new RenoDxMatchEvidence("executable", $"Remote executable '{executableName}'", 0.97)))
                            .Where(x => x is not null).Cast<RenoDxMatchCandidate>().ToArray());
                if (executableMatches.Length == 1 &&
                    TryFinish(catalog, executableMatches[0], RenoDxMatchType.ExactExecutable, 0.97, game, architectureDecision,
                        [new("executable", $"Remote manifest executable '{executableName}'", 0.97)], remote) is { } exeMatch)
                    return exeMatch;
            }
            if (catalog.TryGetByExecutable(executableName, out var byExe))
                return FinishEntry(catalog, byExe, RenoDxMatchType.ExactExecutable, 0.97, game, architectureDecision,
                    [new("executable", $"Executable '{executableName}'", 0.97)], remote, false);

            var slug = RenoDxIdentity.ArtifactSlug(Path.GetFileNameWithoutExtension(executableName));
            if (slug.Length > 0 && catalog.TryGetByArtifactSlug(slug, out var bySlug))
                return FinishEntry(catalog, bySlug, RenoDxMatchType.ExactArtifactSlug, 0.95, game, architectureDecision,
                    [new("artifact-slug", $"Artifact slug '{slug}'", 0.95)], remote, false);
        }

        var fuzzy = FindHighConfidenceTitles(catalog, identityForms, normalizedTitle);
        if (fuzzy.Count > 1 && fuzzy[0].Confidence - fuzzy[1].Confidence < AmbiguityGap)
            return Ambiguous("title-ambiguous", fuzzy.Take(5).ToArray());
        if (fuzzy.Count >= 1 && fuzzy[0].Confidence >= HighConfidenceThreshold &&
            (fuzzy.Count == 1 || fuzzy[0].Confidence - fuzzy[1].Confidence >= AmbiguityGap) &&
            HasSupportingSignal(fuzzy[0], game))
            return FinishEntry(catalog, fuzzy[0].Entry, RenoDxMatchType.HighConfidenceTitle, fuzzy[0].Confidence, game,
                architectureDecision, fuzzy[0].Evidence, remote, false);
        if (fuzzy.Count >= 1 && fuzzy[0].Confidence >= HighConfidenceThreshold)
            return Ambiguous("title-needs-confirmation", fuzzy.Take(5).ToArray());

        if (profile.IsExact && profile.Profile.RenoDx is { } builtInSource)
            return FromBuiltIn(game, profile, builtInSource, RenoDxMatchType.BuiltInProfile, architectureDecision,
                [new("built-in-profile", profile.MatchReason, 0.9)]);

        var engineFallback = TryEngineFallback(game, profile, catalog, architectureDecision, remote);
        if (engineFallback is not null) return engineFallback;

        if (catalog.FetchState == RenoDxWikiFetchState.UnableToCheck && catalog.Entries.Count == 0)
            return RenoDxMatchResult.None(RenoDxCompatibilityState.MetadataUnavailable, "catalog-unavailable",
                "RenoDX catalog unavailable");
        if (catalog.FetchState == RenoDxWikiFetchState.Offline && catalog.Entries.Count == 0)
            return RenoDxMatchResult.None(RenoDxCompatibilityState.MetadataUnavailable, "catalog-unavailable",
                "RenoDX catalog unavailable");

        return RenoDxMatchResult.None(
            catalog.FetchState == RenoDxWikiFetchState.Offline
                ? RenoDxCompatibilityState.OfflineCatalogInUse
                : RenoDxCompatibilityState.NotListed,
            "not-listed",
            "No RenoDX addon found");
    }

    private static RenoDxMatchResult? TryEngineFallback(
        DeploymentTarget game,
        GameProfileMatch profile,
        RenoDxCatalogIndex catalog,
        ArchitectureDecision architectureDecision,
        RemoteManifestCatalog? remote)
    {
        if (game.Engine is GameEngine.UnrealLegacy)
            return RenoDxMatchResult.None(RenoDxCompatibilityState.UnsupportedEngineOrApi, "unreal-legacy",
                "Legacy Unreal Engine versions do not support the official generic RenoDX addon");

        RenoDxCatalogEntry? entry = game.Engine switch
        {
            GameEngine.Unreal => catalog.UnrealFallback,
            GameEngine.Unity => catalog.UnityFallback,
            _ => null
        };
        if (entry is null && profile.IsFallback && profile.Profile.RenoDx is { } builtInFallback)
            return FromBuiltIn(game, profile, builtInFallback, RenoDxMatchType.EngineFallback, architectureDecision,
                [new("engine-fallback", profile.MatchReason, 0.7)], true);
        if (entry is null) return null;

        var verified = catalog.IsVerifiedEngineGame(game.Engine, RenoDxIdentity.NormalizeKey(game.Name));
        var evidence = new List<RenoDxMatchEvidence>
        {
            new("engine", $"Detected engine {game.Engine}", 0.7),
            new("generic-addon", $"Generic {game.Engine} RenoDX addon", 0.7)
        };
        if (verified) evidence.Add(new("verified-list", $"Listed under generic {game.Engine} verified games", 0.05));
        return FinishEntry(catalog, entry, RenoDxMatchType.EngineFallback, verified ? 0.75 : 0.7, game, architectureDecision,
            evidence, remote, true);
    }

    private static bool HasSupportingSignal(RenoDxMatchCandidate candidate, DeploymentTarget game)
    {
        var executableStem = game.Executable is null ? null : Path.GetFileNameWithoutExtension(game.Executable);
        var directory = Path.GetFileName(game.GameRoot.TrimEnd(Path.DirectorySeparatorChar));
        var slug = candidate.Entry.ArtifactSlug;
        if (slug is not null && executableStem is not null &&
            RenoDxIdentity.NormalizeKey(executableStem).Contains(RenoDxIdentity.NormalizeKey(slug), StringComparison.Ordinal))
            return true;
        if (slug is not null && RenoDxIdentity.NormalizeKey(directory).Contains(RenoDxIdentity.NormalizeKey(slug), StringComparison.Ordinal))
            return true;
        return candidate.Evidence.Any(item => item.Kind is "alias" or "executable" or "artifact-slug");
    }

    private static List<RenoDxMatchCandidate> FindHighConfidenceTitles(
        RenoDxCatalogIndex catalog,
        IReadOnlyList<string> identityForms,
        string normalizedTitle)
    {
        var results = new List<RenoDxMatchCandidate>();
        foreach (var entry in catalog.Entries.Where(item =>
                     item.SourceSection is RenoDxCatalogSection.ExactGame or RenoDxCatalogSection.ManualOnly
                         or RenoDxCatalogSection.Deprecated))
        {
            var entryKey = entry.NormalizedCanonicalName;
            if (entryKey.Length == 0) continue;
            double best = 0;
            string? bestForm = null;
            foreach (var form in identityForms.Append(normalizedTitle).Distinct(StringComparer.Ordinal))
            {
                var key = RenoDxIdentity.NormalizeKey(form);
                if (key.Length == 0) continue;
                if (key == entryKey) { best = 1; bestForm = form; break; }
                var score = TokenOverlap(key, entryKey);
                if (score > best) { best = score; bestForm = form; }
            }
            if (best < 0.85) continue;
            if (RenoDxIdentity.LooksLikeEditionCollision(normalizedTitle, entry.CanonicalName) && best < 0.98)
                continue;
            results.Add(new(entry, RenoDxMatchType.HighConfidenceTitle, best,
                [new("title-similarity", $"'{bestForm}' ≈ '{entry.CanonicalName}' ({best:0.00})", best)]));
        }
        return results.OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Entry.CanonicalName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static double TokenOverlap(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0) return 0;
        var shared = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        if (union == 0) return 0;
        var jaccard = (double)shared / union;
        if (left.Equals(right, StringComparison.Ordinal)) return 1;
        if (left.StartsWith(right + " ", StringComparison.Ordinal) || right.StartsWith(left + " ", StringComparison.Ordinal))
            return Math.Max(jaccard, 0.9);
        return jaccard;
    }

    private static RenoDxMatchResult Ambiguous(string reasonCode, IReadOnlyList<RenoDxMatchCandidate> candidates) =>
        new(RenoDxMatchType.Ambiguous, candidates.FirstOrDefault()?.Confidence ?? 0, null, null,
            RenoDxCompatibilityState.AmbiguousMatch, candidates.SelectMany(item => item.Evidence).ToArray(),
            candidates, null, PeArchitecture.Unknown, PeArchitecture.Unknown, reasonCode, false,
            "Multiple RenoDX catalog candidates require confirmation.");

    private static RenoDxMatchCandidate? CandidateFromName(
        RenoDxCatalogIndex catalog,
        string name,
        RenoDxMatchType type,
        double confidence,
        RenoDxMatchEvidence evidence)
    {
        var key = RenoDxIdentity.NormalizeKey(name);
        if (!catalog.TryGetByNormalizedName(key, out var entry) && !catalog.TryGetByAlias(key, out entry))
            return null;
        return new(entry, type, confidence, [evidence]);
    }

    private static RenoDxMatchResult? TryFinish(
        RenoDxCatalogIndex catalog,
        string canonicalName,
        RenoDxMatchType type,
        double confidence,
        DeploymentTarget game,
        ArchitectureDecision architectureDecision,
        IReadOnlyList<RenoDxMatchEvidence> evidence,
        RemoteManifestCatalog? remote)
    {
        var key = RenoDxIdentity.NormalizeKey(canonicalName);
        if (!catalog.TryGetByNormalizedName(key, out var entry) && !catalog.TryGetByAlias(key, out entry))
            return null;
        return FinishEntry(catalog, entry, type, confidence, game, architectureDecision, evidence, remote, false);
    }

    private static RenoDxMatchResult FinishEntry(
        RenoDxCatalogIndex catalog,
        RenoDxCatalogEntry entry,
        RenoDxMatchType type,
        double confidence,
        DeploymentTarget game,
        ArchitectureDecision architectureDecision,
        IReadOnlyList<RenoDxMatchEvidence> evidence,
        RemoteManifestCatalog? remote,
        bool usedEngineFallback)
    {
        if (entry.SourceSection == RenoDxCatalogSection.Related)
            return RenoDxMatchResult.None(RenoDxCompatibilityState.NotListed, "related-mod",
                "Related non-RenoDX mods are not installed automatically.");

        if (entry.SourceSection == RenoDxCatalogSection.Deprecated)
        {
            if (!string.IsNullOrWhiteSpace(entry.SupersededBy))
            {
                var wantsUnity = entry.SupersededBy.Contains("unity", StringComparison.OrdinalIgnoreCase);
                var wantsUnreal = entry.SupersededBy.Contains("unreal", StringComparison.OrdinalIgnoreCase);
                var fallbackEngine = wantsUnity ? GameEngine.Unity : wantsUnreal ? GameEngine.Unreal : game.Engine;
                var fallback = fallbackEngine == GameEngine.Unity ? catalog.UnityFallback : catalog.UnrealFallback;
                if (fallback is not null && fallback.DirectAutomaticDownloadAvailable)
                {
                    var supersededEvidence = evidence.Concat(
                    [
                        new RenoDxMatchEvidence("superseded", entry.StatusNote ?? $"Superseded by {entry.SupersededBy}", 0.2)
                    ]).ToArray();
                    return FinishEntry(catalog, fallback, RenoDxMatchType.EngineFallback, Math.Min(confidence, 0.8), game,
                        architectureDecision, supersededEvidence, remote, true) with
                    {
                        Compatibility = fallbackEngine == GameEngine.Unity
                            ? RenoDxCompatibilityState.GenericUnityAddonAvailable
                            : RenoDxCompatibilityState.GenericUnrealAddonAvailable,
                        Entry = entry,
                        ReasonCode = "superseded-generic",
                        RejectedReason = entry.StatusNote ?? $"Superseded by {entry.SupersededBy}"
                    };
                }
                return new(RenoDxMatchType.Superseded, confidence, entry, null,
                    RenoDxCompatibilityState.SupersededByGenericAddon, evidence, [], null,
                    architectureDecision.Selected, PeArchitecture.Unknown, "superseded", false,
                    entry.StatusNote ?? $"Superseded by {entry.SupersededBy}");
            }
            return RenoDxMatchResult.None(RenoDxCompatibilityState.Unsupported, "deprecated",
                entry.StatusNote ?? "Deprecated RenoDX entry is not installed automatically.");
        }

        if (entry.SourceSection == RenoDxCatalogSection.ManualOnly || !entry.DirectAutomaticDownloadAvailable)
            return new(RenoDxMatchType.ManualOnly, confidence, entry, null,
                RenoDxCompatibilityState.ListedManualDownloadRequired, evidence, [], null,
                architectureDecision.Selected, PeArchitecture.Unknown, "manual-only", usedEngineFallback,
                "Listed by RenoDX, manual download required");

        var decision = DecideArchitecture(game, entry);
        if (decision.Selected is not PeArchitecture.X86 and not PeArchitecture.X64)
            return new(type, confidence, entry, null, RenoDxCompatibilityState.AddonAvailableArchitectureUnconfirmed,
                evidence, [], decision.SuggestedExecutable, decision.Selected, PeArchitecture.Unknown,
                "architecture-unconfirmed", usedEngineFallback, "Architecture could not be confirmed");

        Uri? source = entry.GetAddonUri(decision.Selected);
        var addonArchitecture = decision.Selected;
        if (source is null && decision.SuggestedExecutable is not null && decision.SuggestedArchitecture is { } suggestedArch)
        {
            source = entry.GetAddonUri(suggestedArch);
            addonArchitecture = suggestedArch;
            if (source is not null)
                return BuildSelection(entry, type, confidence, game, evidence, remote, usedEngineFallback, source,
                    addonArchitecture, decision.SuggestedExecutable,
                    RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable,
                    "addon-other-executable",
                    "Exact addon available for another executable");
        }
        if (source is null && entry.Addon64Url is not null && decision.HasX64Candidate)
            return BuildSelection(entry, type, confidence, game, evidence, remote, usedEngineFallback, entry.Addon64Url,
                PeArchitecture.X64, decision.BestX64Executable,
                RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable,
                "addon-other-executable",
                "Exact addon available for another executable");
        if (source is null && entry.Addon32Url is not null && decision.HasX86Candidate)
            return BuildSelection(entry, type, confidence, game, evidence, remote, usedEngineFallback, entry.Addon32Url,
                PeArchitecture.X86, decision.BestX86Executable,
                RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable,
                "addon-other-executable",
                "Exact addon available for another executable");
        if (source is null)
            return new(type, confidence, entry, null, RenoDxCompatibilityState.AddonAvailableExecutableSelectionRequired,
                evidence, [], null, decision.Selected, entry.ArchitectureAvailability, "arch-mismatch",
                usedEngineFallback, "Addon found, but no compatible executable was selected");

        if (!OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(source))
            return new(type, confidence, entry, null, RenoDxCompatibilityState.MetadataUnavailable, evidence, [], null,
                decision.Selected, addonArchitecture, "source-rejected", usedEngineFallback,
                $"The exact RenoDX mapping for '{entry.CanonicalName}' has an unsupported source URL.");

        if (remote?.AddonOverrides.TryGetValue(entry.CanonicalName, out var manifestOverride) == true)
        {
            if (!OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(manifestOverride))
                return new(type, confidence, entry, null, RenoDxCompatibilityState.MetadataUnavailable, evidence, [], null,
                    decision.Selected, addonArchitecture, "manifest-source-rejected", usedEngineFallback,
                    $"The remote manifest override for '{entry.CanonicalName}' has an unsupported source URL.");
            source = manifestOverride;
            addonArchitecture = Path.GetExtension(source.AbsolutePath).Equals(".addon32", StringComparison.OrdinalIgnoreCase)
                ? PeArchitecture.X86 : PeArchitecture.X64;
        }

        var compatibility = usedEngineFallback
            ? game.Engine == GameEngine.Unity
                ? RenoDxCompatibilityState.GenericUnityAddonAvailable
                : RenoDxCompatibilityState.GenericUnrealAddonAvailable
            : entry.Status == RenoDxWikiStatus.InProgress
                ? RenoDxCompatibilityState.InProgress
                : RenoDxCompatibilityState.ExactAddonAvailable;

        return BuildSelection(entry, type, confidence, game, evidence, remote, usedEngineFallback, source,
            addonArchitecture, null, compatibility, usedEngineFallback ? "engine-fallback" : "exact-addon", null);
    }

    private static RenoDxMatchResult FromBuiltIn(
        DeploymentTarget game,
        GameProfileMatch profile,
        RenoDxSource source,
        RenoDxMatchType type,
        ArchitectureDecision architectureDecision,
        IReadOnlyList<RenoDxMatchEvidence> evidence,
        bool usedEngineFallback = false)
    {
        if (!OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(source.Url) &&
            !OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(source.Url))
            return RenoDxMatchResult.None(RenoDxCompatibilityState.MetadataUnavailable, "built-in-source-rejected",
                "Built-in RenoDX source URL failed safety checks.");
        if (source.Architecture != architectureDecision.Selected &&
            architectureDecision.SuggestedArchitecture != source.Architecture)
        {
            if (source.Architecture == PeArchitecture.X64 && architectureDecision.BestX64Executable is { } x64)
                return BuildBuiltIn(game, profile, source, type, evidence, usedEngineFallback, x64,
                    RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable);
            return new(type, 1, null, null, RenoDxCompatibilityState.AddonAvailableExecutableSelectionRequired,
                evidence, [], null, architectureDecision.Selected, source.Architecture, "built-in-arch-mismatch",
                usedEngineFallback, "Addon found, but no compatible executable was selected");
        }
        var compatibility = usedEngineFallback
            ? profile.Profile.Engine == GameEngine.Unity
                ? RenoDxCompatibilityState.GenericUnityAddonAvailable
                : RenoDxCompatibilityState.GenericUnrealAddonAvailable
            : RenoDxCompatibilityState.ExactAddonAvailable;
        return BuildBuiltIn(game, profile, source, type, evidence, usedEngineFallback, null, compatibility);
    }

    private static RenoDxMatchResult BuildBuiltIn(
        DeploymentTarget game,
        GameProfileMatch profile,
        RenoDxSource source,
        RenoDxMatchType type,
        IReadOnlyList<RenoDxMatchEvidence> evidence,
        bool usedEngineFallback,
        string? suggestedExecutable,
        RenoDxCompatibilityState compatibility)
    {
        var support = usedEngineFallback
            ? profile.Profile.Engine == GameEngine.Unity ? ArtifactSupportKind.UnityFallback : ArtifactSupportKind.UnrealFallback
            : type == RenoDxMatchType.BuiltInProfile && profile.ExactAppId
                ? ArtifactSupportKind.ExactGameProfile
                : ArtifactSupportKind.ExecutableOrAliasProfile;
        var selection = new ArtifactSelection(ComponentKind.RenoDx, source.Version, source.Url, source.Version,
            Path.GetFileName(source.Url.LocalPath), source.Architecture,
            source.IsGameSpecific && profile.ExactAppId ? game.SteamAppId : null,
            source.FileName, null, ArtifactArchiveKind.None, GameProfile: profile.Profile.Id, Support: support,
            SourceValidatedByOfficialMetadata: true);
        return new(type, 1, null, selection, compatibility, evidence, [], suggestedExecutable, source.Architecture,
            source.Architecture, usedEngineFallback ? "engine-fallback" : "built-in", usedEngineFallback, null);
    }

    private static RenoDxMatchResult BuildSelection(
        RenoDxCatalogEntry entry,
        RenoDxMatchType type,
        double confidence,
        DeploymentTarget game,
        IReadOnlyList<RenoDxMatchEvidence> evidence,
        RemoteManifestCatalog? remote,
        bool usedEngineFallback,
        Uri source,
        PeArchitecture addonArchitecture,
        string? suggestedExecutable,
        RenoDxCompatibilityState compatibility,
        string reasonCode,
        string? rejectedReason)
    {
        var fileName = Path.GetFileName(Uri.UnescapeDataString(source.AbsolutePath));
        var genericEngine = GenericEngineFor(fileName);
        if (genericEngine is { } requiredEngine && requiredEngine != game.Engine && game.Engine is not GameEngine.Unknown)
            return new(type, confidence, entry, null, RenoDxCompatibilityState.UnsupportedEngineOrApi, evidence, [],
                null, addonArchitecture, addonArchitecture, "engine-mismatch",
                usedEngineFallback, $"The mapping for '{entry.CanonicalName}' is a generic {requiredEngine} addon, but the detected engine is {game.Engine}.");
        var effectiveEngineFallback = usedEngineFallback || genericEngine is not null;
        var support = effectiveEngineFallback
            ? (genericEngine ?? game.Engine) == GameEngine.Unity
                ? ArtifactSupportKind.UnityFallback
                : ArtifactSupportKind.UnrealFallback
            : type is RenoDxMatchType.ExactAppId or RenoDxMatchType.BuiltInProfile
                ? ArtifactSupportKind.ExactGameProfile
                : ArtifactSupportKind.ExecutableOrAliasProfile;
        var compatibilityFinal = effectiveEngineFallback
            ? (genericEngine ?? game.Engine) == GameEngine.Unity
                ? RenoDxCompatibilityState.GenericUnityAddonAvailable
                : RenoDxCompatibilityState.GenericUnrealAddonAvailable
            : compatibility;
        var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot", source, "snapshot", fileName,
            addonArchitecture, effectiveEngineFallback ? null : game.SteamAppId, fileName, null,
            ArtifactArchiveKind.None, GameProfile: RenoDxIdentity.NormalizeKey(entry.CanonicalName), Support: support,
            SourceValidatedByOfficialMetadata: true);
        return new(effectiveEngineFallback ? RenoDxMatchType.EngineFallback : type, confidence, entry, selection,
            compatibilityFinal, evidence, [], suggestedExecutable, addonArchitecture, addonArchitecture,
            effectiveEngineFallback ? "engine-fallback" : reasonCode, effectiveEngineFallback, rejectedReason);
    }

    private static GameEngine? GenericEngineFor(string fileName)
    {
        if (fileName.Contains("unityengine", StringComparison.OrdinalIgnoreCase)) return GameEngine.Unity;
        if (fileName.Contains("unrealengine", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("ue-extended", StringComparison.OrdinalIgnoreCase)) return GameEngine.Unreal;
        return null;
    }

    private static void ApplyRemoteIndexes(RenoDxCatalogIndex catalog, RemoteManifestCatalog? remote)
    {
        if (remote is null) return;
        foreach (var pair in remote.SteamAppIds)
        {
            if (catalog.TryGetByNormalizedName(RenoDxIdentity.NormalizeKey(pair.Key), out var entry))
                catalog.AddAppId(pair.Value, entry);
        }
        foreach (var pair in remote.Aliases)
        {
            if (catalog.TryGetByNormalizedName(RenoDxIdentity.NormalizeKey(pair.Value), out var entry))
                catalog.AddAlias(pair.Key, entry);
        }
        foreach (var pair in remote.LaunchExecutables)
        {
            if (catalog.TryGetByNormalizedName(RenoDxIdentity.NormalizeKey(pair.Key), out var entry))
                catalog.AddExecutable(pair.Value, entry);
        }
    }

    private static ArchitectureDecision DecideArchitecture(DeploymentTarget game, RenoDxCatalogEntry? entry)
    {
        var selected = game.Candidates.FirstOrDefault(item => game.Executable is not null &&
            Path.GetFullPath(item.Path).Equals(Path.GetFullPath(game.Executable), StringComparison.Ordinal))
            ?? game.Candidates.FirstOrDefault();
        var selectedArch = selected?.Architecture ?? PeArchitecture.Unknown;
        if (selectedArch == PeArchitecture.Unknown && game.Executable is not null && File.Exists(game.Executable))
        {
            try { selectedArch = ArtifactValidator.ValidatePe(game.Executable); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                selectedArch = PeArchitecture.Unknown;
            }
        }

        var gameCandidates = game.Candidates
            .Where(item => item.Confidence is DetectionConfidence.High or DetectionConfidence.Medium)
            .Where(item => !LooksLikeUtility(item.Path))
            .ToArray();
        var bestX64 = gameCandidates.Where(item => item.Architecture == PeArchitecture.X64)
            .OrderByDescending(item => item.Score).Select(item => item.Path).FirstOrDefault();
        var bestX86 = gameCandidates.Where(item => item.Architecture == PeArchitecture.X86)
            .OrderByDescending(item => item.Score).Select(item => item.Path).FirstOrDefault();

        string? suggested = null;
        PeArchitecture? suggestedArch = null;
        if (entry is not null && !entry.HasArchitecture(selectedArch))
        {
            if (entry.HasArchitecture(PeArchitecture.X64) && bestX64 is not null)
            {
                suggested = bestX64;
                suggestedArch = PeArchitecture.X64;
            }
            else if (entry.HasArchitecture(PeArchitecture.X86) && bestX86 is not null)
            {
                suggested = bestX86;
                suggestedArch = PeArchitecture.X86;
            }
        }

        return new(selectedArch, suggested, suggestedArch, bestX64, bestX86, bestX64 is not null, bestX86 is not null);
    }

    private static bool LooksLikeUtility(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name.Contains("crash", StringComparison.Ordinal) ||
               name.Contains("editor", StringComparison.Ordinal) ||
               name.Contains("launcher", StringComparison.Ordinal) ||
               name.Contains("unins", StringComparison.Ordinal) ||
               name.Contains("redist", StringComparison.Ordinal) ||
               name.Contains("helper", StringComparison.Ordinal);
    }

    private readonly record struct ArchitectureDecision(
        PeArchitecture Selected,
        string? SuggestedExecutable,
        PeArchitecture? SuggestedArchitecture,
        string? BestX64Executable,
        string? BestX86Executable,
        bool HasX64Candidate,
        bool HasX86Candidate);
}
