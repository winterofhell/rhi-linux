using System.Security.Cryptography;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum ProxyFileClassification
{
    Absent,
    ManagedReShade,
    ManagedOptiScaler,
    VerifiedOfficialFile,
    ManagedBackup,
    KnownGameOwnedDll,
    KnownThirdPartyInjector,
    UnknownDll,
    BrokenOrPartialManagedInstallation
}

public sealed record ProxyCandidateDiagnostic(
    string ProxyName,
    string Path,
    ProxyFileClassification Classification,
    DetectionConfidence Confidence,
    string Reason,
    string? Sha256,
    PeArchitecture Architecture,
    long? Size,
    IReadOnlyList<string> Evidence,
    bool SafeForNewInstallation,
    int Score = 0,
    bool ProhibitedByProfile = false,
    bool ProtonOverrideAppropriate = true);

public sealed record ProxySelectionResult(
    string? SelectedProxy,
    DetectionConfidence Confidence,
    string Reason,
    IReadOnlyList<ProxyCandidateDiagnostic> Candidates,
    IReadOnlyList<string> RejectedAlternatives,
    string? LaunchOption)
{
    public bool HasSafeProxy => SelectedProxy is not null;
}

public sealed class ProxyDiagnosticsService(GameProfileCatalog? profiles = null)
{
    private readonly GameProfileCatalog profiles = profiles ?? new GameProfileCatalog();
    private readonly TargetFileClassifier targetFileClassifier = new(profiles ?? new GameProfileCatalog());

    public Task<ProxySelectionResult> DiagnoseAsync(SteamGame game, CancellationToken cancellationToken = default) =>
        DiagnoseAsync(game.ToDeploymentTarget(), cancellationToken);

    public Task<ProxySelectionResult> DiagnoseAsync(InstalledGame game, CancellationToken cancellationToken = default) =>
        DiagnoseAsync(game.ToDeploymentTarget(), cancellationToken);

    public async Task<ProxySelectionResult> DiagnoseAsync(DeploymentTarget game, CancellationToken cancellationToken = default)
        => await DiagnoseAsync(game, null, null, cancellationToken);

    public Task<ProxySelectionResult> DiagnoseAsync(
        SteamGame game,
        ComponentKind desiredComponent,
        IReadOnlyList<ComponentArtifact> officialArtifacts,
        CancellationToken cancellationToken = default) =>
        DiagnoseAsync(game.ToDeploymentTarget(), desiredComponent, officialArtifacts, cancellationToken);

    public Task<ProxySelectionResult> DiagnoseAsync(
        InstalledGame game,
        ComponentKind desiredComponent,
        IReadOnlyList<ComponentArtifact> officialArtifacts,
        CancellationToken cancellationToken = default) =>
        DiagnoseAsync(game.ToDeploymentTarget(), desiredComponent, officialArtifacts, cancellationToken);

    public async Task<ProxySelectionResult> DiagnoseAsync(
        DeploymentTarget game,
        ComponentKind desiredComponent,
        IReadOnlyList<ComponentArtifact> officialArtifacts,
        CancellationToken cancellationToken = default)
        => await DiagnoseAsync(game, (ComponentKind?)desiredComponent, officialArtifacts, cancellationToken);

