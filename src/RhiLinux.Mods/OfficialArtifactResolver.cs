using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum MetadataCheckState { Online, Offline, UnableToCheck }

public sealed record ResolvedArtifact(
    ComponentKind Component,
    string? Version,
    Uri? OfficialSource,
    string? ReleaseTag,
    string? AssetFileName,
    PeArchitecture Architecture,
    string? GameProfile,
    ArtifactSupportKind Support,
    ArtifactCacheState CacheState,
    ArtifactValidationState Validation,
    string? Sha256,
    string? UnavailableReason,
    ArtifactSelection? Selection);

public sealed record GameArtifactResolution(
    GameProfileMatch Profile,
    IReadOnlyList<ArtifactSelection> Artifacts,
    IReadOnlyList<string> Warnings,
    bool IsFullyAutomatic,
    MetadataCheckState MetadataState = MetadataCheckState.Offline)
{
    public IReadOnlyList<ResolvedArtifact> Components { get; init; } = [];
    public RemoteManifestCheck? RemoteManifest { get; init; }
    public bool CanAcquireRenoSetup { get; init; }
    public bool CanAcquireRenoDx { get; init; }
    public bool CanAcquireOptiScaler { get; init; }
    public RenoDxMatchResult? RenoDxMatch { get; init; }
    public RenoDxCompatibilityState RenoDxCompatibility { get; init; } = RenoDxCompatibilityState.CheckingCatalog;
    public TimeSpan? RenoDxCatalogAge { get; init; }
    public string? SuggestedExecutable { get; init; }
    public Uri? OfficialPageUrl { get; init; }
    public bool CanOpenOfficialPage { get; init; }
    public string? ExpectedAddonFileName { get; init; }
    public string? RecommendedDeploymentRelativeDirectory { get; init; }
    public IReadOnlyList<string> CompatibilityNotes { get; init; } = [];
    public RenoDxSnapshotResolution? SnapshotResolution { get; init; }
}

