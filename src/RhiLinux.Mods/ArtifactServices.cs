using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Digest);
public sealed record UpstreamRelease(string Version, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets, DateTimeOffset PublishedAt);
public sealed record RemoteManifestCheck(MetadataCheckState State, int? Version, bool Changed, bool IsCached, string? Reason);
public sealed record RemoteManifestCatalog(
    int Version,
    IReadOnlyDictionary<string, Uri> AddonOverrides,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyDictionary<string, uint> SteamAppIds,
    IReadOnlyDictionary<string, string> LaunchExecutables);

public static class OfficialArtifactSourcePolicy
{
    public static bool IsConstrainedRenoDxAddon(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port != 443 && !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment)) return false;
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (!extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 6 && segments[1].StartsWith("renodx", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase) &&
                segments[3].Equals("download", StringComparison.OrdinalIgnoreCase);
        }
        if (!uri.Host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase)) return false;
        var firstSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSegment?.StartsWith("renodx", StringComparison.OrdinalIgnoreCase) == true;
    }
}

public sealed class RemoteManifestClient(HttpClient httpClient, XdgPaths paths)
{
    private const string Source = "https://raw.githubusercontent.com/RankFTW/RHI/main/manifest.json";
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private string CacheDirectory => Path.Combine(paths.AppCacheDirectory, "releases");
    private string CachePath => Path.Combine(CacheDirectory, "rhi-manifest.json");
    private string ETagPath => CachePath + ".etag";

