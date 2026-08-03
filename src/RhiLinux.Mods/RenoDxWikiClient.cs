using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum RenoDxWikiStatus
{
    Working,
    InProgress,
    Unknown
}

public enum RenoDxWikiRecordOrigin
{
    ValidatedOfficialWiki
}

public enum RenoDxWikiFetchState
{
    Online,
    Offline,
    UnableToCheck
}

public sealed record RenoDxWikiRecord(
    string Name,
    Uri? Addon32Url,
    Uri? Addon64Url,
    RenoDxWikiStatus Status,
    RenoDxWikiRecordOrigin Origin)
{
    public Uri? GetAddonUri(PeArchitecture architecture) => architecture switch
    {
        PeArchitecture.X86 => Addon32Url,
        PeArchitecture.X64 => Addon64Url,
        _ => null
    };
}

public sealed record RenoDxWikiCatalog(
    IReadOnlyList<RenoDxWikiRecord> Records,
    RenoDxWikiFetchState State,
    bool IsCached,
    bool Changed,
    string? ETag,
    string? Warning)
{
    public IReadOnlyList<RenoDxCatalogEntry> Entries { get; init; } = [];
    public RenoDxCatalogIndex? Index { get; init; }
    public DateTimeOffset? SourceTimestamp { get; init; }
    public TimeSpan? CacheAge { get; init; }
}

public sealed class RenoDxWikiClient(HttpClient httpClient, XdgPaths paths)
{
    private const int MaximumMarkdownBytes = 8 * 1024 * 1024;
    private const int MaximumRecords = 4096;
    private const int SchemaVersion = RenoDxCatalogIndex.SchemaVersion;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex HttpUrlPattern = new(
        @"https?://[^\s<>()]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownLinkPattern = new(
        @"!?\[(?<text>[^\]]*)\]\((?<url>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex HtmlTagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex TooltipPattern = new(
        @"\[(?::white_check_mark:|:construction:|✅|🚧)\]\(#\s*""(?<note>[^""]*)""\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Uri SourceUri =
        new("https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md");

    private static readonly object MemoryGate = new();
    private static readonly Dictionary<string, (DateTime WriteTimeUtc, CachedCatalog Catalog)> MemoryCaches = new(StringComparer.Ordinal);
    private static readonly object InFlightGate = new();
    private static readonly Dictionary<string, Task<RenoDxWikiCatalog>> InFlightRequests = new(StringComparer.Ordinal);

    private string CacheDirectory => Path.Combine(paths.AppCacheDirectory, "metadata", "renodx-wiki");
    private string MarkdownCachePath => Path.Combine(CacheDirectory, "Mods.md");
    private string ETagCachePath => Path.Combine(CacheDirectory, "Mods.md.etag");
    private string MetaCachePath => Path.Combine(CacheDirectory, "Mods.meta");

    public async Task<RenoDxWikiCatalog> GetAsync(
        bool allowNetwork,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var cacheKey = MarkdownCachePath;
        if (allowNetwork && !forceRefresh)
        {
            Task<RenoDxWikiCatalog>? shared;
            lock (InFlightGate) InFlightRequests.TryGetValue(cacheKey, out shared);
            if (shared is not null) return await shared.WaitAsync(cancellationToken);
        }

        var operation = FetchCoreAsync(allowNetwork, forceRefresh, cancellationToken);
        if (allowNetwork)
        {
            lock (InFlightGate) InFlightRequests[cacheKey] = operation;
            try { return await operation; }
            finally
            {
                lock (InFlightGate)
                {
                    if (InFlightRequests.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, operation))
                        InFlightRequests.Remove(cacheKey);
                }
            }
        }
        return await operation;
    }

