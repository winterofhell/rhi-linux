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
    public bool CanAcquireOptiScaler { get; init; }
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

    public async Task<GameArtifactResolution> ResolveAsync(
        SteamGame game,
        bool allowNetwork = true,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var profile = await catalog.MatchAsync(game, cancellationToken);
        var warnings = profile.Profile.Warnings.ToList();
        var selections = new List<ArtifactSelection>();

        var metadataChecked = allowNetwork;
        var manifestTask = manifest.CheckAsync(allowNetwork, cancellationToken);
        var wikiTask = wiki.GetAsync(allowNetwork, cancellationToken);
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

        var renoResolution = ResolveRenoDx(game, profile, manifestCatalog, wikiCatalog.Records);
        profile = renoResolution.Profile;
        if (renoResolution.Warning is not null) warnings.Add(renoResolution.Warning);
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
        else warnings.Add("No compatible RenoDX addon mapping exists for this game.");

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
        var canAcquireReno = selections.Any(x => x.Component == ComponentKind.ReShade) &&
            selections.Any(x => x.Component == ComponentKind.RenoDx && x.Support != ArtifactSupportKind.Unavailable);
        var canAcquireOpti = optiTechnicallyEligible && selections.Any(x => x.Component == ComponentKind.OptiScaler);
        var automatic = canAcquireReno || canAcquireOpti;
        var metadataState = !allowNetwork ? MetadataCheckState.Offline : metadataChecked ? MetadataCheckState.Online : MetadataCheckState.UnableToCheck;
        var result = new GameArtifactResolution(profile, selections, warnings, automatic, metadataState);
        return result with
        {
            Components = BuildComponents(profile, selections, warnings),
            RemoteManifest = manifestCheck,
            CanAcquireRenoSetup = canAcquireReno,
            CanAcquireOptiScaler = canAcquireOpti
        };
    }

    private static bool IsTechnicallyEligibleForOptiScaler(SteamGame game)
    {
        if (game.RequiresConfirmation || game.Executable is null ||
            !Path.GetExtension(game.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(game.ProtonPrefix)) return false;
        return SelectedArchitecture(game) == PeArchitecture.X64;
    }

    private static RenoDxResolution ResolveRenoDx(
        SteamGame game,
        GameProfileMatch profile,
        RemoteManifestCatalog? remote,
        IReadOnlyList<RenoDxWikiRecord> wikiRecords)
    {
        var architecture = SelectedArchitecture(game);
        if (architecture is not PeArchitecture.X86 and not PeArchitecture.X64)
            return new(null, profile, "The selected game architecture is not safe for RenoDX resolution.");

        ArtifactSelection BuiltIn(RenoDxSource source) => new(
            ComponentKind.RenoDx, source.Version, source.Url, source.Version,
            Path.GetFileName(source.Url.LocalPath), source.Architecture,
            profile.IsExact ? game.AppId : null, source.FileName, null, ArtifactArchiveKind.None,
            GameProfile: profile.Profile.Id, Support: SupportFor(profile));

        if (profile.ExactAppId && profile.Profile.RenoDx is { } exactAppIdSource)
            return new(BuiltIn(exactAppIdSource), profile, null);

        RenoDxResolution? Dynamic(string canonicalName, bool exactAppId, string matchReason)
        {
            var record = wikiRecords.SingleOrDefault(item =>
                item.Name.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));
            var source = record?.GetAddonUri(architecture);
            if (remote?.AddonOverrides.TryGetValue(canonicalName, out var manifestOverride) == true)
                source = manifestOverride;
            if (source is null) return null;
            if (!OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(source))
                return new(null, profile,
                    $"The exact RenoDX mapping for '{canonicalName}' has an unsupported source URL.");
            var extension = Path.GetExtension(source.AbsolutePath);
            var sourceArchitecture = extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
                ? PeArchitecture.X86 : PeArchitecture.X64;
            if (sourceArchitecture != architecture)
                return new(null, profile,
                    $"The exact RenoDX mapping for '{canonicalName}' is {sourceArchitecture}, but the selected game is {architecture}.");

            var fileName = Path.GetFileName(Uri.UnescapeDataString(source.AbsolutePath));
            var genericEngine = GenericEngineFor(fileName);
            if (genericEngine is { } requiredEngine && requiredEngine != game.Engine)
                return new(null, profile,
                    $"The manifest maps '{canonicalName}' to a generic {requiredEngine} addon, but the detected engine is {game.Engine}.");
            var gameSpecific = genericEngine is null;
            var support = gameSpecific
                ? exactAppId ? ArtifactSupportKind.ExactGameProfile : ArtifactSupportKind.ExecutableOrAliasProfile
                : genericEngine == GameEngine.Unity
                    ? ArtifactSupportKind.UnityFallback
                    : ArtifactSupportKind.UnrealFallback;
            var sourceModel = new RenoDxSource(source, fileName, "snapshot", architecture, gameSpecific);
            var dynamicProfile = profile.Profile with
            {
                CanonicalName = canonicalName,
                RenoDx = sourceModel,
                RenoDxSupport = gameSpecific ? GameProfileSupport.Supported : GameProfileSupport.EngineFallback
            };
            var matchedProfile = new GameProfileMatch(dynamicProfile, matchReason, exactAppId);
            var selection = new ArtifactSelection(ComponentKind.RenoDx, "snapshot", source, "snapshot",
                fileName, architecture, gameSpecific ? game.AppId : null, fileName, null,
                ArtifactArchiveKind.None, GameProfile: dynamicProfile.Id, Support: support,
                SourceValidatedByOfficialMetadata: true);
            var warning = record?.Status switch
            {
                RenoDxWikiStatus.InProgress =>
                    $"The official RenoDX wiki marks '{record.Name}' as under construction.",
                RenoDxWikiStatus.Unknown =>
                    $"The official RenoDX wiki does not publish a working-status marker for '{record.Name}'.",
                _ => null
            };
            return new(selection, matchedProfile, warning);
        }

        if (remote is not null)
        {
            var appIdMatches = remote.SteamAppIds.Where(entry => entry.Value == game.AppId)
                .Select(entry => entry.Key).ToArray();
            if (appIdMatches.Length == 1 && Dynamic(appIdMatches[0], true,
                    $"Remote manifest Steam AppID {game.AppId}") is { } appIdResolution)
                return appIdResolution;
            if (appIdMatches.Length > 1)
                return new(null, profile, "The remote manifest has ambiguous identities for this Steam AppID.");
        }

        if (profile.IsExact && profile.Profile.RenoDx is { } builtInExactSource)
            return new(BuiltIn(builtInExactSource), profile, null);

        if (remote is not null && game.Executable is not null)
        {
            var executableName = Path.GetFileName(game.Executable);
            var executableMatches = remote.LaunchExecutables.Where(entry =>
                entry.Value.Equals(executableName, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray();
            if (executableMatches.Length == 1 && Dynamic(executableMatches[0], false,
                    $"Remote manifest executable '{executableName}'") is { } executableResolution)
                return executableResolution;
            if (executableMatches.Length > 1)
                return new(null, profile, "Multiple remote game identities use the selected executable filename.");
        }

        var directNames = new[]
        {
            game.Name,
            game.Executable is null ? null : Path.GetFileNameWithoutExtension(game.Executable)
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var name in directNames)
            if (Dynamic(name, false, $"Exact official wiki name '{name}'") is { } directResolution)
                return directResolution;

        if (remote is not null)
        {
            var aliasMatches = directNames.Where(remote.Aliases.ContainsKey)
                .Select(name => remote.Aliases[name]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (aliasMatches.Length == 1 && Dynamic(aliasMatches[0], false,
                    $"Unambiguous remote alias for '{game.Name}'") is { } aliasResolution)
                return aliasResolution;
            if (aliasMatches.Length > 1)
                return new(null, profile, "The game name and executable resolve to different remote aliases.");

            var overrideMatches = directNames.Where(remote.AddonOverrides.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (overrideMatches.Length == 1 && Dynamic(overrideMatches[0], false,
                    $"Exact remote snapshot override for '{overrideMatches[0]}'") is { } overrideResolution)
                return overrideResolution;
            if (overrideMatches.Length > 1)
                return new(null, profile, "Multiple remote snapshot overrides match the selected game.");
        }

        return profile.Profile.RenoDx is { } fallbackSource
            ? new(BuiltIn(fallbackSource), profile, null)
            : new(null, profile, null);
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

    private static GameEngine? GenericEngineFor(string fileName)
    {
        if (fileName.Contains("unityengine", StringComparison.OrdinalIgnoreCase)) return GameEngine.Unity;
        if (fileName.Contains("unrealengine", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("ue-extended", StringComparison.OrdinalIgnoreCase)) return GameEngine.Unreal;
        return null;
    }

    private sealed record RenoDxResolution(
        ArtifactSelection? Selection,
        GameProfileMatch Profile,
        string? Warning);

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
        CancellationToken cancellationToken = default)
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
                selections[component] = await cache.AcquireAsync(selection, httpClient, cancellationToken);
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
        var reshadeRequested = renoRequested || requestedComponents.Contains(ComponentKind.ReShade);
        var reshadeReady = !reshadeRequested || await AcquireOneAsync(ComponentKind.ReShade);
        var renoReady = renoRequested && reshadeReady && await AcquireOneAsync(ComponentKind.RenoDx);
        var optiRequested = requestedComponents.Contains(ComponentKind.OptiScaler);
        var optiReady = optiRequested && await AcquireOneAsync(ComponentKind.OptiScaler);
        var acquiredSelections = selections.Values.ToArray();
        var canAcquireReno = renoRequested && reshadeReady && renoReady;
        var canAcquireOpti = optiRequested && optiReady;

        return resolution with
        {
            Artifacts = acquiredSelections,
            Warnings = warnings,
            IsFullyAutomatic = canAcquireReno || canAcquireOpti,
            Components = BuildComponents(resolution.Profile, acquiredSelections, warnings),
            CanAcquireRenoSetup = canAcquireReno,
            CanAcquireOptiScaler = canAcquireOpti
        };
    }

    private static bool IsRecoverableAcquisitionFailure(Exception exception) => exception is
        HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or JsonException or System.ComponentModel.Win32Exception;

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
                ComponentKind.OptiScaler => release.Assets.Where(x => x.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
                    x.Name.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) &&
                    !IsUnstableAssetName(x.Name))
                    .OrderByDescending(x => x.Name.Equals("OptiScaler.7z", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault(),
                _ => null
            };
            if (asset is null) return (null, true);
            return (new(component, release.Version, asset.Url, release.Version, asset.Name, PeArchitecture.X64,
                null, "OptiScaler.dll", asset.Digest, ArtifactArchiveKind.SevenZip,
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
            // Snapshot display versions stay "snapshot"; only treat as outdated when remote identity metadata changes.
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
        IReadOnlyList<string> warnings)
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
                ComponentKind.RenoDx => "No safe game-specific or supported engine fallback is available.",
                ComponentKind.OptiScaler => "The official OptiScaler release could not be resolved, is not eligible, or no valid cached release is available.",
                _ => warnings.LastOrDefault() ?? "Official release metadata is unavailable."
            };
            return new ResolvedArtifact(component, null, null, null, null, PeArchitecture.Unknown, profile.Profile.Id,
                ArtifactSupportKind.Unavailable, ArtifactCacheState.DownloadRequired, ArtifactValidationState.NotValidated, null, reason, null);
        }).ToArray();
    }
}