    public async Task<RemoteManifestCheck> CheckAsync(bool allowNetwork, CancellationToken cancellationToken = default)
    {
        var cachedCatalog = await ReadCatalogAsync(cancellationToken);
        var cachedVersion = cachedCatalog?.Version;
        if (!allowNetwork)
            return cachedVersion is null
                ? new(MetadataCheckState.Offline, null, false, false, "No cached game manifest is available.")
                : new(MetadataCheckState.Offline, cachedVersion, false, true, null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Source);
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
            if (File.Exists(ETagPath) && EntityTagHeaderValue.TryParse(await File.ReadAllTextAsync(ETagPath, cancellationToken), out var etag))
                request.Headers.IfNoneMatch.Add(etag);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && cachedVersion is not null)
                return new(MetadataCheckState.Online, cachedVersion, false, true, null);
            response.EnsureSuccessStatusCode();
            var json = await ReadUtf8Async(response.Content, cancellationToken);
            var catalog = ParseCatalog(json);
            var version = catalog.Version;
            Directory.CreateDirectory(CacheDirectory);
            await WriteAtomicAsync(CachePath, json, cancellationToken);
            if (response.Headers.ETag is not null) await WriteAtomicAsync(ETagPath, response.Headers.ETag.ToString(), cancellationToken);
            return new(MetadataCheckState.Online, version, cachedVersion is not null && version != cachedVersion, true, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        {
            return cachedVersion is null
                ? new(MetadataCheckState.UnableToCheck, null, false, false, exception.Message)
                : new(MetadataCheckState.Offline, cachedVersion, false, true, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return cachedVersion is null
                ? new(MetadataCheckState.UnableToCheck, null, false, false, "The manifest request timed out.")
                : new(MetadataCheckState.Offline, cachedVersion, false, true, "The manifest request timed out.");
        }
    }

    public async Task<RemoteManifestCatalog?> ReadCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CachePath)) return null;
        try
        {
            await using var input = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ParseCatalog(await ReadUtf8Async(input, input.Length, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                          KeyNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static RemoteManifestCatalog ParseCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionElement) ||
            !versionElement.TryGetInt32(out var version) || version <= 0)
            throw new InvalidDataException("The remote manifest has no valid positive version.");

        var addons = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        ReadStringMap(root, ["addonUrls", "snapshotOverrides"], (name, value) =>
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(uri))
                throw new InvalidDataException($"Remote manifest addon URL for '{name}' is not a constrained RenoDX addon URL.");
            AddUnambiguous(addons, name, uri, "addon URL");
        });

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadStringMap(root, ["aliases", "wikiNameOverrides"], (alias, canonical) =>
            AddUnambiguous(aliases, alias, canonical, "alias"));

        var appIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in new[] { "appIds", "steamAppIdOverrides" })
        {
            if (!root.TryGetProperty(propertyName, out var map)) continue;
            if (map.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Remote manifest '{propertyName}' must be an object.");
            foreach (var property in map.EnumerateObject())
            {
                if (!TryReadAppId(property.Value, out var appId) || appId == 0)
                    throw new InvalidDataException($"Remote manifest AppID for '{property.Name}' is invalid.");
                AddUnambiguous(appIds, property.Name, appId, "Steam AppID");
            }
        }
        if (appIds.GroupBy(entry => entry.Value).Any(group => group.Count() > 1))
            throw new InvalidDataException("Remote manifest maps one Steam AppID to multiple game identities.");

        var executables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadStringMap(root, ["launchExeOverrides"], (name, executable) =>
        {
            if (Path.GetFileName(executable) != executable ||
                !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Remote manifest executable for '{name}' is not a filename-only Windows executable.");
            AddUnambiguous(executables, name, executable, "executable mapping");
        });
        return new(version, addons, aliases, appIds, executables);
    }

    private static void ReadStringMap(
        JsonElement root,
        IReadOnlyList<string> propertyNames,
        Action<string, string> add)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var map)) continue;
            if (map.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Remote manifest '{propertyName}' must be an object.");
            foreach (var property in map.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(property.Name) ||
                    string.IsNullOrWhiteSpace(property.Value.GetString()))
                    throw new InvalidDataException($"Remote manifest '{propertyName}' contains an empty mapping.");
                add(property.Name.Trim(), property.Value.GetString()!.Trim());
            }
        }
    }

    private static void AddUnambiguous<T>(
        IDictionary<string, T> values,
        string key,
        T value,
        string kind)
    {
        if (values.TryGetValue(key, out var previous) && !EqualityComparer<T>.Default.Equals(previous, value))
            throw new InvalidDataException($"Remote manifest {kind} for '{key}' is ambiguous.");
        values[key] = value;
    }

    private static bool TryReadAppId(JsonElement value, out uint appId)
    {
        appId = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetUInt32(out appId);
        return value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out appId);
    }

    private static async Task<string> ReadUtf8Async(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        return await ReadUtf8Async(input, content.Headers.ContentLength, cancellationToken);
    }

    private static async Task<string> ReadUtf8Async(
        Stream input,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength > MaximumManifestBytes)
            throw new InvalidDataException("The remote manifest exceeds the 4 MiB safety limit.");
        await using var buffer = contentLength is >= 0 and <= MaximumManifestBytes
            ? new MemoryStream((int)contentLength.Value)
            : new MemoryStream();
        await ArtifactValidator.CopyWithLimitAsync(input, buffer, MaximumManifestBytes, cancellationToken);
        var bytes = buffer.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        try
        {
            return StrictUtf8.GetString(bytes.AsSpan(offset));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The remote manifest is not valid UTF-8.", exception);
        }
    }

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class GitHubReleaseClient(HttpClient httpClient, XdgPaths paths)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<ComponentKind, (string Repository, string? Tag)> Repositories = new Dictionary<ComponentKind, (string, string?)>
    {
        [ComponentKind.OptiScaler] = ("optiscaler/OptiScaler", null)
    };

    public async Task<UpstreamRelease?> GetLatestAsync(
        ComponentKind component,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!Repositories.TryGetValue(component, out var releaseSource)) return null;
        var cacheDirectory = Path.Combine(paths.AppCacheDirectory, "releases");
        var cachePath = Path.Combine(cacheDirectory, component + ".json");
        var etagPath = cachePath + ".etag";
        var checkedPath = cachePath + ".checked";
        if (!forceRefresh && File.Exists(cachePath) && File.Exists(checkedPath) &&
            DateTimeOffset.TryParse(await File.ReadAllTextAsync(checkedPath, cancellationToken), out var checkedUtc) &&
            DateTimeOffset.UtcNow - checkedUtc < RefreshInterval)
            return ParseRelease(await File.ReadAllTextAsync(cachePath, cancellationToken));
        var releasePath = releaseSource.Tag is null ? "latest" : $"tags/{releaseSource.Tag}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{releaseSource.Repository}/releases/{releasePath}");
        request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
        if (File.Exists(etagPath) && EntityTagHeaderValue.TryParse(await File.ReadAllTextAsync(etagPath, cancellationToken), out var cachedEtag))
            request.Headers.IfNoneMatch.Add(cachedEtag);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string json;
        if (response.StatusCode == HttpStatusCode.NotModified && File.Exists(cachePath)) json = await File.ReadAllTextAsync(cachePath, cancellationToken);
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken);
            Directory.CreateDirectory(cacheDirectory);
            await WriteAtomicAsync(cachePath, json, cancellationToken);
            if (response.Headers.ETag is not null) await WriteAtomicAsync(etagPath, response.Headers.ETag.ToString(), cancellationToken);
        }
        Directory.CreateDirectory(cacheDirectory);
        await WriteAtomicAsync(checkedPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        return ParseRelease(json);
    }

    private static UpstreamRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var assets = root.GetProperty("assets").EnumerateArray().Select(x => new ReleaseAsset(
            x.GetProperty("name").GetString()!, new Uri(x.GetProperty("browser_download_url").GetString()!),
            x.GetProperty("size").GetInt64(), x.TryGetProperty("digest", out var digest) ? digest.GetString() : null)).ToArray();
        return new UpstreamRelease(root.GetProperty("tag_name").GetString()!, new Uri(root.GetProperty("html_url").GetString()!), assets,
            root.GetProperty("published_at").GetDateTimeOffset());
    }

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { await File.WriteAllTextAsync(temporary, text, token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class ArtifactDownloader(HttpClient httpClient, XdgPaths paths)
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com",
        "raw.githubusercontent.com", "reshade.me", "www.reshade.me", "marat569.github.io"
    };

    public async Task<string> DownloadAsync(Uri source, string fileName, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        if (source.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(source.Host)) throw new InvalidOperationException("Artifact URL is not an approved official upstream host.");
        if (Path.GetFileName(fileName) != fileName) throw new InvalidDataException("Artifact filename must not contain a path.");
        var directory = Path.Combine(paths.AppCacheDirectory, "downloads");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fileName);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > ArtifactValidator.MaxDownloadSizeBytes)
                throw new InvalidDataException("Downloaded artifact exceeds the 2 GiB safety limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                await ArtifactValidator.CopyWithLimitAsync(input, output, ArtifactValidator.MaxDownloadSizeBytes, cancellationToken);
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = await Sha256Async(temporary, cancellationToken);
                if (!actual.Equals(expectedSha256.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded artifact checksum does not match the upstream digest.");
            }
            File.Move(temporary, target, true);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }
}

