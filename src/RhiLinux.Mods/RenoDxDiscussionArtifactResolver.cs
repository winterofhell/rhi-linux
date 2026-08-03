using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum RenoDxDiscussionResolveState
{
    ExactAddonAvailableFromOfficialDiscussion,
    DirectAddonFoundNotYetValidated,
    OfficialPageAvailableNoDirectAddon,
    ManualDownloadRequired,
    MultipleOfficialFilesRequireConfirmation,
    AddonArchitectureMismatch,
    OfficialSourceTemporarilyUnavailable,
    OfficialSourceChanged,
    UnsafeArtifactRejected,
    NotTrustedSource
}

public sealed record RenoDxDiscussionCandidate(
    Uri CanonicalUrl,
    string FileName,
    PeArchitecture Architecture,
    string TrustedPostId,
    string TrustedAuthor,
    string TrustReason,
    bool FromAttachment);

public sealed record RenoDxDiscussionResolution(
    RenoDxDiscussionResolveState State,
    ArtifactSelection? Selection,
    Uri OfficialPageUrl,
    int? DiscussionNumber,
    string? ExpectedFileName,
    string? DeploymentRelativeDirectory,
    IReadOnlyList<string> CompatibilityWarnings,
    string? MinimumReshadeVersion,
    string? TrustedPostId,
    string? Warning,
    IReadOnlyList<RenoDxDiscussionCandidate> Candidates,
    bool UsedCache)
{
    public bool CanOpenOfficialPage => OfficialPageUrl is not null;
}