    private async Task<RenoDxWikiCatalog> FetchCoreAsync(
        bool allowNetwork,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cached = await ReadCachedAsync(cancellationToken);
        if (!allowNetwork)
        {
            return cached is null
                ? new([], RenoDxWikiFetchState.Offline, false, false, null,
                    "No validated RenoDX wiki cache is available.")
                : ToCatalog(cached, RenoDxWikiFetchState.Offline, true, false, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SourceUri);
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
            if (!forceRefresh && cached?.ETag is { } cachedTag &&
                EntityTagHeaderValue.TryParse(cachedTag, out var conditionalTag))
                request.Headers.IfNoneMatch.Add(conditionalTag);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (cached is null)
                    throw new InvalidDataException("The RenoDX wiki returned Not Modified without a validated cache.");
                return ToCatalog(cached, RenoDxWikiFetchState.Online, true, false, null);
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumMarkdownBytes)
                throw new InvalidDataException("The RenoDX wiki response exceeds the metadata size limit.");
            var bytes = await ReadBoundedResponseAsync(response.Content, cancellationToken);
            var markdown = StrictUtf8.GetString(bytes);
            var entries = ParseCatalog(markdown);
            var records = ToLegacyRecords(entries);
            var etag = response.Headers.ETag?.ToString();
            var lastModified = response.Content.Headers.LastModified;
            var changed = cached is not null && !CatalogsEqual(cached.Entries, entries);

            var cacheWritten = false;
            string? cacheWarning = null;
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                await WriteAtomicAsync(MarkdownCachePath, bytes, cancellationToken);
                cacheWritten = true;
                InvalidateMemoryCache(MarkdownCachePath);
                if (etag is not null)
                    await WriteAtomicAsync(ETagCachePath, Encoding.UTF8.GetBytes(etag), cancellationToken);
                else if (File.Exists(ETagCachePath))
                    File.Delete(ETagCachePath);
                var meta = $"schema={SchemaVersion}\nsourceUtc={(lastModified ?? DateTimeOffset.UtcNow):O}\n";
                await WriteAtomicAsync(MetaCachePath, Encoding.UTF8.GetBytes(meta), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cacheWarning = $"The validated RenoDX wiki was loaded, but its cache could not be updated: {exception.Message}";
            }

            var fresh = new CachedCatalog(entries, records, etag, lastModified ?? DateTimeOffset.UtcNow, TimeSpan.Zero);
            lock (MemoryGate)
            {
                MemoryCaches[MarkdownCachePath] = (
                    File.Exists(MarkdownCachePath) ? File.GetLastWriteTimeUtc(MarkdownCachePath) : DateTime.UtcNow,
                    fresh);
            }
            return ToCatalog(fresh, RenoDxWikiFetchState.Online, cacheWritten, changed, cacheWarning);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                                           IOException or UnauthorizedAccessException or DecoderFallbackException or
                                           RegexMatchTimeoutException)
        {
            return cached is null
                ? new([], RenoDxWikiFetchState.UnableToCheck, false, false, null, exception.Message)
                : ToCatalog(cached, RenoDxWikiFetchState.Offline, true, false,
                    $"The current RenoDX wiki could not be validated; the last known-good catalog is in use. {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return cached is null
                ? new([], RenoDxWikiFetchState.UnableToCheck, false, false, null,
                    "The RenoDX wiki request timed out.")
                : ToCatalog(cached, RenoDxWikiFetchState.Offline, true, false,
                    "The RenoDX wiki request timed out; the last known-good catalog is in use.");
        }
    }

    public static IReadOnlyList<RenoDxWikiRecord> ParseMarkdown(string markdown) =>
        ToLegacyRecords(ParseCatalog(markdown));

    public static IReadOnlyList<RenoDxCatalogEntry> ParseCatalog(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (Encoding.UTF8.GetByteCount(markdown) > MaximumMarkdownBytes)
            throw new InvalidDataException("The RenoDX wiki response exceeds the metadata size limit.");

        var entries = new Dictionary<string, RenoDxCatalogEntry>(StringComparer.Ordinal);
        var section = WikiParseSection.None;
        TableColumns? table = null;
        var foundModTable = false;
        using var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart('\uFEFF', ' ', '\t');
            if (TryReadHeading(trimmed, out var level, out var heading))
            {
                table = null;
                if (level == 1 && heading.Equals("List", StringComparison.OrdinalIgnoreCase))
                {
                    section = WikiParseSection.Primary;
                    continue;
                }
                if (level == 1 && heading.StartsWith("Related", StringComparison.OrdinalIgnoreCase))
                {
                    section = WikiParseSection.Related;
                    continue;
                }
                if (level == 1 && heading.StartsWith("Deprecated", StringComparison.OrdinalIgnoreCase))
                {
                    section = WikiParseSection.Deprecated;
                    continue;
                }
                if (section == WikiParseSection.None) continue;
                if (level >= 2 && heading.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase))
                {
                    section = WikiParseSection.GenericUnreal;
                    TryAddGenericEngineEntry(entries, heading, GameEngine.Unreal);
                    continue;
                }
                if (level >= 2 && heading.Contains("Unity Engine", StringComparison.OrdinalIgnoreCase))
                {
                    section = WikiParseSection.GenericUnity;
                    TryAddGenericEngineEntry(entries, heading, GameEngine.Unity);
                    continue;
                }
                if (level >= 2 && heading.Contains("Multi-Game", StringComparison.OrdinalIgnoreCase))
                {
                    section = WikiParseSection.GenericUnreal;
                    continue;
                }
                continue;
            }

