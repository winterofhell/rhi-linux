using RhiLinux.Core;

namespace RhiLinux.Mods;

public static class CachedArtifactMaterializer
{
    public static async Task<(ComponentArtifact Main, IReadOnlyList<ComponentArtifact> Support, OptiScalerBundleManifest Manifest)>
        MaterializeOptiScalerAsync(
            ArtifactCacheService cache,
            ArtifactSelection selection,
            CancellationToken cancellationToken = default)
    {
        if (selection.Component != ComponentKind.OptiScaler || selection.CacheState != ArtifactCacheState.Cached ||
            selection.BundleManifestPath is null || selection.ArchiveSha256 is null)
            throw new InvalidOperationException("A validated cached OptiScaler bundle is required.");

        var manifest = await OptiScalerBundleParser.ReadAsync(selection.BundleManifestPath, cancellationToken);
        if (!manifest.ArchiveSha256.Equals(selection.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OptiScaler release metadata and bundle manifest disagree.");
        var runtime = manifest.RuntimeFiles;
        var mainFile = runtime.Single(x => x.RelativePath.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase));
        ArtifactValidator.ValidatePe(cache.BlobPath(mainFile.Sha256), selection.Architecture);
        var main = ToArtifact(cache, selection, manifest, mainFile);
        var support = runtime.Where(x => !ReferenceEquals(x, mainFile) &&
                !x.RelativePath.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase))
            .Select(x => ToArtifact(cache, selection, manifest, x)).ToArray();
        return (main, support, manifest);
    }

    public static ComponentArtifact MaterializeSingle(ArtifactSelection selection)
    {
        var path = selection.CachedPath ?? throw new InvalidOperationException($"{selection.Component} is not cached.");
        ArtifactValidator.ValidatePe(path, selection.Architecture);
        return new(selection.Component, path, selection.DeployFileName, selection.Version,
            selection.SourceUrl.ToString(), selection.Sha256, selection.DeployFileName, selection.Sha256,
            selection.ArchiveSha256);
    }

    private static ComponentArtifact ToArtifact(
        ArtifactCacheService cache,
        ArtifactSelection selection,
        OptiScalerBundleManifest manifest,
        OptiScalerBundleFile file) =>
        new(ComponentKind.OptiScaler, cache.BlobPath(file.Sha256), Path.GetFileName(file.RelativePath),
            selection.Version, selection.SourceUrl.ToString(), file.Sha256, file.RelativePath, file.Sha256,
            manifest.ArchiveSha256, file.Requirement, file.RequirementReason, file.Feature,
            file.CanOmitOnCollision);
}
