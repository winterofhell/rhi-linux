using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum RenoDxSnapshotResolveState
{
    ExactAddonAvailableFromOfficialSnapshotRelease,
    AmbiguousSnapshotAssets,
    SnapshotReleaseUnavailable,
    NoMatchingSnapshotAsset,
    OfflineCachedSnapshotUsed,
    DigestUnavailable
}

public sealed record RenoDxSnapshotAsset(
    long Id,
    string Name,
    string NormalizedName,
    string Slug,
    PeArchitecture Architecture,
    Uri DownloadUrl,
    long Size,
    string? Digest,
    DateTimeOffset? UpdatedAt);

public sealed record RenoDxSnapshotGameMapping(
    string Id,
    string Title,
    IReadOnlyList<string> Aliases,
    uint? SteamAppId,
    string? GameExecutable,
    IReadOnlyList<string> ArtifactNames);

public sealed record RenoDxSnapshotReleaseIndex(
    int SchemaVersion,
    long ReleaseId,
    string ReleaseTag,
    string? ReleaseCommit,
    Uri PageUrl,
    DateTimeOffset PublishedAt,
    DateTimeOffset RetrievedUtc,
    string? ETag,
    string? LastModified,
    bool UsedStructuredMetadata,
    IReadOnlyList<RenoDxSnapshotAsset> Assets,
    IReadOnlyDictionary<string, RenoDxSnapshotAsset> ByExactName,
    IReadOnlyDictionary<string, IReadOnlyList<RenoDxSnapshotAsset>> BySlug,
    IReadOnlyList<RenoDxSnapshotGameMapping> Games);

public sealed record RenoDxSnapshotResolution(
    RenoDxSnapshotResolveState State,
    ArtifactSelection? Selection,
    RenoDxSnapshotReleaseIndex? Index,
    RenoDxSnapshotAsset? Asset,
    string SelectionReason,
    bool UsedStructuredMetadata,
    bool GenericFallbackEvaluated,
    IReadOnlyList<string> AmbiguousCandidates,
    string? Warning,
    string? ReleaseCommit,
    string? PublishedDigest);