            if (section == WikiParseSection.None) continue;
            if (section is WikiParseSection.GenericUnity or WikiParseSection.GenericUnreal &&
                !trimmed.StartsWith('|') &&
                (trimmed.Contains("unityengine", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.Contains("unrealengine", StringComparison.OrdinalIgnoreCase)))
            {
                TryAddGenericEngineEntry(entries, trimmed,
                    section == WikiParseSection.GenericUnity ? GameEngine.Unity : GameEngine.Unreal);
            }
            if (!trimmed.StartsWith('|'))
            {
                if (trimmed.StartsWith('#')) table = null;
                continue;
            }

            var cells = SplitTableRow(line);
            if (TryReadHeader(cells, out var header))
            {
                table = header;
                if (header.Links >= 0) foundModTable = true;
                continue;
            }
            if (LooksLikeAnotherTableHeader(cells))
            {
                if (TryReadVerifiedHeader(cells, out var verifiedHeader))
                    table = verifiedHeader;
                else
                    table = null;
                continue;
            }
            if (table is not { } columns || IsSeparatorRow(cells) || cells.Count <= columns.MaximumIndex) continue;

            var nameCell = cells[columns.Name];
            var name = CleanName(nameCell);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Length > 512)
                throw new InvalidDataException("A RenoDX wiki game name exceeds the metadata size limit.");

            var maintainer = columns.Maintainer >= 0 && columns.Maintainer < cells.Count
                ? CleanName(cells[columns.Maintainer]) : null;
            var linksCell = columns.Links >= 0 && columns.Links < cells.Count ? cells[columns.Links] : string.Empty;
            var statusCell = columns.Status >= 0 && columns.Status < cells.Count ? cells[columns.Status] : string.Empty;
            var notesCell = columns.Notes >= 0 && columns.Notes < cells.Count ? cells[columns.Notes] : string.Empty;
            var (addon32, addon64, sourceType) = ReadAddonUrls(linksCell, name);
            var discussionUrl = ExtractDiscussionUrl(nameCell, linksCell, notesCell);
            if (discussionUrl is not null && sourceType is RenoDxSourceType.Unknown or RenoDxSourceType.Nexus or RenoDxSourceType.Discord)
                sourceType = RenoDxSourceType.Discussion;
            var expectedAddon = ExtractExpectedAddonFileName(nameCell, linksCell, notesCell, statusCell);
            var status = ReadStatus(statusCell);
            var statusNote = ReadStatusNote(statusCell, notesCell);
            var catalogSection = MapSection(section, addon32, addon64, statusNote);
            if (catalogSection == RenoDxCatalogSection.Related && section != WikiParseSection.Related)
                catalogSection = RenoDxCatalogSection.ExactGame;

            if (section == WikiParseSection.Related)
                catalogSection = RenoDxCatalogSection.Related;
            else if (section == WikiParseSection.Deprecated)
                catalogSection = RenoDxCatalogSection.Deprecated;
            else if (section is WikiParseSection.GenericUnreal or WikiParseSection.GenericUnity && columns.Links < 0)
                catalogSection = section == WikiParseSection.GenericUnity
                    ? RenoDxCatalogSection.GenericUnity
                    : RenoDxCatalogSection.GenericUnreal;
            else if (addon32 is null && addon64 is null)
                catalogSection = RenoDxCatalogSection.ManualOnly;

            if (catalogSection == RenoDxCatalogSection.Related)
            {
                AddOrMerge(entries, BuildEntry(name, maintainer, addon32, addon64, status, statusNote,
                    RenoDxCatalogSection.Related, sourceType, false, discussionUrl, expectedAddon));
                continue;
            }

