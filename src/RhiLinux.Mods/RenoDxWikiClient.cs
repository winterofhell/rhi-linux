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
    string? Warning);

public sealed class RenoDxWikiClient(HttpClient httpClient, XdgPaths paths)
{
    private const int MaximumMarkdownBytes = 8 * 1024 * 1024;
    private const int MaximumRecords = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex HttpUrlPattern = new(
        @"https?://[^\s<>()]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownLinkPattern = new(
        @"!?\[(?<text>[^\]]*)\]\([^)]*\)",
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

    public static readonly Uri SourceUri =
        new("https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md");

    private string CacheDirectory => Path.Combine(paths.AppCacheDirectory, "metadata", "renodx-wiki");
    private string MarkdownCachePath => Path.Combine(CacheDirectory, "Mods.md");
    private string ETagCachePath => Path.Combine(CacheDirectory, "Mods.md.etag");

    public async Task<RenoDxWikiCatalog> GetAsync(
        bool allowNetwork,
        CancellationToken cancellationToken = default)
    {
        var cached = await ReadCachedAsync(cancellationToken);
        if (!allowNetwork)
        {
            return cached is null
                ? new([], RenoDxWikiFetchState.Offline, false, false, null,
                    "No validated RenoDX wiki cache is available.")
                : new(cached.Records, RenoDxWikiFetchState.Offline, true, false, cached.ETag, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SourceUri);
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
            if (cached?.ETag is { } cachedTag && EntityTagHeaderValue.TryParse(cachedTag, out var conditionalTag))
                request.Headers.IfNoneMatch.Add(conditionalTag);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (cached is null)
                    throw new InvalidDataException("The RenoDX wiki returned Not Modified without a validated cache.");
                return new(cached.Records, RenoDxWikiFetchState.Online, true, false, cached.ETag, null);
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumMarkdownBytes)
                throw new InvalidDataException("The RenoDX wiki response exceeds the metadata size limit.");
            var bytes = await ReadBoundedResponseAsync(response.Content, cancellationToken);
            var markdown = StrictUtf8.GetString(bytes);
            var records = ParseMarkdown(markdown);
            var etag = response.Headers.ETag?.ToString();
            var changed = cached is not null && !CatalogsEqual(cached.Records, records);

            var cacheWritten = false;
            string? cacheWarning = null;
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                await WriteAtomicAsync(MarkdownCachePath, bytes, cancellationToken);
                cacheWritten = true;
                if (etag is not null)
                    await WriteAtomicAsync(ETagCachePath, Encoding.UTF8.GetBytes(etag), cancellationToken);
                else if (File.Exists(ETagCachePath))
                    File.Delete(ETagCachePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cacheWarning = $"The validated RenoDX wiki was loaded, but its cache could not be updated: {exception.Message}";
            }

            return new(records, RenoDxWikiFetchState.Online, cacheWritten, changed, etag, cacheWarning);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                                           IOException or UnauthorizedAccessException or DecoderFallbackException or
                                           RegexMatchTimeoutException)
        {
            return cached is null
                ? new([], RenoDxWikiFetchState.UnableToCheck, false, false, null, exception.Message)
                : new(cached.Records, RenoDxWikiFetchState.Offline, true, false, cached.ETag,
                    $"The current RenoDX wiki could not be validated; the last known-good catalog is in use. {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return cached is null
                ? new([], RenoDxWikiFetchState.UnableToCheck, false, false, null,
                    "The RenoDX wiki request timed out.")
                : new(cached.Records, RenoDxWikiFetchState.Offline, true, false, cached.ETag,
                    "The RenoDX wiki request timed out; the last known-good catalog is in use.");
        }
    }

    public static IReadOnlyList<RenoDxWikiRecord> ParseMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (Encoding.UTF8.GetByteCount(markdown) > MaximumMarkdownBytes)
            throw new InvalidDataException("The RenoDX wiki response exceeds the metadata size limit.");

        var records = new Dictionary<string, RenoDxWikiRecord>(StringComparer.OrdinalIgnoreCase);
        TableColumns? table = null;
        var foundModTable = false;
        var insidePrimaryList = false;
        using var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart('\uFEFF', ' ', '\t');
            if (TryReadLevelOneHeading(trimmed, out var heading))
            {
                if (heading.Equals("List", StringComparison.OrdinalIgnoreCase))
                {
                    insidePrimaryList = true;
                    table = null;
                    continue;
                }
                if (insidePrimaryList) break;
            }
            if (!insidePrimaryList) continue;
            if (!trimmed.StartsWith('|'))
            {
                if (trimmed.StartsWith('#')) table = null;
                continue;
            }

            var cells = SplitTableRow(line);
            if (TryReadHeader(cells, out var header))
            {
                table = header;
                foundModTable = true;
                continue;
            }
            if (LooksLikeAnotherTableHeader(cells))
            {
                table = null;
                continue;
            }
            if (table is not { } columns || IsSeparatorRow(cells) || cells.Count <= columns.MaximumIndex) continue;

            var name = CleanName(cells[columns.Name]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Length > 512)
                throw new InvalidDataException("A RenoDX wiki game name exceeds the metadata size limit.");
            var (addon32, addon64) = ReadAddonUrls(cells[columns.Links], name);
            if (addon32 is null && addon64 is null) continue;
            var status = ReadStatus(cells[columns.Status]);
            var record = new RenoDxWikiRecord(
                name,
                addon32,
                addon64,
                status,
                RenoDxWikiRecordOrigin.ValidatedOfficialWiki);
            var key = NormalizeName(name);
            if (records.TryGetValue(key, out var existing))
                records[key] = Merge(existing, record);
            else
                records.Add(key, record);
            if (records.Count > MaximumRecords)
                throw new InvalidDataException("The RenoDX wiki contains too many game records.");
        }