    private async Task<ProxySelectionResult> DiagnoseAsync(
        DeploymentTarget game,
        ComponentKind? desiredComponent,
        IReadOnlyList<ComponentArtifact>? officialArtifacts,
        CancellationToken cancellationToken)
    {
        var profileMatch = await profiles.MatchAsync(game, cancellationToken);
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        var orderedNames = profileMatch.Profile.PreferredProxyNames
            .Concat(DeploymentPlanner.SupportedProxyNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = new List<ProxyCandidateDiagnostic>();
        foreach (var proxyName in orderedNames)
            candidates.Add(await ClassifyAsync(game, profileMatch.Profile, manifest, proxyName,
                desiredComponent, officialArtifacts, cancellationToken));

        candidates = candidates.Select((candidate, index) => candidate with
        {
            Score = Score(candidate, profileMatch.Profile, index),
            ProhibitedByProfile = profileMatch.Profile.RejectedProxyNames.Contains(candidate.ProxyName, StringComparer.OrdinalIgnoreCase),
            ProtonOverrideAppropriate = DeploymentPlanner.SupportedProxyNames.Contains(candidate.ProxyName, StringComparer.OrdinalIgnoreCase)
        }).OrderByDescending(x => x.Score).ThenBy(x => Array.IndexOf(orderedNames, x.ProxyName)).ToList();
        var selected = candidates.FirstOrDefault(x => x.SafeForNewInstallation && !x.ProhibitedByProfile && x.ProtonOverrideAppropriate);
        var rejected = candidates.Where(x => selected is null || !x.ProxyName.Equals(selected.ProxyName, StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.ProxyName}: {RejectReason(profileMatch.Profile, x)}").ToArray();
        if (selected is null)
        {
            return new(null, DetectionConfidence.High,
                "No safe proxy filename is available. Existing game-owned, managed-broken, third-party, and unknown DLLs were left untouched.",
                candidates, rejected, null);
        }

        var reason = selected.Classification == ProxyFileClassification.Absent
            ? $"Selected {selected.ProxyName}: it is the highest-ranked unoccupied proxy allowed by profile '{profileMatch.Profile.Id}'."
            : $"Selected {selected.ProxyName}: {selected.Reason}";
        return new(selected.ProxyName, selected.Confidence, reason, candidates, rejected,
            DeploymentPlanner.GenerateLaunchOption(selected.ProxyName));
    }

    private static int Score(ProxyCandidateDiagnostic candidate, GameProfile profile, int originalIndex)
    {
        var preferredIndex = profile.PreferredProxyNames.ToList().FindIndex(x => x.Equals(candidate.ProxyName, StringComparison.OrdinalIgnoreCase));
        var baseScore = candidate.Classification switch
        {
            ProxyFileClassification.ManagedOptiScaler => 1000,
            ProxyFileClassification.ManagedReShade => 950,
            ProxyFileClassification.VerifiedOfficialFile => 925,
            ProxyFileClassification.BrokenOrPartialManagedInstallation when candidate.SafeForNewInstallation => 900,
            ProxyFileClassification.Absent when preferredIndex >= 0 => 800 - preferredIndex * 10,
            ProxyFileClassification.Absent => 600 - originalIndex,
            _ => 0
        };
        return profile.RejectedProxyNames.Contains(candidate.ProxyName, StringComparer.OrdinalIgnoreCase) ? baseScore - 2000 : baseScore;
    }

    private async Task<ProxyCandidateDiagnostic> ClassifyAsync(
        DeploymentTarget game,
        GameProfile profile,
        GameManifest manifest,
        string proxyName,
        ComponentKind? desiredComponent,
        IReadOnlyList<ComponentArtifact>? officialArtifacts,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(game.DeploymentDirectory, proxyName);
        if (!File.Exists(path))
            return new(proxyName, path, ProxyFileClassification.Absent, DetectionConfidence.High,
                "No file occupies this proxy name.", null, PeArchitecture.Unknown, null, ["target is absent"], true);

        var info = new FileInfo(path);
        var hash = await HashAsync(path, cancellationToken);
        var architecture = ReadArchitecture(path);
        var relative = Path.GetRelativePath(game.GameRoot, path);
        var managed = manifest.Files.SingleOrDefault(x => x.RelativePath.Equals(relative, StringComparison.Ordinal));
        var evidence = new List<string> { $"SHA-256 {hash}", $"size {info.Length} bytes", $"PE architecture {architecture}" };
        var exactOfficial = desiredComponent is null ? null : (officialArtifacts ?? []).FirstOrDefault(artifact =>
            artifact.Component == desiredComponent &&
            ArtifactHash(artifact)?.Equals(hash, StringComparison.OrdinalIgnoreCase) == true);
        if (exactOfficial is not null)
        {
            evidence.Add($"hash matches the selected official {exactOfficial.Component} release {exactOfficial.Version ?? "unknown"}");
            return Result(ProxyFileClassification.VerifiedOfficialFile, DetectionConfidence.High,
                "The existing file is byte-identical to the selected official release and can be adopted without replacement.", true);
        }
        if (managed is not null)
        {
            evidence.Add($"ownership manifest records component {managed.Component}");
            if (!hash.Equals(managed.Sha256, StringComparison.OrdinalIgnoreCase))
                return Result(ProxyFileClassification.BrokenOrPartialManagedInstallation, DetectionConfidence.High,
                    "The ownership manifest records this file, but its current hash differs from the installed hash. It can be preserved and repaired transactionally.", true);
            return managed.Component switch
            {
                ComponentKind.ReShade => Result(ProxyFileClassification.ManagedReShade, DetectionConfidence.High,
                    "The ownership manifest and stored hash identify managed ReShade.", true),
                ComponentKind.OptiScaler => Result(ProxyFileClassification.ManagedOptiScaler, DetectionConfidence.High,
                    "The ownership manifest and stored hash identify managed OptiScaler.", true),
                _ => Result(ProxyFileClassification.BrokenOrPartialManagedInstallation, DetectionConfidence.High,
                    $"The proxy name is unexpectedly owned by {managed.Component}.", false)
            };
        }

        var targetDiagnostic = await targetFileClassifier.ClassifyAsync(
            game, manifest, path, cancellationToken: cancellationToken);
        evidence.AddRange(targetDiagnostic.Evidence.Where(x => !evidence.Contains(x, StringComparer.Ordinal)));
        if (targetDiagnostic.Classification == TargetFileClassification.RecoverableLeftover)
            return Result(ProxyFileClassification.BrokenOrPartialManagedInstallation, DetectionConfidence.High,
                "Exact installation history identifies a recoverable managed leftover.", true);
        if (targetDiagnostic.Classification == TargetFileClassification.GameOwned)
            return Result(ProxyFileClassification.KnownGameOwnedDll, DetectionConfidence.High,
                "Game profile, metadata, sibling files, or a previous backup identifies this as game-owned.", false);
        if (targetDiagnostic.Classification is TargetFileClassification.KnownReShadeFile or
            TargetFileClassification.KnownOptiScalerBundleFile or TargetFileClassification.KnownRenoDxRelatedFile)
            return Result(ProxyFileClassification.KnownThirdPartyInjector, DetectionConfidence.High,
                "Component metadata identifies a known unmanaged compatibility file.", false);

        var markers = ReadMarkers(path);
        evidence.AddRange(markers.Evidence);
        if (profile.KnownGameOwnedDlls.Contains(proxyName, StringComparer.OrdinalIgnoreCase))
        {
            evidence.Add($"profile '{profile.Id}' records this DLL as game-owned at the deployment root");
            return Result(ProxyFileClassification.KnownGameOwnedDll, DetectionConfidence.High,
                $"Known game-owned {proxyName}; it is a collision for this name, not a global injector conflict.", false);
        }
        if (markers.IsReShade)
            return Result(ProxyFileClassification.KnownThirdPartyInjector, DetectionConfidence.High,
                "Binary markers identify an unmanaged ReShade installation.", false);
        if (markers.IsOptiScaler)
            return Result(ProxyFileClassification.KnownThirdPartyInjector, DetectionConfidence.High,
                "Binary markers identify an unmanaged OptiScaler installation.", false);
        if (markers.IsKnownInjector)
            return Result(ProxyFileClassification.KnownThirdPartyInjector, DetectionConfidence.Medium,
                "Binary markers identify a known third-party injector or compatibility layer.", false);
        if (markers.IsMicrosoftSystemDll)
            return Result(ProxyFileClassification.KnownGameOwnedDll, DetectionConfidence.Medium,
                "Microsoft product metadata indicates a game-bundled system DLL; it will not be replaced.", false);
        return Result(ProxyFileClassification.UnknownDll, DetectionConfidence.High,
            "The DLL is not owned by RHI Linux and has no trustworthy managed-component signature.", false);

        ProxyCandidateDiagnostic Result(ProxyFileClassification classification, DetectionConfidence confidence, string reason, bool safe) =>
            new(proxyName, path, classification, confidence, reason, hash, architecture, info.Length, evidence, safe);
    }

    private static string RejectReason(GameProfile profile, ProxyCandidateDiagnostic candidate)
    {
        if (candidate.ProhibitedByProfile)
            return $"profile rejects this name; {candidate.Reason}";
        return candidate.SafeForNewInstallation ? "lower-ranked safe alternative" : candidate.Reason;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static string? ArtifactHash(ComponentArtifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
            return artifact.Sha256.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (!File.Exists(artifact.Path)) return null;
        using var input = File.OpenRead(artifact.Path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
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
            return reader.ReadUInt16() switch { 0x014c => PeArchitecture.X86, 0x8664 => PeArchitecture.X64, 0xaa64 => PeArchitecture.Arm64, _ => PeArchitecture.Unknown };
        }
        catch (IOException) { return PeArchitecture.Unknown; }
        catch (UnauthorizedAccessException) { return PeArchitecture.Unknown; }
    }

    private static MarkerResult ReadMarkers(string path)
    {
        try
        {
            var reshade = BinaryMarkerScanner.ContainsAny(path, "reshade.me", "ReShade Add-on");
            var optiScaler = BinaryMarkerScanner.ContainsAny(path, "OptiScaler", "OptiFG");
            var injector = BinaryMarkerScanner.ContainsAny(path, "Special K", "Ultimate ASI Loader", "DXVK");
            var microsoft = BinaryMarkerScanner.ContainsAny(path, "Microsoft Corporation") &&
                BinaryMarkerScanner.ContainsAny(path, "Debugging Tools for Windows", "Windows Debugger");
            var evidence = new List<string>();
            if (reshade) evidence.Add("ReShade-specific binary marker");
            if (optiScaler) evidence.Add("OptiScaler-specific binary marker");
            if (injector) evidence.Add("known third-party injector marker");
            if (microsoft) evidence.Add("Microsoft debugger product/company metadata");
            return new(reshade, optiScaler, injector, microsoft, evidence);
        }
        catch (IOException) { return new(false, false, false, false, ["binary markers could not be read"]); }
        catch (UnauthorizedAccessException) { return new(false, false, false, false, ["binary markers could not be read"]); }
    }

    private sealed record MarkerResult(bool IsReShade, bool IsOptiScaler, bool IsKnownInjector, bool IsMicrosoftSystemDll, IReadOnlyList<string> Evidence);
}
