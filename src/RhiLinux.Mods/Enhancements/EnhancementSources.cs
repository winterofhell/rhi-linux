using System.Net;
using System.Net.Http.Headers;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record ResolvedEnhancementArtifact(
    string Name,
    string Version,
    Uri Source,
    long? Size,
    string? Sha256,
    string CacheFileName);

public sealed class EnhancementSourceClient(HttpClient httpClient, XdgPaths paths)
{
    private static readonly TimeSpan CommitRefreshInterval = TimeSpan.FromHours(6);
    private readonly GitHubReleaseClient releases = new(httpClient, paths);
    private readonly ArtifactDownloader downloader = new(httpClient, paths);

    private string DownloadDirectory => Path.Combine(paths.AppCacheDirectory, "downloads");

    public async Task<ResolvedEnhancementArtifact> ResolveComponentAsync(
        EnhancementDefinition definition,
        PeArchitecture architecture,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var assetName = architecture == PeArchitecture.X86 ? definition.AssetName32 : definition.AssetName64;
        if (assetName is null)
            throw new InvalidOperationException($"{definition.Name} does not publish a build for 32-bit games.");
        var release = await releases.GetReleaseAsync(
            definition.Repository, definition.Tag, "enhancement_" + definition.Kind, cancellationToken, forceRefresh);
        var asset = release.Assets.FirstOrDefault(item => item.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The {definition.Name} release does not contain {assetName}.");
        if (!ArtifactDownloader.IsAllowedHost(asset.Url))
            throw new InvalidDataException($"The {definition.Name} asset is not hosted on an approved upstream host.");
        var version = definition.Tag is "latest" or "rolling" && asset.UpdatedAt is { } updated
            ? $"{release.Version}-{updated.UtcDateTime:yyyyMMddHHmm}"
            : release.Version;
        var digest = NormalizeDigest(asset.Digest);
        var token = digest is null ? SafeToken(version) : digest[..16];
        return new(asset.Name, version, asset.Url, asset.Size, digest,
            $"enh-{definition.Kind}-{token}-{asset.Name}");
    }

    public async Task<ResolvedEnhancementArtifact> ResolveShaderPackAsync(
        ShaderPackDefinition pack,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (pack.SourceKind == ShaderPackSourceKind.GitHubRelease)
        {
            var release = await releases.GetReleaseAsync(pack.Repository, null, "shaders_" + pack.Id, cancellationToken, forceRefresh);
            var asset = release.Assets
                .Where(item => item.Name.EndsWith(pack.Reference, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Size)
                .FirstOrDefault()
                ?? throw new InvalidDataException($"The {pack.Name} release does not contain a {pack.Reference} archive.");
            if (!ArtifactDownloader.IsAllowedHost(asset.Url))
                throw new InvalidDataException($"The {pack.Name} asset is not hosted on an approved upstream host.");
            return new(asset.Name, release.Version, asset.Url, asset.Size, NormalizeDigest(asset.Digest),
                $"shaders-{pack.Id}-{SafeToken(release.Version)}{pack.Reference}");
        }

        var commit = await ResolveBranchCommitAsync(pack.Repository, pack.Reference, cancellationToken, forceRefresh);
        return new($"{pack.Id}.zip", commit[..12], new Uri($"https://codeload.github.com/{pack.Repository}/zip/{commit}"),
            null, null, $"shaders-{pack.Id}-{commit[..12]}.zip");
    }

    public string CachedPath(ResolvedEnhancementArtifact artifact) =>
        Path.Combine(DownloadDirectory, artifact.CacheFileName);

    public bool IsCached(ResolvedEnhancementArtifact artifact) =>
        File.Exists(CachedPath(artifact)) && (artifact.Size is not > 0 || new FileInfo(CachedPath(artifact)).Length == artifact.Size);

    public async Task<string> DownloadAsync(ResolvedEnhancementArtifact artifact, CancellationToken cancellationToken = default)
    {
        var cached = CachedPath(artifact);
        if (IsCached(artifact))
        {
            if (artifact.Sha256 is null || (await ArtifactDownloader.Sha256Async(cached, cancellationToken))
                    .Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                return cached;
            File.Delete(cached);
        }
        return await downloader.DownloadAsync(artifact.Source, artifact.CacheFileName, artifact.Sha256, cancellationToken);
    }

    public async Task<string> ResolveBranchCommitAsync(
        string repository,
        string branch,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var cacheDirectory = Path.Combine(paths.AppCacheDirectory, "releases");
        var key = $"commit_{repository.Replace('/', '_')}_{branch}";
        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The repository or branch name is not valid.");
        var cachePath = Path.Combine(cacheDirectory, key + ".txt");
        var etagPath = cachePath + ".etag";
        var checkedPath = cachePath + ".checked";
        var cached = File.Exists(cachePath) ? (await File.ReadAllTextAsync(cachePath, cancellationToken)).Trim() : null;
        if (!forceRefresh && IsCommitSha(cached) && File.Exists(checkedPath) &&
            DateTimeOffset.TryParse(await File.ReadAllTextAsync(checkedPath, cancellationToken), out var checkedAt) &&
            DateTimeOffset.UtcNow - checkedAt < CommitRefreshInterval)
            return cached!;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/commits/{branch}");
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.4");
            request.Headers.Accept.ParseAdd("application/vnd.github.sha");
            if (IsCommitSha(cached) && File.Exists(etagPath) &&
                EntityTagHeaderValue.TryParse(await File.ReadAllTextAsync(etagPath, cancellationToken), out var etag))
                request.Headers.IfNoneMatch.Add(etag);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            string sha;
            if (response.StatusCode == HttpStatusCode.NotModified && IsCommitSha(cached)) sha = cached!;
            else
            {
                response.EnsureSuccessStatusCode();
                sha = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (!IsCommitSha(sha)) throw new InvalidDataException($"GitHub returned an invalid commit for {repository}.");
                Directory.CreateDirectory(cacheDirectory);
                await WriteAtomicAsync(cachePath, sha, cancellationToken);
                if (response.Headers.ETag is not null)
                    await WriteAtomicAsync(etagPath, response.Headers.ETag.ToString(), cancellationToken);
            }
            Directory.CreateDirectory(cacheDirectory);
            await WriteAtomicAsync(checkedPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            return sha;
        }
        catch (Exception exception) when (IsCommitSha(cached) &&
            exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return cached!;
        }
    }

    public static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    private static string SafeToken(string value)
    {
        var chars = value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_')
            .Take(48)
            .ToArray();
        return chars.Length == 0 ? "unversioned" : new string(chars);
    }

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
}
