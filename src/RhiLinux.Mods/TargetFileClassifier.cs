using System.Diagnostics;
using System.Security.Cryptography;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum TargetFileClassification
{
    Absent,
    ManagedByRhiLinux,
    ChangedManagedFile,
    GameOwned,
    KnownOptiScalerBundleFile,
    KnownReShadeFile,
    KnownRenoDxRelatedFile,
    RecoverableLeftover,
    UnknownForeignFile,
    HardConflict
}

public sealed record TargetFileDiagnostic(
    string Path,
    TargetFileClassification Classification,
    string? Sha256,
    PeArchitecture Architecture,
    IReadOnlyList<string> Evidence,
    bool CanAdopt,
    bool CanReplace,
    bool MustPreserve)
{
    public bool RequiresRepair => Classification == TargetFileClassification.RecoverableLeftover;
}

public sealed class TargetFileClassifier(GameProfileCatalog? profiles = null)
{
    private readonly GameProfileCatalog profiles = profiles ?? new GameProfileCatalog();

    public async Task<TargetFileDiagnostic> ClassifyAsync(
        SteamGame game,
        GameManifest manifest,
        string target,
        IReadOnlyList<ComponentArtifact>? officialArtifacts = null,
        CancellationToken cancellationToken = default)
    {
        TargetFileDiagnostic Classified(
            TargetFileClassification classification,
            string? hash,
            PeArchitecture architecture,
            IReadOnlyList<string> evidence,
            bool canAdopt,
            bool canReplace,
            bool mustPreserve) => new(target, classification, hash, architecture, evidence, canAdopt, canReplace, mustPreserve);

        if (!File.Exists(target))
            return Classified(TargetFileClassification.Absent, null, PeArchitecture.Unknown,
                ["target is absent"], false, true, false);

        var hash = await HashAsync(target, cancellationToken);
        var architecture = ReadArchitecture(target);
        var relative = Path.GetRelativePath(game.GameRoot, target);
        var fileName = Path.GetFileName(target);
        var evidence = new List<string>
        {
            $"SHA-256 {hash}",
            $"size {new FileInfo(target).Length} bytes",
            $"PE architecture {architecture}"
        };
        var exactRecord = manifest.Files.FirstOrDefault(x =>
            x.RelativePath.Equals(relative, StringComparison.Ordinal));
        if (exactRecord is not null)
        {
            evidence.Add($"ownership manifest records {exactRecord.Component}");
            if (hash.Equals(exactRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (manifest.TransactionIds.Count > 0)
                    evidence.Add($"exact installation history contains {manifest.TransactionIds.Count} transaction(s)");
                return Classified(TargetFileClassification.ManagedByRhiLinux, hash, architecture,
                    evidence, false, true, false);
            }
            evidence.Add("current hash differs from the stored managed hash");
            return Classified(TargetFileClassification.ChangedManagedFile, hash, architecture,
                evidence, false, true, true);
        }

        var official = (officialArtifacts ?? []).FirstOrDefault(x =>
            NormalizeHash(x) is { } expected && hash.Equals(expected, StringComparison.OrdinalIgnoreCase));
        if (official is not null)
        {
            evidence.Add($"hash matches the official {official.Component} artifact");
            if (!string.IsNullOrWhiteSpace(official.SourceBundleSha256))
                evidence.Add($"official bundle {official.SourceBundleSha256}");
            return Classified(KnownComponent(official.Component), hash, architecture, evidence, true, false, false);
        }

        var historicalRecord = manifest.Files.FirstOrDefault(x =>
            hash.Equals(x.Sha256, StringComparison.OrdinalIgnoreCase) ||
            hash.Equals(x.SourceBlobSha256, StringComparison.OrdinalIgnoreCase));
        if (historicalRecord is not null)
        {
            evidence.Add($"hash matches stored {historicalRecord.Component} installation history at {historicalRecord.RelativePath}");
            return Classified(TargetFileClassification.RecoverableLeftover, hash, architecture,
                evidence, false, true, false);
        }

        var matchingBackup = FindMatchingBackup(game, relative, hash);
        if (matchingBackup is not null)
        {
            evidence.Add($"hash matches a previous backup for this exact target: {Path.GetRelativePath(game.GameRoot, matchingBackup)}");
            return Classified(TargetFileClassification.GameOwned, hash, architecture, evidence, false, false, true);
        }

        var profile = await profiles.MatchAsync(game, cancellationToken);
        if (profile.Profile.KnownGameOwnedDlls.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            evidence.Add($"game profile '{profile.Profile.Id}' identifies this filename as game-owned");
            return Classified(TargetFileClassification.GameOwned, hash, architecture, evidence, false, false, true);
        }

        var metadata = ReadMetadata(target);
        evidence.AddRange(metadata.Evidence);
        if (metadata.IsReShade)
            return Classified(TargetFileClassification.KnownReShadeFile, hash, architecture, evidence, false, false, true);
        if (metadata.IsOptiScaler)
            return Classified(TargetFileClassification.KnownOptiScalerBundleFile, hash, architecture, evidence, false, false, true);
        if (metadata.IsRenoDx || fileName.Contains("renodx", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(fileName) is ".addon64" or ".addon32")
            return Classified(TargetFileClassification.KnownRenoDxRelatedFile, hash, architecture, evidence, false, false, true);
        if (metadata.IsGameVendor || IsSupportedGameLayout(target, officialArtifacts, evidence))
            return Classified(TargetFileClassification.GameOwned, hash, architecture, evidence, false, false, true);

        evidence.Add("no ownership, official hash, component identity, game profile, sibling-layout, or backup evidence matched");
        return Classified(TargetFileClassification.UnknownForeignFile, hash, architecture, evidence, false, false, true);
    }

    private static TargetFileClassification KnownComponent(ComponentKind component) => component switch
    {
        ComponentKind.OptiScaler or ComponentKind.OptiPatcher => TargetFileClassification.KnownOptiScalerBundleFile,
        ComponentKind.ReShade => TargetFileClassification.KnownReShadeFile,
        ComponentKind.RenoDx => TargetFileClassification.KnownRenoDxRelatedFile,
        _ => TargetFileClassification.UnknownForeignFile
    };

    private static string? FindMatchingBackup(SteamGame game, string relative, string hash)
    {
        var backupRoot = Path.Combine(game.GameRoot, ".rhi-linux", "backups");
        if (!Directory.Exists(backupRoot)) return null;
        foreach (var transaction in Directory.EnumerateDirectories(backupRoot))
        {
            var candidate = Path.Combine(transaction, relative);
            if (File.Exists(candidate) && Hash(candidate).Equals(hash, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    private static bool IsSupportedGameLayout(
        string target,
        IReadOnlyList<ComponentArtifact>? officialArtifacts,
        List<string> evidence)
    {
        var fileName = Path.GetFileName(target);
        if (!fileName.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(target)!;
        var officialHashes = (officialArtifacts ?? []).Select(NormalizeHash).Where(x => x is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sibling = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(x => !x.Equals(target, StringComparison.Ordinal) &&
                Path.GetFileName(x).StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x => !officialHashes.Contains(Hash(x)));
        if (sibling is null) return false;
        evidence.Add($"sibling game layout contains {Path.GetFileName(sibling)} with a non-bundle hash");
        return true;
    }

    private static BinaryMetadata ReadMetadata(string path)
    {
        var evidence = new List<string>();
        string company = string.Empty;
        string product = string.Empty;
        string version = string.Empty;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = info.CompanyName ?? string.Empty;
            product = info.ProductName ?? string.Empty;
            version = info.ProductVersion ?? info.FileVersion ?? string.Empty;
            if (company.Length > 0) evidence.Add($"company '{company}'");
            if (product.Length > 0) evidence.Add($"product '{product}'");
            if (version.Length > 0) evidence.Add($"version '{version}'");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }

        try
        {
            bool HasBinary(params string[] markers) => BinaryMarkerScanner.ContainsAny(path, markers);
            bool HasMeta(string value) =>
                company.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                product.Contains(value, StringComparison.OrdinalIgnoreCase);
            var reshade = HasBinary("reshade.me", "ReShade Add-on") || HasMeta("reshade.me") || HasMeta("ReShade");
            var optiScaler = HasBinary("OptiScaler", "OptiFG") || HasMeta("OptiScaler") || HasMeta("OptiFG");
            var renoDx = HasBinary("RenoDX") || HasMeta("RenoDX");
            var gameVendor = HasBinary("Advanced Micro Devices", "AMD FidelityFX", "FidelityFX SDK") ||
                HasMeta("Advanced Micro Devices") || HasMeta("AMD FidelityFX") || HasMeta("FidelityFX SDK");
            if (reshade) evidence.Add("ReShade binary/product marker");
            if (optiScaler) evidence.Add("OptiScaler binary/product marker");
            if (renoDx) evidence.Add("RenoDX binary/product marker");
            if (gameVendor) evidence.Add("AMD FidelityFX vendor/product metadata");
            return new(reshade, optiScaler, renoDx, gameVendor, evidence);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, false, false, false, evidence);
        }
    }

    private static PeArchitecture ReadArchitecture(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return PeArchitecture.Unknown;
            stream.Position = 0x3c;
            var offset = reader.ReadUInt32();
            if (offset > stream.Length - 6) return PeArchitecture.Unknown;
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) return PeArchitecture.Unknown;
            return reader.ReadUInt16() switch
            {
                0x014c => PeArchitecture.X86,
                0x8664 => PeArchitecture.X64,
                0xaa64 => PeArchitecture.Arm64,
                _ => PeArchitecture.Unknown
            };
        }
        catch (IOException) { return PeArchitecture.Unknown; }
        catch (UnauthorizedAccessException) { return PeArchitecture.Unknown; }
    }

    private static string? NormalizeHash(ComponentArtifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
            return artifact.Sha256.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(artifact.Path)) return null;
        return Hash(artifact.Path);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private static string Hash(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record BinaryMetadata(
        bool IsReShade,
        bool IsOptiScaler,
        bool IsRenoDx,
        bool IsGameVendor,
        IReadOnlyList<string> Evidence);
}

public sealed class DeploymentConflictException(string technicalDetails) : InvalidOperationException(
    "No safe installation method is available because an unknown file would need to be replaced. No files were changed.")
{
    public string TechnicalDetails { get; } = technicalDetails;
}