public static class ArtifactValidator
{
    public const long MaxDownloadSizeBytes = 2L * 1024 * 1024 * 1024;

    public static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("Downloaded or extracted artifact exceeds its safety limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    public static PeArchitecture ValidatePe(string path, PeArchitecture expectedArchitecture = PeArchitecture.Unknown)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Artifact does not have an MZ header.");
        stream.Position = 0x3c;
        var offset = reader.ReadUInt32();
        if (offset > stream.Length - 6) throw new InvalidDataException("Artifact has an invalid PE offset.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Artifact does not have a PE signature.");
        var architecture = reader.ReadUInt16() switch
        {
            0x014c => PeArchitecture.X86,
            0x8664 => PeArchitecture.X64,
            _ => throw new InvalidDataException("Artifact PE architecture is unsupported.")
        };
        if (expectedArchitecture is not PeArchitecture.Unknown && architecture != expectedArchitecture)
            throw new InvalidDataException(
                $"Artifact PE architecture is {architecture}, but {expectedArchitecture} is required.");
        return architecture;
    }

    public static async Task ExtractZipAsync(string archive, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long totalSize = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalSize += entry.Length;
            if (totalSize > MaxDownloadSizeBytes) throw new InvalidDataException("Archive expands beyond the 2 GiB safety limit.");
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"Archive entry escapes the destination: {entry.FullName}");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }
    }
}