public sealed class RenoDxSnapshotReleaseResolver(HttpClient httpClient, XdgPaths paths)
{
    public const string Repository = "clshortfuse/renodx";
    public const int IndexSchemaVersion = 1;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly Regex AddonNamePattern = new(
        @"^renodx-(?<slug>[A-Za-z0-9._-]+)\.addon(?<arch>32|64)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, Task<RenoDxSnapshotReleaseIndex?>> InFlight = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (RenoDxSnapshotReleaseIndex Index, DateTimeOffset WriteUtc)> MemoryIndexes =
        new(StringComparer.Ordinal);

    public static void ClearMemoryCacheForTests()
    {
        MemoryIndexes.Clear();
        InFlight.Clear();
    }

    private string CacheDirectory => Path.Combine(paths.AppCacheDirectory, "metadata", "renodx-snapshot");
    private string IndexCachePath => Path.Combine(CacheDirectory, "release-index.json");
    private string ETagPath => IndexCachePath + ".etag";
    private string CheckedPath => IndexCachePath + ".checked";

    public async Task<RenoDxSnapshotResolution> ResolveAsync(
        SteamGame game,
        RenoDxCatalogEntry? entry,
        string? expectedFileName,
        string? artifactSlug,
        PeArchitecture architecture,
        bool allowNetwork,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var index = await GetIndexAsync(allowNetwork, cancellationToken, forceRefresh);
        if (index is null)
            return new(RenoDxSnapshotResolveState.SnapshotReleaseUnavailable, null, null, null,
                "snapshot-unavailable", false, false, [],
                "Official RenoDX snapshot release metadata is unavailable.", null, null);

        var selectedArchitecture = architecture is PeArchitecture.X86 or PeArchitecture.X64
            ? architecture
            : PeArchitecture.X64;
        var expected = NormalizeAddonFileName(expectedFileName) ??
            NormalizeAddonFileName(entry?.ExpectedAddonFileName) ??
            NormalizeAddonFileName(entry?.ArtifactFileName);
        var slug = FirstNonEmpty(
            RenoDxIdentity.ArtifactSlug(expected),
            artifactSlug,
            entry?.ArtifactSlug,
            DeriveSlug(entry?.CanonicalName),
            DeriveSlug(game.Name));

        var structured = TryMatchStructured(index, game, entry, selectedArchitecture);
        if (structured is { } structuredMatch)
            return Success(index, structuredMatch.Asset, structuredMatch.Reason, true, selectedArchitecture);

        if (!string.IsNullOrWhiteSpace(expected) &&
            index.ByExactName.TryGetValue(expected, out var exactAsset))
        {
            if (exactAsset.Architecture != selectedArchitecture &&
                selectedArchitecture is PeArchitecture.X86 or PeArchitecture.X64)
            {
                var archName = BuildAddonFileName(exactAsset.Slug, selectedArchitecture);
                if (index.ByExactName.TryGetValue(archName, out var archAsset))
                    return Success(index, archAsset, "exact-expected-filename", false, selectedArchitecture);
            }
            if (exactAsset.Architecture == selectedArchitecture || selectedArchitecture == PeArchitecture.Unknown)
                return Success(index, exactAsset, "exact-expected-filename", false, exactAsset.Architecture);
        }

        if (!string.IsNullOrWhiteSpace(slug) &&
            index.BySlug.TryGetValue(slug, out var slugAssets))
        {
            var archMatches = slugAssets.Where(item => item.Architecture == selectedArchitecture).ToArray();
            if (archMatches.Length == 1)
                return Success(index, archMatches[0], "exact-catalog-slug", false, selectedArchitecture);
            if (archMatches.Length > 1)
                return Ambiguous(index, archMatches, "ambiguous-slug-architecture");
            if (selectedArchitecture == PeArchitecture.X86)
            {
                var x64 = slugAssets.Where(item => item.Architecture == PeArchitecture.X64).ToArray();
                if (x64.Length == 1 && HasX64Executable(game))
                    return Success(index, x64[0], "slug-x64-alternate-executable", false, PeArchitecture.X64);
            }
            if (slugAssets.Count > 1)
                return Ambiguous(index, slugAssets, "ambiguous-slug");
        }

        var derived = DeriveSlug(entry?.CanonicalName) ?? DeriveSlug(game.Name);
        if (!string.IsNullOrWhiteSpace(derived) &&
            !derived.Equals(slug, StringComparison.OrdinalIgnoreCase) &&
            index.BySlug.TryGetValue(derived, out var derivedAssets))
        {
            var archMatches = derivedAssets.Where(item => item.Architecture == selectedArchitecture).ToArray();
            if (archMatches.Length == 1)
                return Success(index, archMatches[0], "unique-derived-slug", false, selectedArchitecture);
            if (archMatches.Length > 1)
                return Ambiguous(index, archMatches, "ambiguous-derived-slug");
        }

        return new(RenoDxSnapshotResolveState.NoMatchingSnapshotAsset, null, index, null,
            "no-matching-snapshot-asset", index.UsedStructuredMetadata, false, [],
            null, index.ReleaseCommit, null);
    }

    public async Task<RenoDxSnapshotReleaseIndex?> GetIndexAsync(
        bool allowNetwork,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!forceRefresh &&
            MemoryIndexes.TryGetValue(CacheDirectory, out var memory) &&
            DateTimeOffset.UtcNow - memory.WriteUtc < RefreshInterval)
            return memory.Index;

        if (!forceRefresh)
        {
            var cached = await ReadCachedIndexAsync(cancellationToken);
            if (cached is not null && await IsFreshAsync(cancellationToken))
            {
                Remember(cached);
                return cached;
            }
            if (!allowNetwork && cached is not null)
            {
                Remember(cached);
                return cached;
            }
        }

        if (!allowNetwork)
            return await ReadCachedIndexAsync(cancellationToken);

        var key = $"{CacheDirectory}|{(forceRefresh ? "force" : "default")}";
        var task = InFlight.GetOrAdd(key, _ => LoadRemoteIndexAsync(cancellationToken));
        try
        {
            var index = await task;
            if (index is not null) Remember(index);
            return index ?? await ReadCachedIndexAsync(cancellationToken);
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }

    public static RenoDxSnapshotReleaseIndex ParseReleasePayload(
        string releaseJson,
        IReadOnlyList<string> assetPagesJson,
        string? gamesIndexJson,
        string? generatedMetadataJson,
        string? etag,
        string? lastModified,
        DateTimeOffset retrievedUtc)
    {
        using var releaseDocument = JsonDocument.Parse(releaseJson);
        var root = releaseDocument.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Snapshot release metadata must be a JSON object.");
        var releaseId = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsedId)
            ? parsedId
            : throw new InvalidDataException("Snapshot release metadata is missing a release id.");
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidDataException("Snapshot release metadata is missing a tag.");
        var pageUrlText = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() : null;
        if (!Uri.TryCreate(pageUrlText, UriKind.Absolute, out var pageUrl))
            throw new InvalidDataException("Snapshot release metadata is missing a page URL.");
        var publishedAt = root.TryGetProperty("published_at", out var publishedElement) &&
            publishedElement.TryGetDateTimeOffset(out var published)
            ? published
            : retrievedUtc;
        var commit = root.TryGetProperty("target_commitish", out var commitElement)
            ? commitElement.GetString()
            : null;

        var assets = new List<RenoDxSnapshotAsset>();
        if (assetPagesJson.Count == 0 && root.TryGetProperty("assets", out var embeddedAssets) &&
            embeddedAssets.ValueKind == JsonValueKind.Array)
            assets.AddRange(ParseAssetArray(embeddedAssets, tag));
        foreach (var page in assetPagesJson)
        {
            using var pageDocument = JsonDocument.Parse(page);
            if (pageDocument.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Snapshot release asset page must be a JSON array.");
            assets.AddRange(ParseAssetArray(pageDocument.RootElement, tag));
        }

        assets = assets
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byExact = new Dictionary<string, RenoDxSnapshotAsset>(StringComparer.OrdinalIgnoreCase);
        var bySlug = new Dictionary<string, List<RenoDxSnapshotAsset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            byExact[asset.Name] = asset;
            if (!bySlug.TryGetValue(asset.Slug, out var list))
            {
                list = [];
                bySlug[asset.Slug] = list;
            }
            list.Add(asset);
        }

        var games = new List<RenoDxSnapshotGameMapping>();
        var usedStructured = false;
        string? structuredCommit = null;
        if (!string.IsNullOrWhiteSpace(generatedMetadataJson))
        {
            var parsed = ParseGeneratedMetadata(generatedMetadataJson);
            games.AddRange(parsed.Games);
            structuredCommit = parsed.Commit;
            usedStructured = games.Count > 0;
        }
        if (!string.IsNullOrWhiteSpace(gamesIndexJson))
        {
            var parsed = ParseGamesIndex(gamesIndexJson);
            MergeGames(games, parsed);
            usedStructured = usedStructured || parsed.Count > 0;
        }

        return new(
            IndexSchemaVersion,
            releaseId,
            tag,
            structuredCommit ?? commit,
            pageUrl,
            publishedAt,
            retrievedUtc,
            etag,
            lastModified,
            usedStructured,
            assets,
            byExact,
            bySlug.ToDictionary(
                pair => pair.Key,
                IReadOnlyList<RenoDxSnapshotAsset> (pair) => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            games);
    }

    public static string? DeriveSlug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = RenoDxIdentity.NormalizeKey(name);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(compact) ? null : compact;
    }