            if (catalogSection is RenoDxCatalogSection.GenericUnreal or RenoDxCatalogSection.GenericUnity &&
                columns.Links < 0)
            {
                AddOrMerge(entries, BuildEntry(name, maintainer, null, null, status, statusNote, catalogSection,
                    RenoDxSourceType.GenericAddon, false, null, null));
                continue;
            }

            var direct = addon32 is not null || addon64 is not null;
            if (!direct && catalogSection != RenoDxCatalogSection.ManualOnly &&
                catalogSection != RenoDxCatalogSection.Deprecated)
                continue;

            AddOrMerge(entries, BuildEntry(name, maintainer, addon32, addon64, status, statusNote, catalogSection,
                sourceType, direct && catalogSection != RenoDxCatalogSection.Deprecated, discussionUrl, expectedAddon));
            if (entries.Count > MaximumRecords)
                throw new InvalidDataException("The RenoDX wiki contains too many game records.");
        }

        var exactCount = entries.Values.Count(item => item.SourceSection is RenoDxCatalogSection.ExactGame
            or RenoDxCatalogSection.ManualOnly);
        if (!foundModTable || exactCount == 0)
            throw new InvalidDataException("The RenoDX wiki does not contain a usable Name/Links/Status mod table.");
        return entries.Values.OrderBy(entry => entry.CanonicalName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static RenoDxWikiCatalog ToCatalog(
        CachedCatalog cached,
        RenoDxWikiFetchState state,
        bool isCached,
        bool changed,
        string? warning) =>
        new(cached.Records, state, isCached, changed, cached.ETag, warning)
        {
            Entries = cached.Entries,
            SourceTimestamp = cached.SourceTimestamp,
            CacheAge = cached.CacheAge,
            Index = new RenoDxCatalogIndex(cached.Entries, cached.SourceTimestamp, cached.ETag, state, isCached,
                cached.CacheAge)
        };

    private static IReadOnlyList<RenoDxWikiRecord> ToLegacyRecords(IReadOnlyList<RenoDxCatalogEntry> entries) =>
        entries.Where(entry => entry.SourceSection == RenoDxCatalogSection.ExactGame &&
                               (entry.Addon32Url is not null || entry.Addon64Url is not null))
            .Select(entry => new RenoDxWikiRecord(entry.CanonicalName, entry.Addon32Url, entry.Addon64Url,
                entry.Status, RenoDxWikiRecordOrigin.ValidatedOfficialWiki))
            .ToArray();

    private static RenoDxCatalogEntry BuildEntry(
        string name,
        string? maintainer,
        Uri? addon32,
        Uri? addon64,
        RenoDxWikiStatus status,
        string? statusNote,
        RenoDxCatalogSection section,
        RenoDxSourceType sourceType,
        bool direct,
        Uri? discussionUrl,
        string? expectedAddonFileName)
    {
        var fileName = Path.GetFileName(Uri.UnescapeDataString((addon64 ?? addon32)?.AbsolutePath ?? string.Empty));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = expectedAddonFileName;
        if (string.IsNullOrWhiteSpace(fileName)) fileName = null;
        var architecture = (addon32, addon64) switch
        {
            (not null, not null) => PeArchitecture.Unknown,
            (not null, null) => PeArchitecture.X86,
            (null, not null) => PeArchitecture.X64,
            _ when fileName?.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) == true => PeArchitecture.X86,
            _ when fileName?.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) == true => PeArchitecture.X64,
            _ => PeArchitecture.Unknown
        };
        if (addon32 is not null && addon64 is not null) architecture = PeArchitecture.X64;
        return new(
            name,
            RenoDxIdentity.NormalizeKey(name),
            [],
            string.IsNullOrWhiteSpace(maintainer) ? null : maintainer,
            addon32,
            addon64,
            fileName,
            fileName is null ? null : RenoDxIdentity.ArtifactSlug(fileName),
            architecture,
            status,
            section,
            sourceType,
            direct,
            statusNote,
            null,
            true,
            ExtractSupersededBy(statusNote))
        {
            OfficialPageUrl = discussionUrl,
            DiscussionUrl = discussionUrl,
            ExpectedAddonFileName = expectedAddonFileName ?? fileName
        };
    }

    private static Uri? ExtractDiscussionUrl(params string[] cells)
    {
        foreach (var cell in cells)
        {
            foreach (Match match in HttpUrlPattern.Matches(cell))
            {
                var raw = match.Value.TrimEnd(']', '}', ',', ';', '.', '"', '\'');
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;
                if (RenoDxDiscussionArtifactResolver.IsOfficialDiscussionUrl(uri, out _, out _, out _))
                    return RenoDxDiscussionArtifactResolver.NormalizeDiscussionUrl(uri);
            }
        }
        return null;
    }

    private static readonly Regex ExpectedAddonPattern = new(
        @"\brenodx-[A-Za-z0-9._-]+\.addon(?:32|64)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string? ExtractExpectedAddonFileName(params string[] cells)
    {
        foreach (var cell in cells)
        {
            var match = ExpectedAddonPattern.Match(WebUtility.HtmlDecode(cell));
            if (match.Success) return match.Value;
        }
        return null;
    }

    private static string? ExtractSupersededBy(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        const string marker = "Superseded by ";
        var index = note.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : note[(index + marker.Length)..].Trim();
    }

    private static void TryAddGenericEngineEntry(
        Dictionary<string, RenoDxCatalogEntry> entries,
        string text,
        GameEngine engine)
    {
        var (addon32, addon64, _) = ReadAddonUrls(text, engine.ToString());
        addon32 = PreferCanonicalEngineUrl(addon32, engine, PeArchitecture.X86);
        addon64 = PreferCanonicalEngineUrl(addon64, engine, PeArchitecture.X64);
        if (addon32 is null && addon64 is null) return;
        var name = engine == GameEngine.Unity ? "Unity Engine" : "Unreal Engine";
        AddOrMerge(entries, BuildEntry(name, null, addon32, addon64, RenoDxWikiStatus.Working, null,
            engine == GameEngine.Unity ? RenoDxCatalogSection.GenericUnity : RenoDxCatalogSection.GenericUnreal,
            RenoDxSourceType.GenericAddon, true, null, null));
    }

    private static Uri? PreferCanonicalEngineUrl(Uri? uri, GameEngine engine, PeArchitecture architecture)
    {
        if (uri is null) return null;
        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        var expected = engine == GameEngine.Unity
            ? architecture == PeArchitecture.X86 ? "renodx-unityengine.addon32" : "renodx-unityengine.addon64"
            : "renodx-unrealengine.addon64";
        if (fileName.Equals(expected, StringComparison.OrdinalIgnoreCase)) return uri;
        if (fileName.Contains("unityengine", StringComparison.OrdinalIgnoreCase) && engine == GameEngine.Unity)
            return uri;
        if (fileName.Contains("unrealengine", StringComparison.OrdinalIgnoreCase) && engine == GameEngine.Unreal)
            return uri;
        return null;
    }

    private static void AddOrMerge(Dictionary<string, RenoDxCatalogEntry> entries, RenoDxCatalogEntry candidate)
    {
        var key = $"{candidate.SourceSection}:{candidate.NormalizedCanonicalName}";
        if (entries.TryGetValue(key, out var existing))
            entries[key] = Merge(existing, candidate);
        else
            entries[key] = candidate;
    }

    private static RenoDxCatalogEntry Merge(RenoDxCatalogEntry existing, RenoDxCatalogEntry candidate)
    {
        var addon32 = MergeUrl(existing.CanonicalName, PeArchitecture.X86, existing.Addon32Url, candidate.Addon32Url);
        var addon64 = MergeUrl(existing.CanonicalName, PeArchitecture.X64, existing.Addon64Url, candidate.Addon64Url);
        var fileName = existing.ArtifactFileName ?? candidate.ArtifactFileName;
        return existing with
        {
            Addon32Url = addon32,
            Addon64Url = addon64,
            ArtifactFileName = fileName,
            ArtifactSlug = fileName is null ? existing.ArtifactSlug : RenoDxIdentity.ArtifactSlug(fileName),
            Status = ConservativeStatus(existing.Status, candidate.Status),
            StatusNote = existing.StatusNote ?? candidate.StatusNote,
            DirectAutomaticDownloadAvailable = existing.DirectAutomaticDownloadAvailable ||
                                               candidate.DirectAutomaticDownloadAvailable,
            SourceType = existing.SourceType == RenoDxSourceType.Snapshot ||
                         candidate.SourceType == RenoDxSourceType.Snapshot
                ? RenoDxSourceType.Snapshot
                : existing.SourceType == RenoDxSourceType.Discussion ||
                  candidate.SourceType == RenoDxSourceType.Discussion
                    ? RenoDxSourceType.Discussion
                    : existing.SourceType,
            SupersededBy = existing.SupersededBy ?? candidate.SupersededBy,
            Maintainer = existing.Maintainer ?? candidate.Maintainer,
            OfficialPageUrl = existing.OfficialPageUrl ?? candidate.OfficialPageUrl,
            DiscussionUrl = existing.DiscussionUrl ?? candidate.DiscussionUrl,
            ExpectedAddonFileName = existing.ExpectedAddonFileName ?? candidate.ExpectedAddonFileName,
            DeploymentRelativeDirectory = existing.DeploymentRelativeDirectory ?? candidate.DeploymentRelativeDirectory,
            CompatibilityWarnings = existing.CompatibilityWarnings.Count > 0
                ? existing.CompatibilityWarnings
                : candidate.CompatibilityWarnings,
            MinimumReshadeVersion = existing.MinimumReshadeVersion ?? candidate.MinimumReshadeVersion
        };
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var initialCapacity = content.Headers.ContentLength is > 0 and <= MaximumMarkdownBytes
            ? (int)content.Headers.ContentLength.Value
            : 81920;
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(initialCapacity);
        await ArtifactValidator.CopyWithLimitAsync(input, output, MaximumMarkdownBytes, cancellationToken);
        return output.ToArray();
    }

    private async Task<CachedCatalog?> ReadCachedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(MarkdownCachePath))
        {
            InvalidateMemoryCache(MarkdownCachePath);
            return null;
        }

        DateTime writeTimeUtc;
        try { writeTimeUtc = File.GetLastWriteTimeUtc(MarkdownCachePath); }
        catch (IOException) { return null; }

        lock (MemoryGate)
        {
            if (MemoryCaches.TryGetValue(MarkdownCachePath, out var cachedMemory) &&
                cachedMemory.WriteTimeUtc == writeTimeUtc)
                return cachedMemory.Catalog with { CacheAge = DateTime.UtcNow - writeTimeUtc };
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(MarkdownCachePath, cancellationToken);
            if (bytes.Length > MaximumMarkdownBytes) return null;
            var entries = ParseCatalog(StrictUtf8.GetString(bytes));
            string? etag = null;
            if (File.Exists(ETagCachePath))
            {
                var candidate = (await File.ReadAllTextAsync(ETagCachePath, cancellationToken)).Trim();
                if (EntityTagHeaderValue.TryParse(candidate, out _)) etag = candidate;
            }
            DateTimeOffset sourceTimestamp = writeTimeUtc;
            if (File.Exists(MetaCachePath))
            {
                foreach (var line in (await File.ReadAllTextAsync(MetaCachePath, cancellationToken))
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (line.StartsWith("sourceUtc=", StringComparison.OrdinalIgnoreCase) &&
                        DateTimeOffset.TryParse(line["sourceUtc=".Length..], out var parsed))
                        sourceTimestamp = parsed;
                }
            }
            var catalog = new CachedCatalog(entries, ToLegacyRecords(entries), etag, sourceTimestamp,
                DateTime.UtcNow - writeTimeUtc);
            lock (MemoryGate)
            {
                MemoryCaches[MarkdownCachePath] = (writeTimeUtc, catalog);
            }
            return catalog;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or DecoderFallbackException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static void InvalidateMemoryCache(string path)
    {
        lock (MemoryGate) MemoryCaches.Remove(path);
    }

    private static (Uri? Addon32, Uri? Addon64, RenoDxSourceType SourceType) ReadAddonUrls(string linksCell, string gameName)
    {
        var addon32 = new HashSet<Uri>();
        var addon64 = new HashSet<Uri>();
        var sourceType = RenoDxSourceType.Unknown;
        var isGenericEngineHeading = gameName.Equals("Unity", StringComparison.OrdinalIgnoreCase) ||
            gameName.Equals("Unreal", StringComparison.OrdinalIgnoreCase) ||
            gameName.Contains("Engine", StringComparison.OrdinalIgnoreCase);
        foreach (Match match in HttpUrlPattern.Matches(linksCell))
        {
            var raw = match.Value.TrimEnd(']', '}', ',', ';', '.', '"', '\'');
            var lower = raw.ToLowerInvariant();
            if (lower.Contains("nexusmods.com", StringComparison.Ordinal))
                sourceType = sourceType == RenoDxSourceType.Snapshot ? sourceType : RenoDxSourceType.Nexus;
            else if (lower.Contains("discord.com", StringComparison.Ordinal) ||
                     lower.Contains("discord.gg", StringComparison.Ordinal))
                sourceType = sourceType == RenoDxSourceType.Snapshot ? sourceType : RenoDxSourceType.Discord;
            else if (lower.Contains("/discussions/", StringComparison.Ordinal))
                sourceType = sourceType == RenoDxSourceType.Snapshot ? sourceType : RenoDxSourceType.Discussion;

            if (!raw.Contains(".addon32", StringComparison.OrdinalIgnoreCase) &&
                !raw.Contains(".addon64", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                throw new InvalidDataException($"RenoDX wiki entry '{gameName}' contains an unsafe addon URL.");
            if (!OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(uri))
                throw new InvalidDataException($"RenoDX wiki entry '{gameName}' contains an unsafe addon URL.");
            var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (isGenericEngineHeading &&
                !fileName.Contains("unityengine", StringComparison.OrdinalIgnoreCase) &&
                !fileName.Contains("unrealengine", StringComparison.OrdinalIgnoreCase))
                continue;
            var extension = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath));
            if (extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)) addon32.Add(uri);
            else if (extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) addon64.Add(uri);
            else
                throw new InvalidDataException(
                    $"RenoDX wiki entry '{gameName}' contains a misleading addon URL whose path does not end in .addon32 or .addon64.");
            sourceType = RenoDxSourceType.Snapshot;
        }

        if (addon32.Count > 1 || addon64.Count > 1)
            throw new InvalidDataException(
                $"RenoDX wiki entry '{gameName}' contains ambiguous download URLs for the same architecture.");
        return (addon32.SingleOrDefault(), addon64.SingleOrDefault(), sourceType);
    }

    private static Uri? MergeUrl(string name, PeArchitecture architecture, Uri? first, Uri? second)
    {
        if (first is null) return second;
        if (second is null || first.Equals(second)) return first;
        throw new InvalidDataException(
            $"RenoDX wiki contains ambiguous {architecture} download URLs for '{name}'.");
    }

    private static RenoDxWikiStatus ConservativeStatus(RenoDxWikiStatus first, RenoDxWikiStatus second)
    {
        if (first == RenoDxWikiStatus.InProgress || second == RenoDxWikiStatus.InProgress)
            return RenoDxWikiStatus.InProgress;
        if (first == RenoDxWikiStatus.Unknown || second == RenoDxWikiStatus.Unknown)
            return RenoDxWikiStatus.Unknown;
        return RenoDxWikiStatus.Working;
    }

    private static RenoDxWikiStatus ReadStatus(string value)
    {
        if (value.Contains(":construction:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("🚧", StringComparison.Ordinal))
            return RenoDxWikiStatus.InProgress;
        if (value.Contains(":white_check_mark:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("✅", StringComparison.Ordinal))
            return RenoDxWikiStatus.Working;
        if (value.Contains("Superseded", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Abandoned", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Not working", StringComparison.OrdinalIgnoreCase))
            return RenoDxWikiStatus.Unknown;
        return RenoDxWikiStatus.Unknown;
    }

    private static string? ReadStatusNote(string statusCell, string notesCell)
    {
        var tooltip = TooltipPattern.Match(statusCell);
        if (tooltip.Success) return WebUtility.HtmlDecode(tooltip.Groups["note"].Value).Trim();
        var cleaned = CleanName(string.IsNullOrWhiteSpace(notesCell) ? statusCell : notesCell);
        if (cleaned.Contains("Superseded", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("Abandoned", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("Not working", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("Requires", StringComparison.OrdinalIgnoreCase))
            return cleaned;
        return string.IsNullOrWhiteSpace(notesCell) ? null : CleanName(notesCell);
    }

    private static RenoDxCatalogSection MapSection(
        WikiParseSection section,
        Uri? addon32,
        Uri? addon64,
        string? statusNote) => section switch
        {
            WikiParseSection.Related => RenoDxCatalogSection.Related,
            WikiParseSection.Deprecated => RenoDxCatalogSection.Deprecated,
            WikiParseSection.GenericUnreal => RenoDxCatalogSection.GenericUnreal,
            WikiParseSection.GenericUnity => RenoDxCatalogSection.GenericUnity,
            _ when addon32 is null && addon64 is null => RenoDxCatalogSection.ManualOnly,
            _ => RenoDxCatalogSection.ExactGame
        };

    private static bool TryReadHeader(IReadOnlyList<string> cells, out TableColumns columns)
    {
        var name = FindHeader(cells, "name");
        var links = FindHeader(cells, "links", "download", "downloads");
        var status = FindHeader(cells, "status");
        var maintainer = FindHeader(cells, "maintainer", "author");
        var notes = FindHeader(cells, "notes", "note");
        if (name >= 0 && links >= 0 && status >= 0)
        {
            columns = new(name, links, status, maintainer, notes);
            return true;
        }
        columns = default;
        return false;
    }

    private static bool TryReadVerifiedHeader(IReadOnlyList<string> cells, out TableColumns columns)
    {
        var name = FindHeader(cells, "name");
        var status = FindHeader(cells, "status");
        var notes = FindHeader(cells, "notes", "note");
        if (name >= 0 && status >= 0 && FindHeader(cells, "links", "download", "downloads") < 0)
        {
            columns = new(name, -1, status, -1, notes);
            return true;
        }
        columns = default;
        return false;
    }

    private static bool LooksLikeAnotherTableHeader(IReadOnlyList<string> cells) =>
        FindHeader(cells, "name") >= 0 && FindHeader(cells, "status") >= 0;

    private static bool TryReadHeading(string line, out int level, out string heading)
    {
        level = 0;
        heading = string.Empty;
        if (!line.StartsWith('#')) return false;
        while (level < line.Length && line[level] == '#') level++;
        if (level == 0 || level > 6 || level >= line.Length || line[level] != ' ') return false;
        heading = line[(level + 1)..].Trim().TrimEnd('#').Trim();
        return heading.Length > 0;
    }

    private static int FindHeader(IReadOnlyList<string> cells, params string[] names)
    {
        for (var index = 0; index < cells.Count; index++)
        {
            var value = CleanName(cells[index]).Trim().ToLowerInvariant();
            if (names.Contains(value, StringComparer.Ordinal)) return index;
        }
        return -1;
    }

    private static bool IsSeparatorRow(IReadOnlyList<string> cells) => cells.Count > 0 && cells.All(cell =>
    {
        var value = cell.Trim().Trim(':');
        return value.Length >= 3 && value.All(character => character == '-');
    });

    private static List<string> SplitTableRow(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var start = line.IndexOf('|');
        for (var index = start + 1; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && index + 1 < line.Length && line[index + 1] == '|')
            {
                cell.Append('|');
                index++;
                continue;
            }
            if (character == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }
            cell.Append(character);
        }
        if (cell.Length > 0) cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static string CleanName(string value)
    {
        var withoutImages = MarkdownLinkPattern.Replace(value, match =>
            match.Value.StartsWith('!') ? string.Empty : match.Groups["text"].Value);
        var decoded = WebUtility.HtmlDecode(HtmlTagPattern.Replace(withoutImages, string.Empty));
        return WhitespacePattern.Replace(decoded, " ").Trim().Trim('*', '_', '`');
    }

    private static bool CatalogsEqual(
        IReadOnlyList<RenoDxCatalogEntry> first,
        IReadOnlyList<RenoDxCatalogEntry> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.CanonicalName == pair.Second.CanonicalName &&
            pair.First.Addon32Url == pair.Second.Addon32Url &&
            pair.First.Addon64Url == pair.Second.Addon64Url &&
            pair.First.Status == pair.Second.Status &&
            pair.First.SourceSection == pair.Second.SourceSection &&
            pair.First.DiscussionUrl == pair.Second.DiscussionUrl &&
            pair.First.ExpectedAddonFileName == pair.Second.ExpectedAddonFileName);

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record CachedCatalog(
        IReadOnlyList<RenoDxCatalogEntry> Entries,
        IReadOnlyList<RenoDxWikiRecord> Records,
        string? ETag,
        DateTimeOffset SourceTimestamp,
        TimeSpan CacheAge);

    private readonly record struct TableColumns(int Name, int Links, int Status, int Maintainer, int Notes)
    {
        public int MaximumIndex => new[] { Name, Links, Status, Maintainer, Notes }.Where(index => index >= 0)
            .DefaultIfEmpty(0).Max();
    }

    private enum WikiParseSection
    {
        None,
        Primary,
        GenericUnreal,
        GenericUnity,
        Related,
        Deprecated
    }
}