public sealed class RenoDxDiscussionArtifactResolver(HttpClient httpClient, XdgPaths paths)
{
    private const int MaximumHtmlBytes = 4 * 1024 * 1024;
    private static readonly Regex DiscussionUrlPattern = new(
        @"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/discussions/(?<number>\d+)/?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AddonFileNamePattern = new(
        @"\brenodx-[A-Za-z0-9._-]+\.addon(?:32|64)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DeploymentFolderPattern = new(
        @"\b(?:into|in|place|copy)\b[^.\n]{0,120}?\b((?:bin64|bin32|Binaries[/\\]Win64|Binaries[/\\]Win32|[A-Za-z0-9._-]+[/\\]bin64))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ReshadeVersionPattern = new(
        @"ReShade\s+([0-9]+(?:\.[0-9]+)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AddonUrlInTextPattern = new(
        @"https://(?:github\.com/user-attachments/files/\d+/[^\s""'<>]+\.addon(?:32|64)|[A-Za-z0-9.-]+\.github\.io/[^\s""'<>]+\.addon(?:32|64)|github\.com/[^\s""'<>]+/releases/download/[^\s""'<>]+\.addon(?:32|64))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly object MemoryGate = new();
    private static readonly Dictionary<string, (DateTime WriteTimeUtc, CachedDiscussion Cache)> MemoryCaches = new(StringComparer.Ordinal);
    private static readonly object InFlightGate = new();
    private static readonly Dictionary<string, Task<RenoDxDiscussionResolution>> InFlight = new(StringComparer.Ordinal);

    private string CacheDirectory => Path.Combine(paths.AppCacheDirectory, "metadata", "renodx-discussions");

    public async Task<RenoDxDiscussionResolution> ResolveAsync(
        Uri discussionUrl,
        RenoDxCatalogEntry officialRecord,
        PeArchitecture selectedArchitecture,
        bool allowNetwork,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!officialRecord.OfficialCatalogOrigin)
            return Failed(discussionUrl, RenoDxDiscussionResolveState.NotTrustedSource,
                "Discussion resolution requires a validated official RenoDX catalog record.");
        if (!IsOfficialDiscussionUrl(discussionUrl, out var owner, out var repo, out var number))
            return Failed(discussionUrl, RenoDxDiscussionResolveState.NotTrustedSource,
                "Only official GitHub Discussion URLs from the RenoDX catalog can be resolved.");
        if (officialRecord.DiscussionUrl is null || !SameDiscussion(officialRecord.DiscussionUrl, discussionUrl))
            return Failed(discussionUrl, RenoDxDiscussionResolveState.NotTrustedSource,
                "The Discussion URL is not the official page recorded by the validated RenoDX catalog.");

        var cacheKey = CachePath(owner, repo, number);
        if (allowNetwork && !forceRefresh)
        {
            Task<RenoDxDiscussionResolution>? shared;
            lock (InFlightGate) InFlight.TryGetValue(cacheKey, out shared);
            if (shared is not null) return await shared.WaitAsync(cancellationToken);
        }

        var operation = ResolveCoreAsync(discussionUrl, officialRecord, selectedArchitecture, allowNetwork,
            forceRefresh, owner, repo, number, cacheKey, cancellationToken);
        if (allowNetwork)
        {
            lock (InFlightGate) InFlight[cacheKey] = operation;
            try { return await operation; }
            finally
            {
                lock (InFlightGate)
                {
                    if (InFlight.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, operation))
                        InFlight.Remove(cacheKey);
                }
            }
        }
        return await operation;
    }

    private async Task<RenoDxDiscussionResolution> ResolveCoreAsync(
        Uri discussionUrl,
        RenoDxCatalogEntry officialRecord,
        PeArchitecture selectedArchitecture,
        bool allowNetwork,
        bool forceRefresh,
        string owner,
        string repo,
        int number,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var cached = await ReadCacheAsync(cacheKey, cancellationToken);
        if (!allowNetwork)
        {
            if (cached is null)
                return Failed(discussionUrl, RenoDxDiscussionResolveState.OfficialSourceTemporarilyUnavailable,
                    "No validated Discussion cache is available.");
            return Materialize(cached, discussionUrl, officialRecord, selectedArchitecture, number, usedCache: true);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, discussionUrl);
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
            request.Headers.Accept.ParseAdd("text/html");
            if (!forceRefresh && cached?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                return Materialize(cached, discussionUrl, officialRecord, selectedArchitecture, number, usedCache: true);

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumHtmlBytes)
                throw new InvalidDataException("The Discussion response exceeds the metadata size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var limited = new MemoryStream();
            await ArtifactValidator.CopyWithLimitAsync(stream, limited, MaximumHtmlBytes, cancellationToken);
            var html = Encoding.UTF8.GetString(limited.ToArray());
            if (LooksLikeLoginPage(html))
                throw new InvalidDataException("The Discussion response looks like a GitHub login page.");

            var parsed = ParseDiscussionHtml(html, discussionUrl, owner, repo);
            var cache = new CachedDiscussion(
                NormalizeDiscussionUrl(discussionUrl).AbsoluteUri,
                number,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified,
                DateTimeOffset.UtcNow,
                parsed.AuthorLogin,
                parsed.ExpectedFileName,
                parsed.DeploymentRelativeDirectory,
                parsed.MinimumReshadeVersion,
                parsed.CompatibilityWarnings.ToArray(),
                parsed.Candidates.Select(candidate => new CachedCandidate(
                    candidate.CanonicalUrl.AbsoluteUri,
                    candidate.FileName,
                    candidate.Architecture.ToString(),
                    candidate.TrustedPostId,
                    candidate.TrustedAuthor,
                    candidate.TrustReason,
                    candidate.FromAttachment)).ToArray());
            await WriteCacheAsync(cacheKey, cache, cancellationToken);
            return Materialize(cache, discussionUrl, officialRecord, selectedArchitecture, number, usedCache: false);
        }
        catch (Exception exception) when (IsRecoverableFetchFailure(exception, cancellationToken))
        {
            if (cached is not null)
            {
                var fallback = Materialize(cached, discussionUrl, officialRecord, selectedArchitecture, number, usedCache: true);
                return fallback with
                {
                    State = fallback.Selection is null
                        ? RenoDxDiscussionResolveState.OfficialSourceTemporarilyUnavailable
                        : fallback.State,
                    Warning = $"The current Discussion could not be refreshed; last known-good metadata is in use. {exception.Message}"
                };
            }
            return Failed(discussionUrl, RenoDxDiscussionResolveState.OfficialSourceTemporarilyUnavailable, exception.Message);
        }
    }

    private static RenoDxDiscussionResolution Materialize(
        CachedDiscussion cache,
        Uri discussionUrl,
        RenoDxCatalogEntry officialRecord,
        PeArchitecture selectedArchitecture,
        int number,
        bool usedCache)
    {
        var warnings = cache.CompatibilityWarnings.ToList();
        var expected = cache.ExpectedFileName ?? officialRecord.ExpectedAddonFileName ?? officialRecord.ArtifactFileName;
        var candidates = cache.Candidates.Select(item => new RenoDxDiscussionCandidate(
            new Uri(item.CanonicalUrl),
            item.FileName,
            Enum.TryParse<PeArchitecture>(item.Architecture, true, out var arch) ? arch : PeArchitecture.Unknown,
            item.TrustedPostId,
            item.TrustedAuthor,
            item.TrustReason,
            item.FromAttachment)).ToArray();

        if (candidates.Length == 0)
        {
            return new(RenoDxDiscussionResolveState.OfficialPageAvailableNoDirectAddon, null, discussionUrl, number,
                expected, cache.DeploymentRelativeDirectory, warnings, cache.MinimumReshadeVersion, null,
                "Official RenoDX page found. No direct addon download is available.", [], usedCache);
        }

        var architectureMatches = candidates.Where(item =>
            selectedArchitecture is PeArchitecture.Unknown || item.Architecture == selectedArchitecture).ToArray();
        if (architectureMatches.Length == 0)
            return new(RenoDxDiscussionResolveState.AddonArchitectureMismatch, null, discussionUrl, number, expected,
                cache.DeploymentRelativeDirectory, warnings, cache.MinimumReshadeVersion, null,
                "Addon found, but no compatible executable architecture is available.", candidates, usedCache);

        var selected = SelectDeterministicCandidate(architectureMatches, officialRecord, expected, selectedArchitecture);
        if (selected is null)
            return new(RenoDxDiscussionResolveState.MultipleOfficialFilesRequireConfirmation, null, discussionUrl, number,
                expected, cache.DeploymentRelativeDirectory, warnings, cache.MinimumReshadeVersion, null,
                "Multiple official Discussion addon files require confirmation.", candidates, usedCache);

        if (!IsAllowedAddonUrl(selected.CanonicalUrl))
            return new(RenoDxDiscussionResolveState.UnsafeArtifactRejected, null, discussionUrl, number, expected,
                cache.DeploymentRelativeDirectory, warnings, cache.MinimumReshadeVersion, selected.TrustedPostId,
                "A Discussion link failed addon URL safety checks.", candidates, usedCache);

        var selection = new ArtifactSelection(
            ComponentKind.RenoDx, "snapshot", selected.CanonicalUrl, $"discussion-{number}",
            selected.FileName, selected.Architecture, null, selected.FileName, null, ArtifactArchiveKind.None,
            Support: ArtifactSupportKind.ExecutableOrAliasProfile,
            SourceValidatedByOfficialMetadata: true,
            GameProfile: RenoDxIdentity.NormalizeKey(officialRecord.CanonicalName));

        return new(RenoDxDiscussionResolveState.ExactAddonAvailableFromOfficialDiscussion, selection, discussionUrl,
            number, expected, cache.DeploymentRelativeDirectory, warnings, cache.MinimumReshadeVersion,
            selected.TrustedPostId, null, candidates, usedCache);
    }

    public static ParsedDiscussion ParseDiscussionHtml(string html, Uri discussionUrl, string owner, string repo)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var expectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        string? deployment = null;
        string? reshadeVersion = null;
        var authorLogin = string.Empty;
        var candidates = new List<RenoDxDiscussionCandidate>();

        var authorNode = document.DocumentNode.SelectSingleNode(
            "//*[contains(@class,'gh-header-meta')]//a[contains(@data-hovercard-type,'user')]");
        var authorHref = authorNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(authorHref))
            authorLogin = authorHref.Trim('/').Split('/').Last();

        var comments = document.DocumentNode.SelectNodes(
            "//*[contains(concat(' ', normalize-space(@class), ' '), ' js-comment ') or contains(concat(' ', normalize-space(@class), ' '), ' timeline-comment ')]")
            ?? Enumerable.Empty<HtmlNode>();

        var index = 0;
        foreach (var comment in comments)
        {
            index++;
            var commentAuthorHref = comment.SelectSingleNode(".//a[contains(@data-hovercard-type,'user')]")
                ?.GetAttributeValue("href", string.Empty) ?? string.Empty;
            var author = commentAuthorHref.Trim('/').Split('/').LastOrDefault() ?? string.Empty;
            var isAuthor = !string.IsNullOrWhiteSpace(authorLogin) &&
                author.Equals(authorLogin, StringComparison.OrdinalIgnoreCase);
            var badgeText = comment.InnerText;
            var isMaintainer = badgeText.Contains("Maintainer", StringComparison.OrdinalIgnoreCase);
            var isMember = badgeText.Contains("Member", StringComparison.OrdinalIgnoreCase) ||
                badgeText.Contains("Collaborator", StringComparison.OrdinalIgnoreCase) ||
                badgeText.Contains("Owner", StringComparison.OrdinalIgnoreCase);
            var isAccepted = comment.GetAttributeValue("class", string.Empty)
                .Contains("discussion-comment--accepted", StringComparison.OrdinalIgnoreCase) ||
                comment.InnerHtml.Contains("Marked as answer", StringComparison.OrdinalIgnoreCase);
            var isOriginal = index == 1;
            if (!(isOriginal || isAuthor || isMaintainer || isMember || isAccepted))
                continue;

            var trustReason = isOriginal ? "original-post" :
                isAccepted ? "accepted-answer" :
                isMaintainer || isMember || author.Equals(owner, StringComparison.OrdinalIgnoreCase)
                    ? "repository-authority"
                    : "discussion-author";
            var postId = comment.GetAttributeValue("id", string.Empty);
            if (string.IsNullOrWhiteSpace(postId))
                postId = comment.SelectSingleNode(".//*[@id]")?.GetAttributeValue("id", $"post-{index}") ?? $"post-{index}";

            var body = comment.SelectSingleNode(".//*[contains(@class,'comment-body')]") ?? comment;
            var text = HtmlEntity.DeEntitize(body.InnerText);
            foreach (Match match in AddonFileNamePattern.Matches(text))
                expectedNames.Add(match.Value);
            if (deployment is null)
            {
                var folder = DeploymentFolderPattern.Match(text);
                if (folder.Success) deployment = NormalizeDeployment(folder.Groups[1].Value);
            }
            if (reshadeVersion is null)
            {
                var version = ReshadeVersionPattern.Match(text);
                if (version.Success) reshadeVersion = version.Groups[1].Value;
            }
            if (text.Contains("Optiscaler", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("SpecialK", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("external", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("inject", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("This RenoDX profile may conflict with other injectors. Test RenoDX and ReShade by themselves first.");
            }
            if (text.Contains("DOES NOT CONTAIN AN EXE", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("never run one that claims to be a renodx mod", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Official guidance: this mod does not contain an EXE. Reject any EXE claiming to be RenoDX.");
            }

            foreach (var anchor in body.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                var href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", string.Empty)).Trim();
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (!Uri.TryCreate(discussionUrl, href, out var absolute)) continue;
                if (!TryClassifyAddonCandidate(absolute, anchor.InnerText, out var fileName, out var architecture, out var attachment))
                    continue;
                candidates.Add(new(StripExpiringQuery(absolute), fileName, architecture, postId, author, trustReason, attachment));
            }

            foreach (Match match in AddonUrlInTextPattern.Matches(body.InnerHtml ?? string.Empty))
            {
                var raw = match.Value.TrimEnd(')', ']', '"', '\'', '>', ',');
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var absolute)) continue;
                if (!TryClassifyAddonCandidate(absolute, null, out var fileName, out var architecture, out var attachment))
                    continue;
                candidates.Add(new(StripExpiringQuery(absolute), fileName, architecture, postId, author, trustReason, attachment));
            }
        }

        var distinct = candidates
            .GroupBy(item => item.CanonicalUrl.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return new(authorLogin, expectedNames.FirstOrDefault(), deployment, reshadeVersion,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), distinct);
    }

    public static RenoDxDiscussionCandidate? SelectDeterministicCandidate(
        IReadOnlyList<RenoDxDiscussionCandidate> candidates,
        RenoDxCatalogEntry record,
        string? expectedFileName,
        PeArchitecture architecture)
    {
        static bool IsUnwanted(string fileName) =>
            fileName.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("experimental", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("old", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("archive", StringComparison.OrdinalIgnoreCase);

        var usable = candidates.Where(item => !IsUnwanted(item.FileName)).ToArray();
        if (usable.Length == 0) usable = candidates.ToArray();

        if (!string.IsNullOrWhiteSpace(expectedFileName))
        {
            var exact = usable.Where(item => item.FileName.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 1) return exact[0];
            if (exact.Length > 1) return null;
        }

        var slug = record.ArtifactSlug ?? RenoDxIdentity.ArtifactSlug(expectedFileName ?? record.CanonicalName);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = usable.Where(item =>
                RenoDxIdentity.ArtifactSlug(item.FileName).Equals(slug, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bySlug.Length == 1) return bySlug[0];
        }

        var byArch = usable.Where(item => item.Architecture == architecture).ToArray();
        if (byArch.Length == 1) return byArch[0];
        if (byArch.Length == 0 && usable.Length == 1) return usable[0];
        return null;
    }

    public static bool IsOfficialDiscussionUrl(Uri uri, out string owner, out string repo, out int number)
    {
        owner = repo = string.Empty;
        number = 0;
        var match = DiscussionUrlPattern.Match(NormalizeDiscussionUrl(uri).AbsoluteUri);
        if (!match.Success) return false;
        owner = match.Groups["owner"].Value;
        repo = match.Groups["repo"].Value;
        return int.TryParse(match.Groups["number"].Value, out number) && number > 0 &&
               repo.StartsWith("renodx", StringComparison.OrdinalIgnoreCase);
    }

    public static Uri NormalizeDiscussionUrl(Uri uri) =>
        new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty, Path = uri.AbsolutePath.TrimEnd('/') }.Uri;

    public static bool SameDiscussion(Uri left, Uri right) =>
        string.Equals(NormalizeDiscussionUrl(left).AbsoluteUri, NormalizeDiscussionUrl(right).AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryClassifyAddonCandidate(
        Uri uri,
        string? anchorText,
        out string fileName,
        out PeArchitecture architecture,
        out bool attachment)
    {
        fileName = string.Empty;
        architecture = PeArchitecture.Unknown;
        attachment = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Contains("/user-attachments/files/", StringComparison.OrdinalIgnoreCase);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        var pathName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (pathName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return false;

        if (pathName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ||
            pathName.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase))
            fileName = pathName;
        else if (!string.IsNullOrWhiteSpace(anchorText))
        {
            var named = AddonFileNamePattern.Match(HtmlEntity.DeEntitize(anchorText));
            if (!named.Success) return false;
            fileName = named.Value;
        }
        else return false;

        architecture = fileName.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)
            ? PeArchitecture.X86 : PeArchitecture.X64;
        return IsAllowedAddonUrl(StripExpiringQuery(uri), fileName);
    }

    private static bool IsAllowedAddonUrl(Uri uri, string? fileName = null)
    {
        var candidate = StripExpiringQuery(uri);
        if (IsTrustedGithubAttachment(candidate)) return true;
        if (OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(candidate)) return true;
        if (OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(candidate)) return true;
        if (!string.IsNullOrWhiteSpace(fileName) &&
            candidate.Host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return OfficialArtifactSourcePolicy.IsTrustedOfficialWikiAddon(
                    new Uri($"{candidate.Scheme}://{candidate.Host}{candidate.AbsolutePath}"));
            }
            catch (UriFormatException)
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsTrustedGithubAttachment(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Contains("/user-attachments/files/", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase));

    private static Uri StripExpiringQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return uri;
        if (IsTrustedGithubAttachment(uri) ||
            uri.Host.Contains("githubusercontent", StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(uri) { Query = string.Empty }.Uri;
        return uri;
    }

    private static string? NormalizeDeployment(string value)
    {
        var normalized = value.Replace('\\', '/').Trim().Trim('/');
        if (normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(normalized)) return null;
        return normalized;
    }

    private static bool LooksLikeLoginPage(string html) =>
        html.Contains("name=\"password\"", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("/session", StringComparison.OrdinalIgnoreCase) ||
        (html.Contains("Sign in to GitHub", StringComparison.OrdinalIgnoreCase) &&
         html.Contains("login", StringComparison.OrdinalIgnoreCase) && html.Length < 200_000);

    private static bool IsRecoverableFetchFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private static RenoDxDiscussionResolution Failed(Uri page, RenoDxDiscussionResolveState state, string warning) =>
        new(state, null, page, null, null, null, [], null, null, warning, [], false);

    private string CachePath(string owner, string repo, int number) =>
        Path.Combine(CacheDirectory, $"{owner}.{repo}.{number}.json");

    private async Task<CachedDiscussion?> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var writeTime = File.GetLastWriteTimeUtc(path);
        lock (MemoryGate)
        {
            if (MemoryCaches.TryGetValue(path, out var memory) && memory.WriteTimeUtc == writeTime)
                return memory.Cache;
        }
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var cache = JsonSerializer.Deserialize<CachedDiscussion>(json);
            if (cache is null) return null;
            lock (MemoryGate) MemoryCaches[path] = (writeTime, cache);
            return cache;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(string path, CachedDiscussion cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cache, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, true);
            lock (MemoryGate) MemoryCaches[path] = (File.GetLastWriteTimeUtc(path), cache);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public sealed record ParsedDiscussion(
        string AuthorLogin,
        string? ExpectedFileName,
        string? DeploymentRelativeDirectory,
        string? MinimumReshadeVersion,
        IReadOnlyList<string> CompatibilityWarnings,
        IReadOnlyList<RenoDxDiscussionCandidate> Candidates);

    private sealed record CachedDiscussion(
        string DiscussionUrl,
        int DiscussionNumber,
        string? ETag,
        DateTimeOffset? LastModified,
        DateTimeOffset FetchedUtc,
        string AuthorLogin,
        string? ExpectedFileName,
        string? DeploymentRelativeDirectory,
        string? MinimumReshadeVersion,
        string[] CompatibilityWarnings,
        CachedCandidate[] Candidates);

    private sealed record CachedCandidate(
        string CanonicalUrl,
        string FileName,
        string Architecture,
        string TrustedPostId,
        string TrustedAuthor,
        string TrustReason,
        bool FromAttachment);
}