    private async Task<RenoDxSnapshotReleaseIndex?> LoadRemoteIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var release = await SelectOfficialSnapshotReleaseAsync(cancellationToken);
            if (release is null) return await ReadCachedIndexAsync(cancellationToken);

            var (releaseJson, etag, lastModified) = release.Value;
            using var releaseDocument = JsonDocument.Parse(releaseJson);
            var releaseId = releaseDocument.RootElement.GetProperty("id").GetInt64();
            var tag = releaseDocument.RootElement.GetProperty("tag_name").GetString()!;
            var assetPages = await FetchAllAssetPagesAsync(releaseId, cancellationToken);
            string? gamesIndexJson = null;
            string? generatedMetadataJson = null;
            foreach (var page in assetPages)
            {
                using var pageDocument = JsonDocument.Parse(page);
                foreach (var asset in pageDocument.RootElement.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    var urlText = asset.GetProperty("browser_download_url").GetString();
                    if (name is null || urlText is null || !Uri.TryCreate(urlText, UriKind.Absolute, out var url))
                        continue;
                    if (name.Equals("games-index.json", StringComparison.OrdinalIgnoreCase))
                        gamesIndexJson = await DownloadTextAssetAsync(url, 8 * 1024 * 1024, cancellationToken);
                    else if (name.Equals("generated-metadata.json", StringComparison.OrdinalIgnoreCase))
                        generatedMetadataJson = await DownloadTextAssetAsync(url, 8 * 1024 * 1024, cancellationToken);
                }
            }

