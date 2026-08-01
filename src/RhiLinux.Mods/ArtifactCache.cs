using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum ArtifactArchiveKind { None, ReShadeInstaller, SevenZip }
public enum ArtifactCacheState { DownloadRequired, Cached, Invalid }
public enum ArtifactSupportKind { General, ExactGameProfile, ExecutableOrAliasProfile, UnityFallback, UnrealFallback, Unavailable }
public enum ArtifactValidationState { NotValidated, Valid, Invalid }

public sealed record ArtifactSelection(
    ComponentKind Component,
    string Version,
    Uri SourceUrl,
    string ReleaseTag,
    string AssetName,
    PeArchitecture Architecture,
    uint? GameAppId,
    string DeployFileName,
    string? UpstreamDigest,
    ArtifactArchiveKind ArchiveKind,
    ArtifactCacheState CacheState = ArtifactCacheState.DownloadRequired,
    string? CachedPath = null,
    IReadOnlyList<string>? AdditionalCachedFiles = null,
    string? GameProfile = null,
    ArtifactSupportKind Support = ArtifactSupportKind.General,
    ArtifactValidationState Validation = ArtifactValidationState.NotValidated,
    string? Sha256 = null,
    string? UnavailableReason = null,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    string? BundleManifestPath = null,
    string? ArchiveSha256 = null,
    bool SourceValidatedByOfficialMetadata = false);

public sealed record CachedArtifactFile(string RelativePath, string BlobSha256, long Size, bool RuntimeRequired);

public sealed record ArtifactCacheMetadata(
    ComponentKind Component,
    string Version,
    string SourceUrl,
    string ReleaseTag,
    string AssetName,
    PeArchitecture Architecture,
    uint? GameAppId,
    DateTimeOffset DownloadedUtc,
    string Sha256,
    bool ValidationSucceeded,
    bool ExtractionSucceeded,
    string? ETag,
    DateTimeOffset? LastModified,
    string RelativePayloadPath,
    IReadOnlyList<string> AdditionalRelativePaths,
    string? GameProfile = null,
    ArtifactSupportKind Support = ArtifactSupportKind.General,
    string? ArchiveSha256 = null,
    string? PayloadBlobSha256 = null,
    IReadOnlyList<CachedArtifactFile>? Files = null,
    string? BundleManifestRelativePath = null,
    string? DeployFileName = null);

public sealed record ArtifactCacheEntry(string MetadataPath, ArtifactCacheMetadata Metadata, bool IsValid, string? Problem);

public sealed record ArtifactCacheStatistics(long SizeBytes, int BlobCount, int ReleaseCount, DateTimeOffset? LastCleanupUtc)
{
    public double SizeMiB => SizeBytes / 1024d / 1024d;
}

public sealed record ArtifactCacheCleanupResult(long BytesRemoved, int BlobsRemoved, int ReleasesRemoved, DateTimeOffset CompletedUtc);

public sealed class ArtifactCacheService(XdgPaths paths)
{
    public const long DefaultLimitBytes = 5L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> ApprovedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "reshade.me", "www.reshade.me", "github.com", "objects.githubusercontent.com",
        "github-releases.githubusercontent.com", "clshortfuse.github.io", "notvoosh.github.io", "marat569.github.io",
        "mqhaji.github.io", "souperman9.github.io"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root { get; } = paths.AppCacheDirectory;
    public string BlobRoot => Path.Combine(Root, "blobs", "sha256");
    public string ReleaseMetadataRoot => Path.Combine(Root, "metadata", "releases");
    public string StagingRoot => Path.Combine(Root, "staging");
    public string QuarantineRoot => Path.Combine(Root, "quarantine");