        if (!foundModTable || records.Count == 0)
            throw new InvalidDataException("The RenoDX wiki does not contain a usable Name/Links/Status mod table.");
        return records.Values.OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase).ToArray();
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
        if (!File.Exists(MarkdownCachePath)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(MarkdownCachePath, cancellationToken);
            if (bytes.Length > MaximumMarkdownBytes) return null;
            var records = ParseMarkdown(StrictUtf8.GetString(bytes));
            string? etag = null;
            if (File.Exists(ETagCachePath))
            {
                var candidate = (await File.ReadAllTextAsync(ETagCachePath, cancellationToken)).Trim();
                if (EntityTagHeaderValue.TryParse(candidate, out _)) etag = candidate;
            }
            return new(records, etag);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or DecoderFallbackException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static (Uri? Addon32, Uri? Addon64) ReadAddonUrls(string linksCell, string gameName)
    {
        var addon32 = new HashSet<Uri>();
        var addon64 = new HashSet<Uri>();
        foreach (Match match in HttpUrlPattern.Matches(linksCell))
        {
            var raw = match.Value.TrimEnd(']', '}', ',', ';', '.', '"', '\'');
            if (!raw.Contains(".addon32", StringComparison.OrdinalIgnoreCase) &&
                !raw.Contains(".addon64", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException($"RenoDX wiki entry '{gameName}' contains an unsafe addon URL.");
            var extension = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath));
            if (extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)) addon32.Add(uri);
            else if (extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) addon64.Add(uri);
            else
                throw new InvalidDataException(
                    $"RenoDX wiki entry '{gameName}' contains a misleading addon URL whose path does not end in .addon32 or .addon64.");
        }

        if (addon32.Count > 1 || addon64.Count > 1)
            throw new InvalidDataException(
                $"RenoDX wiki entry '{gameName}' contains ambiguous download URLs for the same architecture.");
        return (addon32.SingleOrDefault(), addon64.SingleOrDefault());
    }

    private static RenoDxWikiRecord Merge(RenoDxWikiRecord existing, RenoDxWikiRecord candidate)
    {
        var addon32 = MergeUrl(existing.Name, PeArchitecture.X86, existing.Addon32Url, candidate.Addon32Url);
        var addon64 = MergeUrl(existing.Name, PeArchitecture.X64, existing.Addon64Url, candidate.Addon64Url);
        return existing with
        {
            Addon32Url = addon32,
            Addon64Url = addon64,
            Status = ConservativeStatus(existing.Status, candidate.Status)
        };
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
        return RenoDxWikiStatus.Unknown;
    }

    private static bool TryReadHeader(IReadOnlyList<string> cells, out TableColumns columns)
    {
        var name = FindHeader(cells, "name");
        var links = FindHeader(cells, "links", "download", "downloads");
        var status = FindHeader(cells, "status");
        if (name >= 0 && links >= 0 && status >= 0)
        {
            columns = new(name, links, status);
            return true;
        }
        columns = default;
        return false;
    }

    private static bool LooksLikeAnotherTableHeader(IReadOnlyList<string> cells) =>
        FindHeader(cells, "name") >= 0 && FindHeader(cells, "status") >= 0;

    private static bool TryReadLevelOneHeading(string line, out string heading)
    {
        heading = string.Empty;
        if (!line.StartsWith("# ", StringComparison.Ordinal)) return false;
        heading = line[2..].Trim().TrimEnd('#').Trim();
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

    private static string NormalizeName(string value) =>
        WhitespacePattern.Replace(value.Normalize(NormalizationForm.FormKC), " ").Trim();

    private static bool CatalogsEqual(
        IReadOnlyList<RenoDxWikiRecord> first,
        IReadOnlyList<RenoDxWikiRecord> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => pair.First == pair.Second);

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

    private sealed record CachedCatalog(IReadOnlyList<RenoDxWikiRecord> Records, string? ETag);

    private readonly record struct TableColumns(int Name, int Links, int Status)
    {
        public int MaximumIndex => Math.Max(Name, Math.Max(Links, Status));
    }
}