public sealed class OfficialArtifactResolver(HttpClient httpClient, XdgPaths paths, GameProfileCatalog? catalog = null)
{
    private static readonly Regex ReShadeLink = new(
        @"/downloads/ReShade_Setup_([0-9]+(?:\.[0-9]+)+)_Addon\.exe",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly GameProfileCatalog catalog = catalog ?? new GameProfileCatalog(paths);
    private readonly ArtifactCacheService cache = new(paths);
    private readonly GitHubReleaseClient releases = new(httpClient, paths);
    private readonly RemoteManifestClient manifest = new(httpClient, paths);
    private readonly RenoDxWikiClient wiki = new(httpClient, paths);
    private readonly RenoDxGameMatcher renoMatcher = new();
    private readonly RenoDxDiscussionArtifactResolver discussions = new(httpClient, paths);
    private readonly RenoDxSnapshotReleaseResolver snapshots = new(httpClient, paths);

    public async Task<GameArtifactResolution> ResolveAsync(
        SteamGame game,
        bool allowNetwork = true,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var profile = await catalog.MatchAsync(game, cancellationToken);
        var warnings = profile.Profile.Warnings
            .Where(item => !item.Contains("No supported game profile", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selections = new List<ArtifactSelection>();

        var metadataChecked = allowNetwork;
        var manifestTask = manifest.CheckAsync(allowNetwork, cancellationToken);
        var wikiTask = wiki.GetAsync(allowNetwork, cancellationToken, forceRefresh);
        Task<(ArtifactSelection? Selection, bool Checked)> reshadeTask = allowNetwork
            ? TryResolveReShadeAsync(game, cancellationToken)
            : Task.FromResult<(ArtifactSelection? Selection, bool Checked)>((null, false));

        await Task.WhenAll(manifestTask, wikiTask, reshadeTask);
        var manifestCheck = await manifestTask;
        metadataChecked &= !allowNetwork || manifestCheck.State == MetadataCheckState.Online;
        var manifestCatalog = await manifest.ReadCatalogAsync(cancellationToken);
        var wikiCatalog = await wikiTask;
        if (allowNetwork) metadataChecked &= wikiCatalog.State == RenoDxWikiFetchState.Online;
        if (wikiCatalog.Warning is not null) warnings.Add(wikiCatalog.Warning);

        var reshadeAttempt = await reshadeTask;
        metadataChecked &= reshadeAttempt.Checked;
        var reshade = reshadeAttempt.Selection;
        reshade ??= await FindCachedAsync(ComponentKind.ReShade, game.AppId, cancellationToken);
        if (reshade is not null) selections.Add(await cache.InspectAsync(reshade, cancellationToken));
        else warnings.Add("The official full-addon ReShade release could not be resolved and no valid cached build is available.");

        var renoResolution = ResolveRenoDx(game, profile, manifestCatalog, wikiCatalog);
        renoResolution = await EnrichWithSnapshotAsync(game, renoResolution, allowNetwork, forceRefresh, cancellationToken);
        if (renoResolution.Selection is null ||
            renoResolution.Match.Compatibility is RenoDxCompatibilityState.ListedManualDownloadRequired)
            renoResolution = await EnrichWithDiscussionAsync(game, renoResolution, allowNetwork, forceRefresh, cancellationToken);
        profile = renoResolution.Profile;
        if (renoResolution.Warning is not null) warnings.Add(renoResolution.Warning);
        foreach (var note in renoResolution.CompatibilityNotes)
            if (!warnings.Contains(note, StringComparer.OrdinalIgnoreCase))
                warnings.Add(note);
        if (renoResolution.Selection is { } renoSelection)
        {
            var inspected = await cache.InspectAsync(renoSelection, cancellationToken);
            if (allowNetwork)
            {
                var snapshotCheck = await CheckSnapshotAsync(inspected, cancellationToken);
                inspected = snapshotCheck.Selection;
                metadataChecked &= snapshotCheck.Checked;
            }
            selections.Add(inspected);
        }
        else if (renoResolution.Match.Compatibility is RenoDxCompatibilityState.NotListed
                 or RenoDxCompatibilityState.NoAddonFound)
            warnings.Add("No RenoDX addon found for this game.");
        else if (renoResolution.Match.RejectedReason is { } rejected)
            warnings.Add(rejected);

        var optiTechnicallyEligible = IsTechnicallyEligibleForOptiScaler(game);
        if (optiTechnicallyEligible)
        {
            var optiAttempt = allowNetwork
                ? await TryResolveGitHubAsync(ComponentKind.OptiScaler, game.AppId, cancellationToken, forceRefresh)
                : ((ArtifactSelection?)null, false);
            metadataChecked &= optiAttempt.Item2;
            var opti = optiAttempt.Item1;
            opti ??= await FindCachedAsync(ComponentKind.OptiScaler, game.AppId, cancellationToken);
            if (opti is not null) selections.Add(await cache.InspectAsync(opti, cancellationToken));
            else warnings.Add("The official OptiScaler release could not be resolved and no valid cached release is available.");
        }
        var canAcquireRenoDx = selections.Any(x => x.Component == ComponentKind.RenoDx &&
            x.Support != ArtifactSupportKind.Unavailable);
        var canAcquireReno = canAcquireRenoDx && selections.Any(x => x.Component == ComponentKind.ReShade);
        var canAcquireOpti = optiTechnicallyEligible && selections.Any(x => x.Component == ComponentKind.OptiScaler);
        var canAcquireReshade = selections.Any(x => x.Component == ComponentKind.ReShade &&
            x.Support != ArtifactSupportKind.Unavailable);
        var automatic = canAcquireReno || canAcquireOpti || canAcquireRenoDx || canAcquireReshade;
        var metadataState = !allowNetwork ? MetadataCheckState.Offline : metadataChecked ? MetadataCheckState.Online : MetadataCheckState.UnableToCheck;
        var officialPage = renoResolution.OfficialPageUrl ?? renoResolution.Match.Entry?.OfficialPageUrl ??
            renoResolution.Match.Entry?.DiscussionUrl;
        var result = new GameArtifactResolution(profile, selections, warnings, automatic, metadataState);
        return result with
        {
            Components = BuildComponents(profile, selections, warnings, renoResolution.Match),
            RemoteManifest = manifestCheck,
            CanAcquireRenoSetup = canAcquireReno,
            CanAcquireRenoDx = canAcquireRenoDx,
            CanAcquireOptiScaler = canAcquireOpti,
            RenoDxMatch = renoResolution.Match,
            RenoDxCompatibility = renoResolution.Match.Compatibility,
            RenoDxCatalogAge = wikiCatalog.CacheAge,
            SuggestedExecutable = renoResolution.Match.SuggestedExecutable,
            OfficialPageUrl = officialPage,
            CanOpenOfficialPage = officialPage is not null,
            ExpectedAddonFileName = renoResolution.ExpectedAddonFileName ??
                renoResolution.Match.Entry?.ExpectedAddonFileName ??
                renoResolution.Match.Entry?.ArtifactFileName,
            RecommendedDeploymentRelativeDirectory = renoResolution.DeploymentRelativeDirectory ??
                renoResolution.Match.Entry?.DeploymentRelativeDirectory,
            CompatibilityNotes = renoResolution.CompatibilityNotes,
            SnapshotResolution = renoResolution.Snapshot
        };
    }

    private static bool IsTechnicallyEligibleForOptiScaler(SteamGame game)
    {
        if (game.RequiresConfirmation || game.Executable is null ||
            !Path.GetExtension(game.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(game.ProtonPrefix)) return false;
        return SelectedArchitecture(game) == PeArchitecture.X64;
    }

    private static PeArchitecture SelectedArchitecture(SteamGame game)
    {
        var candidate = game.Candidates.FirstOrDefault(item => game.Executable is not null &&
            Path.GetFullPath(item.Path).Equals(Path.GetFullPath(game.Executable), StringComparison.Ordinal)) ??
            game.Candidates.FirstOrDefault();
        if (candidate is not null) return candidate.Architecture;
        if (game.Executable is null || !File.Exists(game.Executable)) return PeArchitecture.Unknown;
        try { return ArtifactValidator.ValidatePe(game.Executable); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return PeArchitecture.Unknown;
        }
    }

    private async Task<RenoDxResolution> EnrichWithSnapshotAsync(
        SteamGame game,
        RenoDxResolution current,
        bool allowNetwork,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (current.Selection is not null &&
            !current.Match.UsedEngineFallback &&
            current.Match.Compatibility is RenoDxCompatibilityState.ExactAddonAvailable or
                RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease or
                RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion or
                RenoDxCompatibilityState.InProgress)
            return current;

        var entry = current.Match.Entry;
        var architecture = current.Match.SelectedArchitecture is PeArchitecture.X86 or PeArchitecture.X64
            ? current.Match.SelectedArchitecture
            : SelectedArchitecture(game);
        var snapshot = await snapshots.ResolveAsync(
            game,
            entry,
            current.ExpectedAddonFileName ?? entry?.ExpectedAddonFileName ?? entry?.ArtifactFileName,
            entry?.ArtifactSlug,
            architecture,
            allowNetwork,
            cancellationToken,
            forceRefresh);

        if (snapshot.Selection is null)
        {
            var notes = current.CompatibilityNotes.ToList();
            if (snapshot.AmbiguousCandidates.Count > 0)
                notes.Add("Ambiguous snapshot assets: " + string.Join(", ", snapshot.AmbiguousCandidates));
            return current with
            {
                Snapshot = snapshot,
                Notes = notes,
                Warning = current.Warning ?? snapshot.Warning
            };
        }

        var updatedEntry = entry is null ? null : entry with
        {
            ExpectedAddonFileName = snapshot.Selection.DeployFileName,
            ArtifactFileName = snapshot.Selection.DeployFileName,
            ArtifactSlug = RenoDxIdentity.ArtifactSlug(snapshot.Selection.DeployFileName),
            ArchitectureAvailability = snapshot.Selection.Architecture,
            DirectAutomaticDownloadAvailable = true,
            SourceType = RenoDxSourceType.Snapshot
        };
        var match = current.Match with
        {
            Entry = updatedEntry ?? current.Match.Entry,
            Selection = snapshot.Selection,
            Compatibility = RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease,
            SelectedArchitecture = snapshot.Selection.Architecture,
            AddonArchitecture = snapshot.Selection.Architecture,
            RejectedReason = null,
            ReasonCode = "snapshot-release:" + snapshot.SelectionReason,
            UsedEngineFallback = false
        };
        var resolved = ApplySelection(game, current.Profile, match, snapshot.Selection, updatedEntry ?? entry);
        var diagnosticNotes = current.CompatibilityNotes.ToList();
        diagnosticNotes.Add($"Snapshot release: {snapshot.Index?.ReleaseTag ?? snapshot.Selection.ReleaseTag}");
        if (snapshot.ReleaseCommit is not null) diagnosticNotes.Add($"Release commit: {snapshot.ReleaseCommit}");
        if (snapshot.Asset is not null) diagnosticNotes.Add($"Asset ID: {snapshot.Asset.Id}");
        diagnosticNotes.Add($"Asset filename: {snapshot.Selection.AssetName}");
        if (snapshot.PublishedDigest is not null) diagnosticNotes.Add($"Published digest: {snapshot.PublishedDigest}");
        diagnosticNotes.Add($"Selection reason: {snapshot.SelectionReason}");
        diagnosticNotes.Add(snapshot.UsedStructuredMetadata
            ? "Structured snapshot metadata mapped this game."
            : "Structured snapshot metadata did not map this game; release assets were indexed instead.");
        diagnosticNotes.Add("Generic engine fallback was not used.");
        return resolved with
        {
            Snapshot = snapshot,
            OfficialPageUrl = snapshot.Index?.PageUrl ?? current.OfficialPageUrl ?? entry?.OfficialPageUrl ?? entry?.DiscussionUrl,
            ExpectedAddonFileName = snapshot.Selection.DeployFileName,
            DeploymentRelativeDirectory = current.DeploymentRelativeDirectory ?? entry?.DeploymentRelativeDirectory,
            Notes = diagnosticNotes,
            Warning = snapshot.Warning ?? resolved.Warning
        };
    }

    private async Task<RenoDxResolution> EnrichWithDiscussionAsync(
        SteamGame game,
        RenoDxResolution current,
        bool allowNetwork,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (current.Selection is not null &&
            current.Match.Compatibility is RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease)
            return current;

        var entry = current.Match.Entry;
        if (entry?.DiscussionUrl is null || !entry.OfficialCatalogOrigin)
            return current with
            {
                OfficialPageUrl = entry?.OfficialPageUrl ?? entry?.DiscussionUrl,
                ExpectedAddonFileName = entry?.ExpectedAddonFileName ?? entry?.ArtifactFileName,
                DeploymentRelativeDirectory = entry?.DeploymentRelativeDirectory,
                Notes = entry?.CompatibilityWarnings ?? []
            };
        if (current.Selection is not null &&
            current.Match.Compatibility is not RenoDxCompatibilityState.ListedManualDownloadRequired)
            return current with
            {
                OfficialPageUrl = entry.OfficialPageUrl ?? entry.DiscussionUrl,
                ExpectedAddonFileName = entry.ExpectedAddonFileName ?? entry.ArtifactFileName,
                DeploymentRelativeDirectory = entry.DeploymentRelativeDirectory,
                Notes = entry.CompatibilityWarnings
            };

        var architecture = current.Match.SelectedArchitecture is PeArchitecture.X86 or PeArchitecture.X64
            ? current.Match.SelectedArchitecture
            : SelectedArchitecture(game);
        var discussion = await discussions.ResolveAsync(
            entry.DiscussionUrl, entry, architecture, allowNetwork, cancellationToken, forceRefresh);
        var updatedEntry = entry with
        {
            OfficialPageUrl = discussion.OfficialPageUrl,
            DiscussionUrl = entry.DiscussionUrl,
            ExpectedAddonFileName = discussion.ExpectedFileName ?? entry.ExpectedAddonFileName,
            DeploymentRelativeDirectory = discussion.DeploymentRelativeDirectory ?? entry.DeploymentRelativeDirectory,
            CompatibilityWarnings = discussion.CompatibilityWarnings.Count > 0
                ? discussion.CompatibilityWarnings
                : entry.CompatibilityWarnings,
            MinimumReshadeVersion = discussion.MinimumReshadeVersion ?? entry.MinimumReshadeVersion,
            ArtifactFileName = discussion.ExpectedFileName ?? entry.ArtifactFileName,
            ArtifactSlug = RenoDxIdentity.ArtifactSlug(discussion.ExpectedFileName ?? entry.ArtifactFileName ?? entry.CanonicalName),
            ArchitectureAvailability = discussion.Selection?.Architecture ??
                (discussion.ExpectedFileName?.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) == true
                    ? PeArchitecture.X86
                    : discussion.ExpectedFileName?.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) == true
                        ? PeArchitecture.X64
                        : entry.ArchitectureAvailability)
        };

        if (discussion.Selection is { } selection)
        {
            var match = current.Match with
            {
                Entry = updatedEntry,
                Selection = selection,
                Compatibility = RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion,
                SelectedArchitecture = selection.Architecture,
                AddonArchitecture = selection.Architecture,
                RejectedReason = null,
                ReasonCode = "discussion-direct-addon"
            };
            var resolved = ApplySelection(game, current.Profile, match, selection, updatedEntry);
            return resolved with
            {
                OfficialPageUrl = discussion.OfficialPageUrl,
                ExpectedAddonFileName = discussion.ExpectedFileName,
                DeploymentRelativeDirectory = discussion.DeploymentRelativeDirectory,
                Notes = discussion.CompatibilityWarnings,
                Warning = discussion.Warning ?? resolved.Warning
            };
        }

        var compatibility = MapDiscussionCompatibility(discussion.State, current.Match.Compatibility);
        return current with
        {
            Match = current.Match with
            {
                Entry = updatedEntry,
                Compatibility = compatibility,
                RejectedReason = discussion.Warning ?? DescribeCompatibility(compatibility),
                ReasonCode = discussion.State.ToString()
            },
            Warning = discussion.Warning ?? DescribeCompatibility(compatibility),
            OfficialPageUrl = discussion.OfficialPageUrl,
            ExpectedAddonFileName = discussion.ExpectedFileName,
            DeploymentRelativeDirectory = discussion.DeploymentRelativeDirectory,
            Notes = discussion.CompatibilityWarnings
        };
    }

    private static RenoDxCompatibilityState MapDiscussionCompatibility(
        RenoDxDiscussionResolveState state,
        RenoDxCompatibilityState fallback) => state switch
        {
            RenoDxDiscussionResolveState.ExactAddonAvailableFromOfficialDiscussion =>
                RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion,
            RenoDxDiscussionResolveState.DirectAddonFoundNotYetValidated =>
                RenoDxCompatibilityState.DirectAddonFoundNotYetValidated,
            RenoDxDiscussionResolveState.OfficialPageAvailableNoDirectAddon =>
                RenoDxCompatibilityState.OfficialPageAvailableNoDirectAddon,
            RenoDxDiscussionResolveState.MultipleOfficialFilesRequireConfirmation =>
                RenoDxCompatibilityState.MultipleOfficialFilesRequireConfirmation,
            RenoDxDiscussionResolveState.AddonArchitectureMismatch =>
                RenoDxCompatibilityState.AddonArchitectureMismatch,
            RenoDxDiscussionResolveState.OfficialSourceTemporarilyUnavailable =>
                RenoDxCompatibilityState.OfficialSourceTemporarilyUnavailable,
            RenoDxDiscussionResolveState.OfficialSourceChanged =>
                RenoDxCompatibilityState.OfficialSourceChanged,
            RenoDxDiscussionResolveState.UnsafeArtifactRejected =>
                RenoDxCompatibilityState.UnsafeArtifactRejected,
            RenoDxDiscussionResolveState.ManualDownloadRequired =>
                RenoDxCompatibilityState.ListedManualDownloadRequired,
            _ => fallback
        };

    private RenoDxResolution ResolveRenoDx(
        SteamGame game,
        GameProfileMatch profile,
        RemoteManifestCatalog? remote,
        RenoDxWikiCatalog wikiCatalog)
    {
        var index = wikiCatalog.Index ?? new RenoDxCatalogIndex(
            wikiCatalog.Entries.Count > 0
                ? wikiCatalog.Entries
                : wikiCatalog.Records.Select(record => new RenoDxCatalogEntry(
                    record.Name, RenoDxIdentity.NormalizeKey(record.Name), [], null, record.Addon32Url,
                    record.Addon64Url,
                    Path.GetFileName((record.Addon64Url ?? record.Addon32Url)?.AbsolutePath),
                    RenoDxIdentity.ArtifactSlug(Path.GetFileName((record.Addon64Url ?? record.Addon32Url)?.AbsolutePath)),
                    record.Addon64Url is not null ? PeArchitecture.X64 :
                        record.Addon32Url is not null ? PeArchitecture.X86 : PeArchitecture.Unknown,
                    record.Status, RenoDxCatalogSection.ExactGame, RenoDxSourceType.Snapshot,
                    record.Addon32Url is not null || record.Addon64Url is not null, null, null, true, null)).ToArray(),
            wikiCatalog.SourceTimestamp, wikiCatalog.ETag, wikiCatalog.State, wikiCatalog.IsCached, wikiCatalog.CacheAge);

        var match = renoMatcher.Resolve(game, profile, index, remote);
        if (match.Compatibility == RenoDxCompatibilityState.OfflineCatalogInUse && match.Entry is null &&
            match.Selection is null && profile.Profile.RenoDx is null)
            match = match with { Compatibility = RenoDxCompatibilityState.NotListed };

        if (match.Selection is null && match.Entry is null && profile.Profile.RenoDx is { } fallbackSource &&
            match.MatchType is RenoDxMatchType.NoMatch or RenoDxMatchType.Superseded)
        {
            var builtIn = new ArtifactSelection(ComponentKind.RenoDx, fallbackSource.Version, fallbackSource.Url,
                fallbackSource.Version, Path.GetFileName(fallbackSource.Url.LocalPath), fallbackSource.Architecture,
                profile.IsExact ? game.AppId : null, fallbackSource.FileName, null, ArtifactArchiveKind.None,
                GameProfile: profile.Profile.Id, Support: SupportFor(profile), SourceValidatedByOfficialMetadata: true);
            var compatibility = profile.IsFallback
                ? profile.Profile.Engine == GameEngine.Unity
                    ? RenoDxCompatibilityState.GenericUnityAddonAvailable
                    : RenoDxCompatibilityState.GenericUnrealAddonAvailable
                : RenoDxCompatibilityState.ExactAddonAvailable;
            match = new(profile.IsFallback ? RenoDxMatchType.EngineFallback : RenoDxMatchType.BuiltInProfile,
                0.8, match.Entry, builtIn, compatibility, match.Evidence, match.AmbiguousCandidates,
                match.SuggestedExecutable, fallbackSource.Architecture, fallbackSource.Architecture,
                profile.IsFallback ? "engine-fallback" : "built-in", profile.IsFallback, null);
        }

        if (match.Selection is null)
            return new(null, profile, match.RejectedReason ?? DescribeCompatibility(match.Compatibility), match,
                match.Entry?.OfficialPageUrl ?? match.Entry?.DiscussionUrl,
                match.Entry?.ExpectedAddonFileName ?? match.Entry?.ArtifactFileName,
                match.Entry?.DeploymentRelativeDirectory, match.Entry?.CompatibilityWarnings ?? []);

        return ApplySelection(game, profile, match, match.Selection, match.Entry);
    }

    private static RenoDxResolution ApplySelection(
        SteamGame game,
        GameProfileMatch profile,
        RenoDxMatchResult match,
        ArtifactSelection selection,
        RenoDxCatalogEntry? entry)
    {
        var canonicalName = entry?.CanonicalName ?? profile.Profile.CanonicalName;
        var gameSpecific = !match.UsedEngineFallback &&
            selection.Support is ArtifactSupportKind.ExactGameProfile or ArtifactSupportKind.ExecutableOrAliasProfile;
        var sourceModel = new RenoDxSource(selection.SourceUrl, selection.DeployFileName, selection.Version,
            selection.Architecture, gameSpecific);
        var dynamicProfile = profile.Profile with
        {
            Id = gameSpecific ? $"wiki-{RenoDxIdentity.NormalizeKey(canonicalName).Replace(' ', '-')}" : profile.Profile.Id,
            CanonicalName = canonicalName,
            RenoDx = sourceModel,
            RenoDxSupport = gameSpecific ? GameProfileSupport.Supported : GameProfileSupport.EngineFallback,
            Warnings = profile.Profile.Warnings.Where(item =>
                !item.Contains("No supported game profile", StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        var matchReason = match.Evidence.FirstOrDefault()?.Detail ?? match.MatchType.ToString();
        var exactAppId = match.MatchType == RenoDxMatchType.ExactAppId ||
            match.MatchType == RenoDxMatchType.BuiltInProfile && profile.ExactAppId;
        var matchedProfile = new GameProfileMatch(dynamicProfile, matchReason, exactAppId);
        var warning = entry?.Status switch
        {
            RenoDxWikiStatus.InProgress =>
                $"The official RenoDX wiki marks '{entry.CanonicalName}' as under construction.",
            RenoDxWikiStatus.Unknown when entry.SourceSection == RenoDxCatalogSection.ExactGame =>
                $"The official RenoDX wiki does not publish a working-status marker for '{entry.CanonicalName}'.",
            _ => match.Compatibility == RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable
                ? "Exact RenoDX addon available for another executable."
                : null
        };
        return new(selection, matchedProfile, warning, match,
            entry?.OfficialPageUrl ?? entry?.DiscussionUrl,
            entry?.ExpectedAddonFileName ?? entry?.ArtifactFileName ?? selection.DeployFileName,
            entry?.DeploymentRelativeDirectory, entry?.CompatibilityWarnings ?? []);
    }

    private static string? DescribeCompatibility(RenoDxCompatibilityState state) => state switch
    {
        RenoDxCompatibilityState.ListedManualDownloadRequired => "Listed by RenoDX, manual download required",
        RenoDxCompatibilityState.OfficialPageAvailableNoDirectAddon =>
            "Official RenoDX page found. No direct addon download is available.",
        RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease => "Exact RenoDX addon available",
        RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion => "Exact RenoDX addon available",
        RenoDxCompatibilityState.MultipleOfficialFilesRequireConfirmation =>
            "Multiple official Discussion addon files require confirmation.",
        RenoDxCompatibilityState.AddonArchitectureMismatch =>
            "Addon found, but no compatible executable architecture is available.",
        RenoDxCompatibilityState.OfficialSourceTemporarilyUnavailable =>
            "Official RenoDX Discussion source is temporarily unavailable.",
        RenoDxCompatibilityState.UnsafeArtifactRejected => "Unsafe or invalid Discussion artifact rejected.",
        RenoDxCompatibilityState.AmbiguousMatch => "Multiple RenoDX catalog candidates require confirmation.",
        RenoDxCompatibilityState.MetadataUnavailable => "RenoDX catalog unavailable",
        RenoDxCompatibilityState.NotListed or RenoDxCompatibilityState.NoAddonFound => "No RenoDX addon found",
        RenoDxCompatibilityState.AddonAvailableExecutableSelectionRequired =>
            "Addon found, but no compatible executable was selected",
        RenoDxCompatibilityState.AddonAvailableArchitectureUnconfirmed => "Architecture could not be confirmed",
        RenoDxCompatibilityState.SupersededByGenericAddon => "Exact profile was superseded by a generic RenoDX addon",
        _ => null
    };

    private sealed record RenoDxResolution(
        ArtifactSelection? Selection,
        GameProfileMatch Profile,
        string? Warning,
        RenoDxMatchResult Match,
        Uri? OfficialPageUrl = null,
        string? ExpectedAddonFileName = null,
        string? DeploymentRelativeDirectory = null,
        IReadOnlyList<string>? Notes = null,
        RenoDxSnapshotResolution? Snapshot = null)
    {
        public IReadOnlyList<string> CompatibilityNotes => Notes ?? [];
    }

    public async Task<GameArtifactResolution> AcquireAsync(SteamGame game, bool allowNetwork = true, CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(game, allowNetwork, cancellationToken, forceRefresh: allowNetwork);
        if (!resolution.IsFullyAutomatic)
            throw new InvalidOperationException(string.Join(" ", resolution.Warnings));

        var requested = resolution.Artifacts.Where(selection => selection.Component switch
        {
            ComponentKind.ReShade or ComponentKind.RenoDx => resolution.CanAcquireRenoSetup,
            ComponentKind.OptiScaler => resolution.CanAcquireOptiScaler,
            _ => false
        }).Select(x => x.Component).ToHashSet();
        var acquired = await AcquireSelectedAsync(resolution, requested, allowNetwork, cancellationToken);
        if (!acquired.IsFullyAutomatic)
            throw new InvalidOperationException(string.Join(" ", acquired.Warnings));
        return acquired;
    }

    public async Task<GameArtifactResolution> AcquireSelectedAsync(
        GameArtifactResolution resolution,
        IReadOnlySet<ComponentKind> requestedComponents,
        bool allowNetwork = true,
        CancellationToken cancellationToken = default,
        IProgress<ArtifactAcquisitionProgress>? progress = null)
    {
        var selections = resolution.Artifacts.ToDictionary(x => x.Component);
        var warnings = resolution.Warnings.ToList();

        async Task<bool> AcquireOneAsync(ComponentKind component)
        {
            if (!selections.TryGetValue(component, out var selection))
            {
                warnings.Add($"{component} was requested, but no official artifact selection is available.");
                return false;
            }
            if (selection.CacheState == ArtifactCacheState.Cached) return true;
            if (!allowNetwork)
            {
                warnings.Add($"{component} is not cached for offline installation.");
                return false;
            }

            try
            {
                selections[component] = await cache.AcquireAsync(selection, httpClient, cancellationToken, progress);
                return true;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"{component} acquisition timed out: {exception.Message}");
                return false;
            }
            catch (Exception exception) when (IsRecoverableAcquisitionFailure(exception))
            {
                warnings.Add($"{component} acquisition failed: {exception.Message}");
                return false;
            }
        }

        var renoRequested = requestedComponents.Contains(ComponentKind.RenoDx);
        var reshadeRequested = requestedComponents.Contains(ComponentKind.ReShade) || renoRequested;
        var optiRequested = requestedComponents.Contains(ComponentKind.OptiScaler);

        var reshadeReady = !reshadeRequested || await AcquireOneAsync(ComponentKind.ReShade);
        var renoReady = !renoRequested || await AcquireOneAsync(ComponentKind.RenoDx);
        if (renoRequested && !reshadeReady)
            warnings.Add("RenoDX requires a prepared ReShade full-addon host before installation can continue.");
        var optiReady = !optiRequested || await AcquireOneAsync(ComponentKind.OptiScaler);
        var acquiredSelections = selections.Values.ToArray();
        var canAcquireReno = renoRequested && reshadeReady && renoReady;
        var canAcquireReshadeOnly = requestedComponents.Contains(ComponentKind.ReShade) && reshadeReady;
        var canAcquireOpti = optiRequested && optiReady;

        return resolution with
        {
            Artifacts = acquiredSelections,
            Warnings = FilterAcquisitionWarnings(warnings, requestedComponents),
            IsFullyAutomatic = canAcquireReno || canAcquireReshadeOnly || canAcquireOpti,
            Components = BuildComponents(resolution.Profile, acquiredSelections, warnings, resolution.RenoDxMatch),
            CanAcquireRenoSetup = canAcquireReno,
            CanAcquireRenoDx = renoRequested && renoReady && reshadeReady,
            CanAcquireOptiScaler = canAcquireOpti
        };
    }

    private static IReadOnlyList<string> FilterAcquisitionWarnings(
        IReadOnlyList<string> warnings,
        IReadOnlySet<ComponentKind> requested)
    {
        return warnings.Where(warning =>
        {
            if (warning.Contains("No supported game profile", StringComparison.OrdinalIgnoreCase) &&
                !requested.Contains(ComponentKind.RenoDx))
                return false;
            return true;
        }).ToArray();
    }

    private static bool IsRecoverableAcquisitionFailure(Exception exception) => exception is
        HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or JsonException or ArtifactPipelineException or
        System.ComponentModel.Win32Exception;

    private async Task<(ArtifactSelection? Selection, bool Checked)> TryResolveReShadeAsync(SteamGame game, CancellationToken cancellationToken)
    {
        try
        {
            var html = await httpClient.GetStringAsync("https://reshade.me/", cancellationToken);
            var match = ReShadeLink.Match(html);
            if (!match.Success) return (null, true);
            var version = match.Groups[1].Value;
            var architecture = game.Candidates.FirstOrDefault(x => x.Path == game.Executable)?.Architecture ?? PeArchitecture.X64;
            if (architecture is not PeArchitecture.X86 and not PeArchitecture.X64) architecture = PeArchitecture.X64;
            return (new(ComponentKind.ReShade, version, new Uri($"https://reshade.me{match.Value}"), version,
                Path.GetFileName(match.Value), architecture, null,
                architecture == PeArchitecture.X86 ? "ReShade32.dll" : "ReShade64.dll", null, ArtifactArchiveKind.ReShadeInstaller,
                Support: ArtifactSupportKind.General), true);
        }
        catch (HttpRequestException) { return (null, false); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return (null, false); }
    }

    private async Task<(ArtifactSelection? Selection, bool Checked)> TryResolveGitHubAsync(
        ComponentKind component,
        uint appId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            var release = await releases.GetLatestAsync(component, cancellationToken, forceRefresh);
            if (release is null) return (null, true);
            ReleaseAsset? asset = component switch
            {
                ComponentKind.OptiScaler => release.Assets.Where(x =>
                    (x.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                     x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
                    x.Name.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) &&
                    !IsUnstableAssetName(x.Name))
                    .OrderByDescending(x => x.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.Name.Equals("OptiScaler.7z", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault(),
                _ => null
            };
            if (asset is null) return (null, true);
            var archiveKind = asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? ArtifactArchiveKind.Zip
                : ArtifactArchiveKind.SevenZip;
            return (new(component, release.Version, asset.Url, release.Version, asset.Name, PeArchitecture.X64,
                null, "OptiScaler.dll", asset.Digest, archiveKind,
                Support: ArtifactSupportKind.General), true);
        }
        catch (HttpRequestException) { return (null, false); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return (null, false); }
    }

    private async Task<ArtifactSelection?> FindCachedAsync(ComponentKind component, uint appId, CancellationToken cancellationToken)
    {
        var entries = (await cache.ListAsync(cancellationToken)).Where(x => x.IsValid && x.Metadata.Component == component &&
            (x.Metadata.GameAppId is null || x.Metadata.GameAppId == appId)).OrderByDescending(x => x.Metadata.DownloadedUtc).ToArray();
        if (entries.Length == 0) return null;
        var entry = entries[0];
        var directory = Path.GetDirectoryName(entry.MetadataPath)!;
        var payload = Path.GetFullPath(Path.Combine(directory, entry.Metadata.RelativePayloadPath));
        var bundleManifest = entry.Metadata.BundleManifestRelativePath is null ? null :
            Path.GetFullPath(Path.Combine(directory, entry.Metadata.BundleManifestRelativePath));
        return new(component, entry.Metadata.Version, new Uri(entry.Metadata.SourceUrl), entry.Metadata.ReleaseTag,
            entry.Metadata.AssetName, entry.Metadata.Architecture, entry.Metadata.GameAppId,
            entry.Metadata.DeployFileName ?? Path.GetFileName(payload), null,
            entry.Metadata.ExtractionSucceeded ? (component == ComponentKind.ReShade ? ArtifactArchiveKind.ReShadeInstaller : ArtifactArchiveKind.SevenZip) : ArtifactArchiveKind.None,
            ArtifactCacheState.Cached, payload, entry.Metadata.AdditionalRelativePaths.Select(x => Path.GetFullPath(Path.Combine(directory, x))).ToArray(),
            entry.Metadata.GameProfile, entry.Metadata.Support, ArtifactValidationState.Valid, entry.Metadata.Sha256,
            ETag: entry.Metadata.ETag, LastModified: entry.Metadata.LastModified,
            BundleManifestPath: bundleManifest, ArchiveSha256: entry.Metadata.ArchiveSha256);
    }

    private async Task<(ArtifactSelection Selection, bool Checked)> CheckSnapshotAsync(
        ArtifactSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, selection.SourceUrl);
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
            if (selection.ETag is not null && EntityTagHeaderValue.TryParse(selection.ETag, out var etag))
                request.Headers.IfNoneMatch.Add(etag);
            if (selection.LastModified is not null) request.Headers.IfModifiedSince = selection.LastModified;
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified) return (selection, true);
            response.EnsureSuccessStatusCode();
            var cached = selection.CacheState == ArtifactCacheState.Cached;
            var comparable = (selection.ETag is not null && response.Headers.ETag is not null) ||
                (selection.LastModified is not null && response.Content.Headers.LastModified is not null);
            if (cached && !comparable) return (selection, false);
            var changed = cached &&
                ((selection.ETag is not null && response.Headers.ETag is not null &&
                  !selection.ETag.Equals(response.Headers.ETag.ToString(), StringComparison.Ordinal)) ||
                 (selection.LastModified is not null && response.Content.Headers.LastModified is not null &&
                  selection.LastModified != response.Content.Headers.LastModified));
            return (changed ? selection with { CacheState = ArtifactCacheState.DownloadRequired } : selection, true);
        }
        catch (HttpRequestException) { return (selection, false); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return (selection, false); }
    }

    private static ArtifactSupportKind SupportFor(GameProfileMatch profile) => profile.Profile.RenoDxSupport switch
    {
        GameProfileSupport.Supported when profile.ExactAppId => ArtifactSupportKind.ExactGameProfile,
        GameProfileSupport.Supported => ArtifactSupportKind.ExecutableOrAliasProfile,
        GameProfileSupport.EngineFallback when profile.Profile.Engine == GameEngine.Unity => ArtifactSupportKind.UnityFallback,
        GameProfileSupport.EngineFallback when profile.Profile.Engine == GameEngine.Unreal => ArtifactSupportKind.UnrealFallback,
        _ => ArtifactSupportKind.Unavailable
    };

    private static bool IsUnstableAssetName(string assetName) =>
        assetName.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
        assetName.Contains("source", StringComparison.OrdinalIgnoreCase) ||
        assetName.Contains("nightly", StringComparison.OrdinalIgnoreCase) ||
        assetName.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
        assetName.Contains("test", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ResolvedArtifact> BuildComponents(
        GameProfileMatch profile,
        IReadOnlyList<ArtifactSelection> selections,
        IReadOnlyList<string> warnings,
        RenoDxMatchResult? renoMatch)
    {
        return new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }.Select(component =>
        {
            var selection = selections.SingleOrDefault(x => x.Component == component);
            if (selection is not null)
                return new ResolvedArtifact(component, selection.Version, selection.SourceUrl, selection.ReleaseTag,
                    selection.AssetName, selection.Architecture, selection.GameProfile ?? profile.Profile.Id, selection.Support,
                    selection.CacheState, selection.Validation, selection.Sha256, selection.UnavailableReason, selection);
            var reason = component switch
            {
                ComponentKind.ReShade => "The official full-addon ReShade release could not be resolved and no valid cached build is available.",
                ComponentKind.RenoDx => renoMatch?.RejectedReason ??
                    DescribeCompatibility(renoMatch?.Compatibility ?? RenoDxCompatibilityState.NotListed) ??
                    "No RenoDX addon found",
                ComponentKind.OptiScaler => "The official OptiScaler release could not be resolved, is not eligible, or no valid cached release is available.",
                _ => warnings.LastOrDefault() ?? "Official release metadata is unavailable."
            };
            var support = ArtifactSupportKind.Unavailable;
            return new ResolvedArtifact(component, null, null, null, null, PeArchitecture.Unknown, profile.Profile.Id,
                support, ArtifactCacheState.DownloadRequired, ArtifactValidationState.NotValidated, null, reason, null);
        }).ToArray();
    }
}