    public async Task<ArtifactSelection> InspectAsync(ArtifactSelection selection, CancellationToken cancellationToken = default)
    {
        var metadataPath = MetadataPath(selection);
        if (!File.Exists(metadataPath)) return selection;
        try
        {
            var metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
            var identityProblem = ValidateSelectionIdentity(selection, metadata);
            if (identityProblem is not null)
                return selection with
                {
                    CacheState = ArtifactCacheState.DownloadRequired,
                    Validation = ArtifactValidationState.NotValidated,
                    UnavailableReason = identityProblem
                };
            var payload = ResolveMetadataPath(metadataPath, metadata.RelativePayloadPath);
            var problem = await ValidateMetadataAsync(metadataPath, metadata, cancellationToken);
            if (problem is not null)
                return selection with { CacheState = ArtifactCacheState.Invalid, Validation = ArtifactValidationState.Invalid, UnavailableReason = problem };
            if (selection.ArchiveKind == ArtifactArchiveKind.None && !string.IsNullOrWhiteSpace(selection.UpstreamDigest) &&
                !metadata.Sha256.Equals(NormalizeDigest(selection.UpstreamDigest), StringComparison.OrdinalIgnoreCase))
                return selection with { CacheState = ArtifactCacheState.DownloadRequired, Validation = ArtifactValidationState.Valid, Sha256 = metadata.Sha256 };
            var additional = metadata.AdditionalRelativePaths.Select(x => ResolveMetadataPath(metadataPath, x)).ToArray();
            var bundleManifest = metadata.BundleManifestRelativePath is null
                ? null : ResolveMetadataPath(metadataPath, metadata.BundleManifestRelativePath);
            return selection with
            {
                CacheState = ArtifactCacheState.Cached,
                CachedPath = payload,
                AdditionalCachedFiles = additional,
                GameProfile = metadata.GameProfile ?? selection.GameProfile,
                Support = metadata.Support,
                Validation = ArtifactValidationState.Valid,
                Sha256 = metadata.Sha256,
                UnavailableReason = null,
                ETag = metadata.ETag,
                LastModified = metadata.LastModified,
                BundleManifestPath = bundleManifest,
                ArchiveSha256 = metadata.ArchiveSha256
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return selection with { CacheState = ArtifactCacheState.Invalid, Validation = ArtifactValidationState.Invalid, UnavailableReason = exception.Message };
        }
    }

    public async Task<ArtifactSelection> AcquireAsync(
        ArtifactSelection unresolved,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        if (unresolved.Component == ComponentKind.OptiPatcher)
            throw new NotSupportedException("OptiPatcher is legacy-cleanup-only and is not downloaded.");
        if (unresolved.SourceUrl.Scheme != Uri.UriSchemeHttps ||
            !ApprovedHosts.Contains(unresolved.SourceUrl.Host) &&
            !(unresolved.SourceValidatedByOfficialMetadata &&
              OfficialArtifactSourcePolicy.IsConstrainedRenoDxAddon(unresolved.SourceUrl)))
            throw new InvalidOperationException("Artifact URL is not an approved official upstream source.");
        var refreshRequested = unresolved.CacheState == ArtifactCacheState.DownloadRequired &&
            unresolved.CachedPath is not null && unresolved.Validation == ArtifactValidationState.Valid;
        var inspected = await InspectAsync(unresolved, cancellationToken);
        if (!refreshRequested && inspected.CacheState == ArtifactCacheState.Cached) return inspected;

        var staging = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        var download = Path.Combine(staging, "archive.part");
        Directory.CreateDirectory(staging);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, unresolved.SourceUrl);
            request.Headers.UserAgent.ParseAdd("rhi-linux/0.1");
            if (unresolved.SourceUrl.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
                request.Headers.Referrer = new Uri("https://reshade.me/");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > ArtifactValidator.MaxDownloadSizeBytes)
                throw new InvalidDataException("Downloaded artifact exceeds the 2 GiB safety limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                await ArtifactValidator.CopyWithLimitAsync(
                    input, output, ArtifactValidator.MaxDownloadSizeBytes, cancellationToken);

            var archiveHash = await HashAsync(download, cancellationToken);
            if (!string.IsNullOrWhiteSpace(unresolved.UpstreamDigest) &&
                !archiveHash.Equals(NormalizeDigest(unresolved.UpstreamDigest), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded artifact checksum does not match the upstream digest.");

            var extracted = Path.Combine(staging, "extracted");
            Directory.CreateDirectory(extracted);
            string payloadHash;
            IReadOnlyList<CachedArtifactFile> files;
            string? bundleManifestPath = null;
            switch (unresolved.ArchiveKind)
            {
                case ArtifactArchiveKind.None:
                    ArtifactValidator.ValidatePe(download, unresolved.Architecture);
                    _ = await PutBlobAsync(download, archiveHash, cancellationToken);
                    payloadHash = archiveHash;
                    files = [new(unresolved.DeployFileName, archiveHash, new FileInfo(download).Length, true)];
                    break;
                case ArtifactArchiveKind.ReShadeInstaller:
                    var reshade = Path.Combine(extracted, unresolved.DeployFileName);
                    await ExtractReShadeAsync(download, unresolved.Architecture, reshade, cancellationToken);
                    ArtifactValidator.ValidatePe(reshade, unresolved.Architecture);
                    if (!ContainsAsciiMarker(reshade, "Searching for add-ons"))
                        throw new InvalidDataException("The extracted ReShade DLL does not expose full add-on loading.");
                    payloadHash = await HashAsync(reshade, cancellationToken);
                    _ = await PutBlobAsync(download, archiveHash, cancellationToken);
                    _ = await PutBlobAsync(reshade, payloadHash, cancellationToken);
                    files = [new(unresolved.DeployFileName, payloadHash, new FileInfo(reshade).Length, true)];
                    break;
                case ArtifactArchiveKind.SevenZip when unresolved.Component == ComponentKind.OptiScaler:
                    await ExtractSevenZipAsync(download, extracted, cancellationToken);
                    var parsed = await OptiScalerBundleParser.ParseAsync(extracted, unresolved.Version, unresolved.ReleaseTag,
                        unresolved.AssetName, archiveHash, cancellationToken);
                    foreach (var file in parsed.Files)
                    {
                        var source = Path.Combine(extracted, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        if (file.RuntimeRequired && Path.GetExtension(file.RelativePath)
                            .Equals(".dll", StringComparison.OrdinalIgnoreCase))
                            ArtifactValidator.ValidatePe(source);
                    }
                    var runtime = parsed.Files.Single(x => x.RelativePath.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase));
                    var runtimePath = Path.Combine(extracted,
                        runtime.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    ArtifactValidator.ValidatePe(runtimePath, unresolved.Architecture);
                    if (!ContainsAsciiMarker(runtimePath, "OptiScaler"))
                        throw new InvalidDataException("The selected release payload does not identify itself as OptiScaler.");
                    _ = await PutBlobAsync(download, archiveHash, cancellationToken);
                    foreach (var file in parsed.Files)
                    {
                        var source = Path.Combine(extracted, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        _ = await PutBlobAsync(source, file.Sha256, cancellationToken);
                    }
                    payloadHash = runtime.Sha256;
                    files = parsed.Files.Select(x => new CachedArtifactFile(x.RelativePath, x.Sha256, x.Size, x.RuntimeRequired)).ToArray();
                    bundleManifestPath = Path.Combine(Root, "bundles", "optiscaler", SafeSegment(unresolved.Version), "manifest.json");
                    await OptiScalerBundleParser.WriteAsync(bundleManifestPath, parsed, cancellationToken);
                    break;
                default:
                    throw new InvalidDataException("The archive type is not supported for this component.");
            }

            var metadataPath = MetadataPath(unresolved);
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            var runtimeFiles = files.Where(x => x.RuntimeRequired && !x.BlobSha256.Equals(payloadHash, StringComparison.OrdinalIgnoreCase)).ToArray();
            var metadata = new ArtifactCacheMetadata(
                unresolved.Component, unresolved.Version, unresolved.SourceUrl.ToString(), unresolved.ReleaseTag,
                unresolved.AssetName, unresolved.Architecture, unresolved.GameAppId, DateTimeOffset.UtcNow, payloadHash, true,
                unresolved.ArchiveKind != ArtifactArchiveKind.None, response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified, RelativeToMetadata(metadataPath, BlobPath(payloadHash)),
                runtimeFiles.Select(x => RelativeToMetadata(metadataPath, BlobPath(x.BlobSha256))).ToArray(),
                unresolved.GameProfile, unresolved.Support, archiveHash, payloadHash, files,
                bundleManifestPath is null ? null : RelativeToMetadata(metadataPath, bundleManifestPath), unresolved.DeployFileName);
            await PreservePreviousMetadataAsync(metadataPath, metadata, cancellationToken);
            await WriteMetadataAsync(metadataPath, metadata, cancellationToken);
            return unresolved with
            {
                CacheState = ArtifactCacheState.Cached,
                CachedPath = BlobPath(payloadHash),
                AdditionalCachedFiles = runtimeFiles.Select(x => BlobPath(x.BlobSha256)).ToArray(),
                Validation = ArtifactValidationState.Valid,
                Sha256 = payloadHash,
                ETag = metadata.ETag,
                LastModified = metadata.LastModified,
                BundleManifestPath = bundleManifestPath,
                ArchiveSha256 = archiveHash
            };
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task<IReadOnlyList<ArtifactCacheEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ReleaseMetadataRoot)) return [];
        var result = new List<ArtifactCacheEntry>();
        foreach (var path in Directory.EnumerateFiles(ReleaseMetadataRoot, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            try
            {
                var metadata = await ReadMetadataAsync(path, cancellationToken);
                var problem = await ValidateMetadataAsync(path, metadata, cancellationToken);
                result.Add(new(path, metadata, problem is null, problem));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                result.Add(new(path, EmptyMetadata(), false, exception.Message));
            }
        }
        return result;
    }

    public async Task<ArtifactCacheStatistics> GetStatisticsAsync(CancellationToken token = default)
    {
        var blobs = Directory.Exists(BlobRoot) ? Directory.EnumerateFiles(BlobRoot, "*", SearchOption.AllDirectories).ToArray() : [];
        var size = Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(x => !Path.GetFullPath(x).StartsWith(Path.GetFullPath(StagingRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Sum(x => new FileInfo(x).Length) : 0;
        var releases = Directory.Exists(ReleaseMetadataRoot) ? Directory.EnumerateFiles(ReleaseMetadataRoot, "*.json").Count() : 0;
        return new(size, blobs.Length, releases, await ReadLastCleanupAsync(token));
    }

    public async Task<IReadOnlyList<ArtifactCacheEntry>> VerifyAsync(CancellationToken token = default)
    {
        var entries = await ListAsync(token);
        foreach (var entry in entries.Where(x => !x.IsValid))
        {
            foreach (var hash in ReferencedHashes(entry.Metadata))
            {
                var blob = BlobPath(hash);
                if (!File.Exists(blob)) continue;
                var actual = await HashAsync(blob, token);
                if (actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(QuarantineRoot);
                File.Move(blob, Path.Combine(QuarantineRoot, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{hash}"), true);
            }
        }
        return await ListAsync(token);
    }

    public async Task<ArtifactCacheCleanupResult> CleanupAsync(
        IReadOnlySet<string>? installedBlobReferences = null,
        long maxBytes = DefaultLimitBytes,
        CancellationToken token = default)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        installedBlobReferences ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = await ListAsync(token);
        var releasesRemoved = 0;
        foreach (var invalid in entries.Where(x => !x.IsValid))
        {
            if (File.Exists(invalid.MetadataPath)) File.Delete(invalid.MetadataPath);
            releasesRemoved++;
        }

        var blobs = Directory.Exists(BlobRoot) ? Directory.EnumerateFiles(BlobRoot, "*", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x)).OrderBy(x => x.LastWriteTimeUtc).ToList() : [];
        var size = blobs.Sum(x => x.Length);
        long removedBytes = 0;
        var removedBlobs = 0;

        async Task RemoveUnreferencedBlobsAsync()
        {
            var retained = (await ListAsync(token)).Where(x => x.IsValid)
                .SelectMany(x => ReferencedHashes(x.Metadata)).Concat(installedBlobReferences)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var blob in blobs.Where(x => x.Exists).ToArray())
            {
                if (retained.Contains(blob.Name)) continue;
                size -= blob.Length;
                removedBytes += blob.Length;
                removedBlobs++;
                blob.Delete();
            }
        }

        await RemoveUnreferencedBlobsAsync();
        if (size > maxBytes)
        {
            var valid = (await ListAsync(token)).Where(x => x.IsValid).ToArray();
            var ranked = valid.GroupBy(x => new
            {
                x.Metadata.Component,
                x.Metadata.Architecture,
                Artifact = x.Metadata.DeployFileName ?? x.Metadata.AssetName
            })
                .SelectMany(group => group.OrderByDescending(x => x.Metadata.DownloadedUtc)
                    .Select((entry, rank) => (Entry: entry, Rank: rank)))
                .Where(x => x.Rank > 1 &&
                    !ReferencedHashes(x.Entry.Metadata).Any(installedBlobReferences.Contains))
                .OrderByDescending(x => x.Rank > 1)
                .ThenBy(x => x.Entry.Metadata.DownloadedUtc)
                .ToArray();
            foreach (var candidate in ranked)
            {
                if (size <= maxBytes) break;
                if (!File.Exists(candidate.Entry.MetadataPath)) continue;
                File.Delete(candidate.Entry.MetadataPath);
                releasesRemoved++;
                await RemoveUnreferencedBlobsAsync();
            }
        }
        if (Directory.Exists(StagingRoot))
            foreach (var entry in Directory.EnumerateFileSystemEntries(StagingRoot))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
            }
        if (Directory.Exists(QuarantineRoot))
            foreach (var file in Directory.EnumerateFiles(QuarantineRoot).Where(x => File.GetLastWriteTimeUtc(x) < DateTime.UtcNow.AddDays(-7)))
                File.Delete(file);
        var completed = DateTimeOffset.UtcNow;
        foreach (var legacy in new[] { Path.Combine(Root, "artifacts"), Path.Combine(Root, "downloads") })
        {
            if (!Directory.Exists(legacy)) continue;
            var legacyFiles = Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).ToArray();
            removedBytes += legacyFiles.Sum(x => new FileInfo(x).Length);
            removedBlobs += legacyFiles.Length;
            Directory.Delete(legacy, true);
        }
        await WriteLastCleanupAsync(completed, token);
        return new(removedBytes, removedBlobs, releasesRemoved, completed);
    }

    public async Task<int> CleanInvalidAsync(CancellationToken token = default)
    {
        var before = (await ListAsync(token)).Count(x => !x.IsValid);
        _ = await VerifyAsync(token);
        // "Clean invalid" must not perform size-based release pruning because the CLI does not
        // have a complete inventory of installed game manifests.
        _ = await CleanupAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase), long.MaxValue, token);
        return before;
    }

    public string BlobPath(string sha256)
    {
        var normalized = NormalizeDigest(sha256);
        if (normalized.Length != 64 || normalized.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidDataException("Blob SHA-256 is invalid.");
        return Path.Combine(BlobRoot, normalized[..2], normalized);
    }

    private async Task<string> PutBlobAsync(string source, string sha256, CancellationToken token)
    {
        var target = BlobPath(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (!(await HashAsync(target, token)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An existing content-addressed blob is corrupt.");
            return target;
        }
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                await input.CopyToAsync(output, token);
            if (!(await HashAsync(temporary, token)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged blob hash changed while caching.");
            try { File.Move(temporary, target); }
            catch (IOException) when (File.Exists(target)) { }
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string MetadataPath(ArtifactSelection selection)
    {
        var architecture = selection.Architecture.ToString().ToLowerInvariant();
        var scope = selection.GameAppId is { } appId ? $"-app-{appId}" : string.Empty;
        var key = selection.Component switch
        {
            ComponentKind.ReShade => $"reshade-{selection.Version}-{architecture}{scope}-{selection.DeployFileName}",
            ComponentKind.RenoDx => $"renodx-{selection.Version}-{architecture}{scope}-{selection.DeployFileName}",
            ComponentKind.OptiScaler => $"optiscaler-{selection.Version}-{architecture}{scope}-{selection.AssetName}",
            _ => throw new NotSupportedException("This component is not cached by the normal product.")
        };
        return Path.Combine(ReleaseMetadataRoot, SafeSegment(key) + ".json");
    }

    private static string? ValidateSelectionIdentity(
        ArtifactSelection selection,
        ArtifactCacheMetadata metadata)
    {
        if (metadata.Component != selection.Component || metadata.Architecture != selection.Architecture ||
            metadata.GameAppId != selection.GameAppId ||
            !metadata.Version.Equals(selection.Version, StringComparison.Ordinal) ||
            !metadata.ReleaseTag.Equals(selection.ReleaseTag, StringComparison.Ordinal) ||
            !metadata.AssetName.Equals(selection.AssetName, StringComparison.Ordinal) ||
            !metadata.SourceUrl.Equals(selection.SourceUrl.ToString(), StringComparison.Ordinal) ||
            !(metadata.DeployFileName ?? Path.GetFileName(metadata.RelativePayloadPath))
                .Equals(selection.DeployFileName, StringComparison.Ordinal))
            return "Cached release metadata belongs to a different artifact identity.";
        return null;
    }

    private async Task<string?> ValidateMetadataAsync(string metadataPath, ArtifactCacheMetadata metadata, CancellationToken token)
    {
        if (!metadata.ValidationSucceeded) return "Release validation was not completed.";
        foreach (var hash in ReferencedHashes(metadata))
        {
            var blob = BlobPath(hash);
            if (!File.Exists(blob)) return $"Cached blob {hash} is missing.";
            if (!(await HashAsync(blob, token)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                return $"Cached blob {hash} failed SHA-256 validation.";
        }
        try
        {
            ArtifactValidator.ValidatePe(
                BlobPath(metadata.PayloadBlobSha256 ?? metadata.Sha256), metadata.Architecture);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"Cached payload architecture validation failed: {exception.Message}";
        }
        if (metadata.BundleManifestRelativePath is not null)
        {
            var path = ResolveMetadataPath(metadataPath, metadata.BundleManifestRelativePath);
            if (!File.Exists(path)) return "The parsed bundle manifest is missing.";
            _ = await OptiScalerBundleParser.ReadAsync(path, token);
        }
        return null;
    }

    private async Task PreservePreviousMetadataAsync(
        string metadataPath,
        ArtifactCacheMetadata replacement,
        CancellationToken token)
    {
        if (!File.Exists(metadataPath)) return;
        ArtifactCacheMetadata previous;
        try
        {
            previous = await ReadMetadataAsync(metadataPath, token);
            if (await ValidateMetadataAsync(metadataPath, previous, token) is not null) return;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return;
        }

        var previousPayloadHash = previous.PayloadBlobSha256 ?? previous.Sha256;
        var replacementPayloadHash = replacement.PayloadBlobSha256 ?? replacement.Sha256;
        if (previousPayloadHash.Equals(replacementPayloadHash, StringComparison.OrdinalIgnoreCase)) return;
        var stem = Path.GetFileNameWithoutExtension(metadataPath);
        var timestamp = previous.DownloadedUtc.UtcDateTime.Ticks.ToString("x16");
        var historyPath = Path.Combine(Path.GetDirectoryName(metadataPath)!,
            $"{stem}-{timestamp}-{previousPayloadHash[..12]}.json");
        await WriteMetadataAsync(historyPath, previous, token);
    }

    private static IEnumerable<string> ReferencedHashes(ArtifactCacheMetadata metadata)
    {
        if (metadata.ArchiveSha256 is not null) yield return metadata.ArchiveSha256;
        if (metadata.PayloadBlobSha256 is not null) yield return metadata.PayloadBlobSha256;
        else if (!string.IsNullOrWhiteSpace(metadata.Sha256)) yield return metadata.Sha256;
        foreach (var file in metadata.Files ?? []) yield return file.BlobSha256;
    }

    private static ArtifactCacheMetadata EmptyMetadata() => new(ComponentKind.ReShade, "unknown", string.Empty,
        string.Empty, string.Empty, PeArchitecture.Unknown, null, default, string.Empty, false, false,
        null, null, string.Empty, []);

    private static string RelativeToMetadata(string metadataPath, string target) =>
        Path.GetRelativePath(Path.GetDirectoryName(metadataPath)!, target);
    private static string ResolveMetadataPath(string metadataPath, string relative) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(metadataPath)!, relative));
    private static string SafeSegment(string value) => Regex.Replace(value, "[^A-Za-z0-9._-]", "_");
    private static string NormalizeDigest(string value) => value.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();

    private static async Task ExtractReShadeAsync(string installer, PeArchitecture architecture, string output, CancellationToken token)
    {
        var entryName = architecture == PeArchitecture.X86 ? "ReShade32.dll" : "ReShade64.dll";
        try
        {
            using var archive = ZipFile.OpenRead(installer);
            var entry = archive.Entries.SingleOrDefault(x => Path.GetFileName(x.FullName).Equals(entryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"The full-addon ReShade installer does not contain {entryName}.");
            await using var input = entry.Open();
            await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await ArtifactValidator.CopyWithLimitAsync(
                input, target, ArtifactValidator.MaxDownloadSizeBytes, token);
        }
        catch (InvalidDataException)
        {
            await ExtractWithBsdtarAsync(installer, Path.GetDirectoryName(output)!, [entryName], token);
            var extracted = Path.Combine(Path.GetDirectoryName(output)!, entryName);
            if (!File.Exists(extracted)) throw;
            if (!extracted.Equals(output, StringComparison.Ordinal)) File.Move(extracted, output);
        }
    }

    private static Task ExtractSevenZipAsync(string archive, string destination, CancellationToken token) =>
        ExtractWithBsdtarAsync(archive, destination, null, token);

    private static async Task ExtractWithBsdtarAsync(string archive, string destination, IReadOnlyList<string>? selected, CancellationToken token)
    {
        var executable = File.Exists("/usr/bin/bsdtar") ? "/usr/bin/bsdtar" :
            throw new InvalidOperationException("bsdtar is required to safely inspect official release archives.");
        var entries = await RunAsync(executable, ["-tf", archive], token);
        foreach (var entry in entries.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = entry.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Contains("..", StringComparer.Ordinal))
                throw new InvalidDataException($"Archive entry escapes the extraction root: {entry}");
        }
        var arguments = new List<string> { "-xf", archive, "-C", destination };
        if (selected is not null) arguments.AddRange(selected);
        _ = await RunAsync(executable, arguments, token);
        ValidateExtractedTree(destination);
    }

    private static void ValidateExtractedTree(string destination)
    {
        long totalSize = 0;
        var count = 0;
        var directories = new Stack<string>();
        directories.Push(destination);
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 4096) throw new InvalidDataException("Archive contains too many extracted entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Archive contains a symbolic link or reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }
                totalSize += new FileInfo(entry).Length;
                if (totalSize > ArtifactValidator.MaxDownloadSizeBytes)
                    throw new InvalidDataException("Archive expands beyond the 2 GiB safety limit.");
            }
        }
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidDataException($"Archive extraction failed: {error.Trim()}");
        return output;
    }

    private static async Task<ArtifactCacheMetadata> ReadMetadataAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ArtifactCacheMetadata>(input, JsonOptions, token)
            ?? throw new InvalidDataException("Release metadata is empty.");
    }

    private static async Task WriteMetadataAsync(string path, ArtifactCacheMetadata metadata, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions), token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string CacheStatePath => Path.Combine(Root, "metadata", "cache-state.json");
    private async Task<DateTimeOffset?> ReadLastCleanupAsync(CancellationToken token)
    {
        if (!File.Exists(CacheStatePath)) return null;
        try { return JsonSerializer.Deserialize<CacheState>(await File.ReadAllTextAsync(CacheStatePath, token), JsonOptions)?.LastCleanupUtc; }
        catch (JsonException) { return null; }
    }
    private async Task WriteLastCleanupAsync(DateTimeOffset value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CacheStatePath)!);
        var temporary = CacheStatePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new CacheState(value), JsonOptions), token);
            File.Move(temporary, CacheStatePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
    }

    private static bool ContainsAsciiMarker(string path, string marker)
    {
        using var input = File.OpenRead(path);
        var bytes = new byte[Math.Min(input.Length, 32 * 1024 * 1024)];
        _ = input.Read(bytes);
        return System.Text.Encoding.ASCII.GetString(bytes).Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CacheState(DateTimeOffset LastCleanupUtc);
}