            var index = ParseReleasePayload(
                releaseJson, assetPages, gamesIndexJson, generatedMetadataJson, etag, lastModified,
                DateTimeOffset.UtcNow);
            await WriteCachedIndexAsync(index, releaseJson, assetPages, gamesIndexJson, generatedMetadataJson,
                etag, cancellationToken);
            _ = tag;
            return index;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or
                                          IOException or UnauthorizedAccessException)
        {
            return await ReadCachedIndexAsync(cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await ReadCachedIndexAsync(cancellationToken);
        }
    }

    private async Task<(string Json, string? ETag, string? LastModified)?> SelectOfficialSnapshotReleaseAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (File.Exists(ETagPath) &&
            EntityTagHeaderValue.TryParse(await File.ReadAllTextAsync(ETagPath, cancellationToken), out var etag))
            request.Headers.IfNoneMatch.Add(etag);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            var cached = await ReadCachedRawReleaseAsync(cancellationToken);
            return cached is null ? null : (cached, response.Headers.ETag?.ToString(), null);
        }
        if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
            return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub releases listing must be a JSON array.");
        JsonElement? selected = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var name = release.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var prerelease = release.TryGetProperty("prerelease", out var preElement) &&
                preElement.ValueKind == JsonValueKind.True;
            if (tag is not null && tag.Contains("snapshot", StringComparison.OrdinalIgnoreCase) ||
                name is not null && name.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
            {
                selected = release;
                break;
            }
            if (selected is null && prerelease) selected = release;
        }
        selected ??= document.RootElement.EnumerateArray().FirstOrDefault();
        if (selected is null || selected.Value.ValueKind != JsonValueKind.Object) return null;
        var selectedJson = selected.Value.GetRawText();
        return (selectedJson, response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToString("R"));
    }

    private async Task<IReadOnlyList<string>> FetchAllAssetPagesAsync(long releaseId, CancellationToken cancellationToken)
    {
        var pages = new List<string>();
        for (var page = 1; page <= 20; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repository}/releases/{releaseId}/assets?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
                break;
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                break;
            pages.Add(json);
            if (document.RootElement.GetArrayLength() < 100) break;
        }
        return pages;
    }

    private async Task<string?> DownloadTextAssetAsync(Uri url, int maximumBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        await ArtifactValidator.CopyWithLimitAsync(input, buffer, maximumBytes, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length >= 1 && bytes[0] == (byte)'<')
            throw new InvalidDataException("Snapshot metadata download returned HTML instead of JSON.");
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return Encoding.UTF8.GetString(bytes.AsSpan(offset));
    }

    private RenoDxSnapshotResolution Success(
        RenoDxSnapshotReleaseIndex index,
        RenoDxSnapshotAsset asset,
        string reason,
        bool usedStructured,
        PeArchitecture architecture)
    {
        var selection = new ArtifactSelection(
            ComponentKind.RenoDx,
            index.ReleaseTag,
            asset.DownloadUrl,
            index.ReleaseTag,
            asset.Name,
            architecture,
            null,
            asset.Name,
            asset.Digest,
            ArtifactArchiveKind.None,
            Support: ArtifactSupportKind.ExactGameProfile,
            SourceValidatedByOfficialMetadata: true,
            GameProfile: $"snapshot:{index.ReleaseId}:{asset.Id}");
        return new(
            RenoDxSnapshotResolveState.ExactAddonAvailableFromOfficialSnapshotRelease,
            selection,
            index,
            asset,
            reason,
            usedStructured,
            false,
            [],
            null,
            index.ReleaseCommit,
            asset.Digest);
    }

    private static RenoDxSnapshotResolution Ambiguous(
        RenoDxSnapshotReleaseIndex index,
        IReadOnlyList<RenoDxSnapshotAsset> candidates,
        string reason) =>
        new(RenoDxSnapshotResolveState.AmbiguousSnapshotAssets, null, index, null, reason,
            index.UsedStructuredMetadata, false,
            candidates.Select(item => item.Name).ToArray(),
            "Multiple official snapshot addon assets match this game.",
            index.ReleaseCommit, null);

    private static (RenoDxSnapshotAsset Asset, string Reason)? TryMatchStructured(
        RenoDxSnapshotReleaseIndex index,
        SteamGame game,
        RenoDxCatalogEntry? entry,
        PeArchitecture architecture)
    {
        if (index.Games.Count == 0) return null;
        RenoDxSnapshotGameMapping? mapping = null;
        foreach (var candidate in index.Games)
        {
            if (candidate.SteamAppId is uint appId && appId == game.AppId)
            {
                mapping = candidate;
                break;
            }
        }
        if (mapping is null && entry is not null)
        {
            var key = RenoDxIdentity.NormalizeKey(entry.CanonicalName);
            mapping = index.Games.FirstOrDefault(item =>
                RenoDxIdentity.NormalizeKey(item.Title) == key ||
                item.Aliases.Any(alias => RenoDxIdentity.NormalizeKey(alias) == key) ||
                DeriveSlug(item.Title) == DeriveSlug(entry.CanonicalName) ||
                item.Id.Equals(entry.ArtifactSlug, StringComparison.OrdinalIgnoreCase) ||
                item.Id.Equals(DeriveSlug(entry.CanonicalName), StringComparison.OrdinalIgnoreCase));
        }
        if (mapping is null)
        {
            var gameKey = RenoDxIdentity.NormalizeKey(game.Name);
            mapping = index.Games.FirstOrDefault(item =>
                RenoDxIdentity.NormalizeKey(item.Title) == gameKey ||
                item.Aliases.Any(alias => RenoDxIdentity.NormalizeKey(alias) == gameKey) ||
                DeriveSlug(item.Title) == DeriveSlug(game.Name));
        }
        if (mapping is null) return null;

        var preferredNames = mapping.ArtifactNames
            .Where(name => name.EndsWith(architecture == PeArchitecture.X86 ? ".addon32" : ".addon64",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var name in preferredNames)
        {
            if (index.ByExactName.TryGetValue(name, out var asset))
                return (asset, "structured-metadata");
        }
        foreach (var name in mapping.ArtifactNames)
        {
            if (index.ByExactName.TryGetValue(name, out var asset) &&
                (architecture == PeArchitecture.Unknown || asset.Architecture == architecture))
                return (asset, "structured-metadata");
        }
        if (index.BySlug.TryGetValue(mapping.Id, out var slugAssets))
        {
            var match = slugAssets.FirstOrDefault(item => item.Architecture == architecture) ??
                slugAssets.FirstOrDefault();
            if (match is not null) return (match, "structured-metadata-slug");
        }
        return null;
    }

    private static IEnumerable<RenoDxSnapshotAsset> ParseAssetArray(JsonElement array, string releaseTag)
    {
        foreach (var asset in array.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var match = AddonNamePattern.Match(name);
            if (!match.Success) continue;
            if (!asset.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
                continue;
            var urlText = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString() : null;
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url)) continue;
            if (!OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(url)) continue;
            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize : 0;
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            DateTimeOffset? updated = null;
            if (asset.TryGetProperty("updated_at", out var updatedElement) &&
                updatedElement.TryGetDateTimeOffset(out var parsedUpdated))
                updated = parsedUpdated;
            var architecture = match.Groups["arch"].Value == "32" ? PeArchitecture.X86 : PeArchitecture.X64;
            yield return new(
                id,
                name,
                name.ToLowerInvariant(),
                match.Groups["slug"].Value.ToLowerInvariant(),
                architecture,
                url,
                size,
                digest,
                updated);
        }
        _ = releaseTag;
    }

    private static (IReadOnlyList<RenoDxSnapshotGameMapping> Games, string? Commit) ParseGeneratedMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return ([], null);
        if (root.TryGetProperty("schema_version", out var versionElement))
        {
            var version = versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : versionElement.ToString();
            if (version is not null && !(version.StartsWith("1.", StringComparison.Ordinal) || version == "1"))
                return ([], null);
        }
        var commit = root.TryGetProperty("source", out var source) &&
            source.TryGetProperty("commit", out var commitNode) &&
            commitNode.TryGetProperty("sha", out var sha)
            ? sha.GetString()
            : null;
        var games = new List<RenoDxSnapshotGameMapping>();
        if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
            return (games, commit);
        foreach (var mod in mods.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object) continue;
            var id = mod.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var title = mod.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            uint? appId = null;
            string? exe = null;
            if (mod.TryGetProperty("deploy", out var deploy) && deploy.ValueKind == JsonValueKind.Object)
            {
                if (deploy.TryGetProperty("steam_appid", out var appElement))
                    appId = TryReadUInt(appElement);
                if (deploy.TryGetProperty("game_exe", out var exeElement))
                    exe = exeElement.GetString();
            }
            var artifacts = new List<string>();
            if (mod.TryGetProperty("artifacts", out var artifactArray) && artifactArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var artifact in artifactArray.EnumerateArray())
                {
                    var name = artifact.TryGetProperty("name", out var artifactName) ? artifactName.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name) && AddonNamePattern.IsMatch(name))
                        artifacts.Add(name);
                }
            }
            games.Add(new(id, title, [], appId, exe, artifacts));
        }
        return (games, commit);
    }

    private static IReadOnlyList<RenoDxSnapshotGameMapping> ParseGamesIndex(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("games", out var gamesElement) ||
            gamesElement.ValueKind != JsonValueKind.Array)
            return [];
        var games = new List<RenoDxSnapshotGameMapping>();
        foreach (var game in gamesElement.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object) continue;
            var id = game.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var title = game.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            var aliases = new List<string>();
            if (game.TryGetProperty("aliases", out var aliasElement) && aliasElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var alias in aliasElement.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                        aliases.Add(alias.GetString()!);
                }
            }
            uint? appId = null;
            string? exe = null;
            if (game.TryGetProperty("steam_appid", out var appElement))
                appId = TryReadUInt(appElement);
            if (game.TryGetProperty("deploy", out var deploy) && deploy.ValueKind == JsonValueKind.Object)
            {
                if (appId is null && deploy.TryGetProperty("steam_appid", out var deployApp))
                    appId = TryReadUInt(deployApp);
                if (deploy.TryGetProperty("game_exe", out var exeElement))
                    exe = exeElement.GetString();
            }
            var artifacts = new List<string>();
            if (game.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in mods.EnumerateArray())
                {
                    if (!mod.TryGetProperty("artifacts", out var artifactArray) ||
                        artifactArray.ValueKind != JsonValueKind.Array) continue;
                    foreach (var artifact in artifactArray.EnumerateArray())
                    {
                        var name = artifact.TryGetProperty("name", out var artifactName)
                            ? artifactName.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name) && AddonNamePattern.IsMatch(name))
                            artifacts.Add(name);
                    }
                }
            }
            games.Add(new(id, title, aliases, appId, exe, artifacts));
        }
        return games;
    }

    private static void MergeGames(List<RenoDxSnapshotGameMapping> target, IReadOnlyList<RenoDxSnapshotGameMapping> source)
    {
        foreach (var candidate in source)
        {
            var index = target.FindIndex(item => item.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                target.Add(candidate);
                continue;
            }
            var existing = target[index];
            var aliases = existing.Aliases.Concat(candidate.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var artifacts = existing.ArtifactNames.Concat(candidate.ArtifactNames)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            target[index] = existing with
            {
                Aliases = aliases,
                SteamAppId = existing.SteamAppId ?? candidate.SteamAppId,
                GameExecutable = existing.GameExecutable ?? candidate.GameExecutable,
                ArtifactNames = artifacts
            };
        }
    }

    private async Task WriteCachedIndexAsync(
        RenoDxSnapshotReleaseIndex index,
        string releaseJson,
        IReadOnlyList<string> assetPages,
        string? gamesIndexJson,
        string? generatedMetadataJson,
        string? etag,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheDirectory);
        var payload = new
        {
            schemaVersion = IndexSchemaVersion,
            releaseId = index.ReleaseId,
            releaseTag = index.ReleaseTag,
            releaseCommit = index.ReleaseCommit,
            pageUrl = index.PageUrl.ToString(),
            publishedAt = index.PublishedAt,
            retrievedUtc = index.RetrievedUtc,
            etag = index.ETag,
            lastModified = index.LastModified,
            usedStructuredMetadata = index.UsedStructuredMetadata,
            releaseJson,
            assetPages,
            gamesIndexJson,
            generatedMetadataJson
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await WriteAtomicAsync(IndexCachePath, json, cancellationToken);
        if (!string.IsNullOrWhiteSpace(etag))
            await WriteAtomicAsync(ETagPath, etag, cancellationToken);
        await WriteAtomicAsync(CheckedPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }

    private async Task<RenoDxSnapshotReleaseIndex?> ReadCachedIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(IndexCachePath)) return null;
        try
        {
            await using var stream = new FileStream(IndexCachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != IndexSchemaVersion)
                return null;
            var releaseJson = root.GetProperty("releaseJson").GetString() ??
                throw new InvalidDataException("Cached snapshot release JSON is missing.");
            var assetPages = root.TryGetProperty("assetPages", out var pages) && pages.ValueKind == JsonValueKind.Array
                ? pages.EnumerateArray().Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0).ToArray()
                : [];
            var gamesIndexJson = root.TryGetProperty("gamesIndexJson", out var games) ? games.GetString() : null;
            var generatedMetadataJson = root.TryGetProperty("generatedMetadataJson", out var generated)
                ? generated.GetString() : null;
            var etag = root.TryGetProperty("etag", out var etagElement) ? etagElement.GetString() : null;
            var lastModified = root.TryGetProperty("lastModified", out var lastModifiedElement)
                ? lastModifiedElement.GetString() : null;
            var retrieved = root.TryGetProperty("retrievedUtc", out var retrievedElement) &&
                retrievedElement.TryGetDateTimeOffset(out var retrievedUtc)
                ? retrievedUtc
                : DateTimeOffset.UtcNow;
            return ParseReleasePayload(releaseJson, assetPages, gamesIndexJson, generatedMetadataJson, etag,
                lastModified, retrieved);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                          UnauthorizedAccessException or KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<string?> ReadCachedRawReleaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(IndexCachePath)) return null;
        try
        {
            await using var stream = new FileStream(IndexCachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("releaseJson", out var release)
                ? release.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<bool> IsFreshAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CheckedPath)) return false;
        return DateTimeOffset.TryParse(await File.ReadAllTextAsync(CheckedPath, cancellationToken), out var checkedUtc) &&
               DateTimeOffset.UtcNow - checkedUtc < RefreshInterval;
    }

    private void Remember(RenoDxSnapshotReleaseIndex index) =>
        MemoryIndexes[CacheDirectory] = (index, DateTimeOffset.UtcNow);

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? NormalizeAddonFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = Path.GetFileName(value.Trim());
        return AddonNamePattern.IsMatch(name) ? name : null;
    }

    private static string BuildAddonFileName(string slug, PeArchitecture architecture) =>
        $"renodx-{slug}.addon{(architecture == PeArchitecture.X86 ? "32" : "64")}";

    private static bool HasX64Executable(SteamGame game) =>
        game.Candidates.Any(item => item.Architecture == PeArchitecture.X64) ||
        game.Executable is not null && File.Exists(game.Executable) &&
        SafeArchitecture(game.Executable) == PeArchitecture.X64;

    private static PeArchitecture SafeArchitecture(string path)
    {
        try { return ArtifactValidator.ValidatePe(path); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return PeArchitecture.Unknown;
        }
    }

    private static uint? TryReadUInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var number)) return number;
        if (element.ValueKind == JsonValueKind.String && uint.TryParse(element.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
