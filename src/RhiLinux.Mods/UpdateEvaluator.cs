using RhiLinux.Core;

namespace RhiLinux.Mods;

public static class UpdateEvaluator
{
    public static ComponentStateReport Apply(
        ComponentStateReport report,
        ResolvedArtifact? artifact,
        GameArtifactResolution? resolution = null,
        GameProfileMatch? profile = null)
    {
        if (report.Component == ComponentKind.RenoDx && resolution is not null && profile is not null)
        {
            var mapped = MapMissingReno(report, artifact, resolution, profile);
            if (mapped is not null) return mapped;
        }

        if (artifact is null)
        {
            if (IsInstalledLifecycle(report.State))
                return report with
                {
                    Update = report.Ownership is OwnershipHealth.Unmanaged or OwnershipHealth.Foreign
                        ? UpdateAvailability.ManualInstallationDetected
                        : UpdateAvailability.StatusUnavailable,
                    Evidence = report.Evidence with
                    {
                        AvailableArtifactIdentity = null,
                        ReasonCode = report.Evidence.ReasonCode
                    }
                };
            return report with { Update = UpdateAvailability.NotInstalled };
        }

        if (IsInstalledLifecycle(report.State))
        {
            var comparison = Compare(report, artifact);
            if (comparison == UpdateAvailability.UpdateAvailable &&
                report.Ownership is not (OwnershipHealth.Unmanaged or OwnershipHealth.Foreign) &&
                report.State is not ComponentLifecycleState.InstalledUnmanaged)
            {
                return report with
                {
                    State = ComponentLifecycleState.UpdateAvailable,
                    Update = UpdateAvailability.UpdateAvailable,
                    Version = report.Version ?? artifact.Version,
                    Explanation = BuildUpdateExplanation(report, artifact),
                    Evidence = report.Evidence with
                    {
                        AvailableArtifactIdentity = ArtifactIdentity(artifact),
                        ReasonCode = "update.available"
                    }
                };
            }

            var explanation = comparison switch
            {
                UpdateAvailability.UpToDate when report.Ownership is OwnershipHealth.Unmanaged or OwnershipHealth.Foreign =>
                    "Installed files match the current official build.",
                UpdateAvailability.ManualInstallationDetected =>
                    "Installed manually. No action needed.",
                UpdateAvailability.InstalledVersionUnknown =>
                    "Installed. Version not identified.",
                _ => report.Explanation
            };

            return report with
            {
                Update = comparison,
                Explanation = explanation,
                Evidence = report.Evidence with
                {
                    AvailableArtifactIdentity = ArtifactIdentity(artifact),
                    ReasonCode = comparison switch
                    {
                        UpdateAvailability.UpToDate => "update.up_to_date",
                        UpdateAvailability.ManualInstallationDetected => "update.manual",
                        UpdateAvailability.InstalledVersionUnknown => "update.version_unknown",
                        UpdateAvailability.StatusUnavailable => "update.unavailable",
                        _ => report.Evidence.ReasonCode
                    }
                }
            };
        }

        if (report.State is ComponentLifecycleState.NotInstalled)
        {
            return report with
            {
                Version = artifact.Version,
                Update = UpdateAvailability.NotInstalled,
                Explanation = artifact.CacheState == ArtifactCacheState.Cached
                    ? $"Official version {artifact.Version} is ready for offline use."
                    : $"Official version {artifact.Version} will be downloaded automatically.",
                Compatibility = CompatibilityStatus.Compatible,
                Evidence = report.Evidence with
                {
                    AvailableArtifactIdentity = ArtifactIdentity(artifact),
                    ReasonCode = "artifact.available"
                }
            };
        }

        return report;
    }

    public static ComponentStatus ApplyToStatus(
        ComponentStatus status,
        ResolvedArtifact? artifact,
        GameArtifactResolution? resolution = null,
        GameProfileMatch? profile = null)
    {
        var report = new ComponentStateReport(
            status.Component,
            status.Lifecycle == ComponentLifecycleState.Unknown && status.Health == ComponentHealth.Installed
                ? ComponentLifecycleState.InstalledHealthy
                : status.Lifecycle == ComponentLifecycleState.Unknown && status.Health == ComponentHealth.Available
                    ? ComponentLifecycleState.NotInstalled
                    : status.Lifecycle,
            status.Runtime,
            FileIntegrityStatus.Unknown,
            ConfigurationHealth.Unknown,
            status.Ownership,
            status.Update,
            CompatibilityStatus.Unknown,
            status.Version,
            status.Explanation,
            status.Diagnostic,
            new ComponentStateEvidence(status.Files, [], [], [], null, null, PeArchitecture.Unknown,
                status.Version, null, status.RepairReason, status.ReasonCode ?? "legacy"));
        if (status.Health == ComponentHealth.Installed)
            report = report with
            {
                State = status.Verification == InstallationVerification.MetadataUnverified
                ? ComponentLifecycleState.InstalledMetadataIncomplete
                : status.Verification == InstallationVerification.RecognizedExisting
                    ? ComponentLifecycleState.InstalledUnmanaged
                    : ComponentLifecycleState.InstalledHealthy
            };
        else if (status.Health == ComponentHealth.Outdated)
            report = report with { State = ComponentLifecycleState.UpdateAvailable };
        else if (status.Health is ComponentHealth.Available or ComponentHealth.DownloadRequired or
                 ComponentHealth.Cached or ComponentHealth.Supported or ComponentHealth.Experimental)
            report = report with { State = ComponentLifecycleState.NotInstalled };
        else if (status.Health == ComponentHealth.Unsupported)
            report = report with { State = ComponentLifecycleState.Unsupported };
        else if (status.Health == ComponentHealth.Conflicting)
            report = report with { State = ComponentLifecycleState.Conflict };
        else if (status.Health is ComponentHealth.Broken or ComponentHealth.PartiallyInstalled ||
                 (status.Health is ComponentHealth.IncorrectlyConfigured &&
                  status.Verification == InstallationVerification.RepairNeeded))
            report = report with { State = ComponentLifecycleState.RepairRequired };
        else if (status.Health is ComponentHealth.ForeignInstallation or ComponentHealth.ManifestUnavailable)
            report = report with { State = ComponentLifecycleState.Unknown };

        return StackDetector.ToComponentStatus(Apply(report, artifact, resolution, profile));
    }

    public static UpdateAvailability Compare(ComponentStateReport report, ResolvedArtifact artifact)
    {
        if (artifact.Validation is ArtifactValidationState.Invalid)
            return UpdateAvailability.StatusUnavailable;

        var fingerprint = BuildFingerprint(report);
        var availableIdentity = ArtifactIdentity(artifact);
        var isManual = report.Ownership is OwnershipHealth.Unmanaged or OwnershipHealth.Foreign ||
            report.State == ComponentLifecycleState.InstalledUnmanaged;

        if (!string.IsNullOrWhiteSpace(artifact.Sha256) &&
            TryGetInstalledImmutableHash(report, out var installedHash))
        {
            if (installedHash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                return UpdateAvailability.UpToDate;
            if (!isManual && HasPositiveNewerProof(report, artifact, fingerprint, availableIdentity))
                return UpdateAvailability.UpdateAvailable;
            if (isManual)
                return UpdateAvailability.ManualInstallationDetected;
            return UpdateAvailability.InstalledVersionUnknown;
        }

        if (!string.IsNullOrWhiteSpace(artifact.Sha256) && HasMatchingInstalledHash(report, artifact))
            return UpdateAvailability.UpToDate;

        if (isManual)
        {
            if (TryMatchManualByContent(report, artifact))
                return UpdateAvailability.UpToDate;
            return UpdateAvailability.ManualInstallationDetected;
        }

        var installedIdentity = EffectiveInstalledIdentity(report, fingerprint);
        if (string.IsNullOrWhiteSpace(installedIdentity))
            return UpdateAvailability.InstalledVersionUnknown;

        if (IsRolling(installedIdentity) || IsRolling(artifact.Version))
        {
            if (!string.IsNullOrWhiteSpace(artifact.Sha256) && HasMatchingInstalledHash(report, artifact))
                return UpdateAvailability.UpToDate;
            if (HasPositiveRollingUpdateProof(report, artifact, installedIdentity, availableIdentity))
                return UpdateAvailability.UpdateAvailable;
            return UpdateAvailability.UpToDate;
        }

        var installedVersion = NormalizeVersion(installedIdentity);
        var availableVersion = NormalizeVersion(artifact.Version ?? string.Empty);
        if (installedVersion.Length == 0 || availableVersion.Length == 0)
            return UpdateAvailability.InstalledVersionUnknown;
        if (installedVersion.Equals(availableVersion, StringComparison.OrdinalIgnoreCase))
            return UpdateAvailability.UpToDate;
        if (!string.IsNullOrWhiteSpace(artifact.Sha256) && HasMatchingInstalledHash(report, artifact))
            return UpdateAvailability.UpToDate;
        if (HasPositiveVersionUpdateProof(installedVersion, availableVersion, artifact))
            return UpdateAvailability.UpdateAvailable;
        return UpdateAvailability.InstalledVersionUnknown;
    }

    public static ArtifactFingerprint BuildFingerprint(ComponentStateReport report)
    {
        var path = report.Evidence.DetectedFiles.FirstOrDefault(file =>
            File.Exists(file) && StackDetector.IsRecognizedRuntime(file, report.Component) &&
            IsRuntimeBinaryPath(file, report.Component))
            ?? report.Evidence.DetectedFiles.FirstOrDefault(file =>
                File.Exists(file) && StackDetector.IsRecognizedRuntime(file, report.Component))
            ?? report.Evidence.DetectedFiles.FirstOrDefault()
            ?? string.Empty;
        string? hash = null;
        if (path.Length > 0 && File.Exists(path) && IsRuntimeBinaryPath(path, report.Component))
        {
            try
            {
                using var stream = File.OpenRead(path);
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var sourceIdentity = EffectiveInstalledIdentity(
            report,
            new ArtifactFingerprint(
                report.Component,
                report.Evidence.Architecture,
                path,
                Path.GetFileName(path),
                hash,
                null,
                report.Version,
                null,
                report.Ownership is OwnershipHealth.Managed or OwnershipHealth.Incomplete or OwnershipHealth.Migrated));

        return new ArtifactFingerprint(
            report.Component,
            report.Evidence.Architecture,
            path,
            Path.GetFileName(path),
            hash,
            null,
            report.Version,
            sourceIdentity,
            report.Ownership is OwnershipHealth.Managed or OwnershipHealth.Incomplete or OwnershipHealth.Migrated);
    }

    private static bool HasPositiveNewerProof(
        ComponentStateReport report,
        ResolvedArtifact artifact,
        ArtifactFingerprint fingerprint,
        string availableIdentity)
    {
        if (!string.IsNullOrWhiteSpace(fingerprint.Sha256) &&
            !string.IsNullOrWhiteSpace(artifact.Sha256) &&
            !fingerprint.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var installedIdentity = fingerprint.SourceIdentity ?? report.Version;
            if (IsRolling(installedIdentity) || IsRolling(artifact.Version))
                return HasPositiveRollingUpdateProof(report, artifact, installedIdentity ?? string.Empty, availableIdentity);
            var installedVersion = NormalizeVersion(installedIdentity ?? string.Empty);
            var availableVersion = NormalizeVersion(artifact.Version ?? string.Empty);
            if (installedVersion.Length > 0 && availableVersion.Length > 0 &&
                !installedVersion.Equals(availableVersion, StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrWhiteSpace(artifact.ReleaseTag) &&
                   !string.IsNullOrWhiteSpace(installedIdentity) &&
                   !NormalizeIdentity(installedIdentity).Equals(NormalizeIdentity(artifact.ReleaseTag!), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool HasPositiveRollingUpdateProof(
        ComponentStateReport report,
        ResolvedArtifact artifact,
        string installedIdentity,
        string availableIdentity)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Sha256) &&
            TryGetInstalledImmutableHash(report, out var installedHash) &&
            !installedHash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(artifact.ReleaseTag) &&
                !IsRolling(artifact.ReleaseTag) &&
                !NormalizeIdentity(installedIdentity).Equals(NormalizeIdentity(artifact.ReleaseTag), StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(availableIdentity) &&
                !IsShaIdentity(availableIdentity) &&
                !IsRolling(availableIdentity) &&
                !NormalizeIdentity(installedIdentity).Equals(NormalizeIdentity(availableIdentity), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasPositiveVersionUpdateProof(string installedVersion, string availableVersion, ResolvedArtifact artifact)
    {
        if (StackDetector.IsConfigurationSchemaVersion(installedVersion))
            return false;
        if (installedVersion.Equals(availableVersion, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Version.TryParse(StripPrerelease(installedVersion), out var left) &&
            Version.TryParse(StripPrerelease(availableVersion), out var right))
            return right > left;
        return !string.IsNullOrWhiteSpace(artifact.Version) &&
               !StackDetector.IsConfigurationSchemaVersion(artifact.Version);
    }

    private static string? EffectiveInstalledIdentity(ComponentStateReport report, ArtifactFingerprint fingerprint)
    {
        foreach (var candidate in new[]
                 {
                     fingerprint.SourceIdentity,
                     report.Evidence.InstalledArtifactIdentity,
                     report.Version,
                     fingerprint.DetectedVersion
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (StackDetector.IsConfigurationSchemaVersion(candidate)) continue;
            return candidate;
        }

        return null;
    }

    private static bool IsRuntimeBinaryPath(string path, ComponentKind component)
    {
        var extension = Path.GetExtension(path);
        return component switch
        {
            ComponentKind.RenoDx =>
                extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase),
            _ => extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".asi", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool TryMatchManualByContent(ComponentStateReport report, ResolvedArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Sha256)) return false;
        return HasMatchingInstalledHash(report, artifact);
    }

    private static bool IsShaIdentity(string value) =>
        value.Length == 64 && value.All(static c => char.IsAsciiHexDigit(c));

    private static ComponentStateReport? MapMissingReno(
        ComponentStateReport report,
        ResolvedArtifact? artifact,
        GameArtifactResolution resolution,
        GameProfileMatch profile)
    {
        if (IsInstalledLifecycle(report.State) ||
            report.State is ComponentLifecycleState.RepairRequired or ComponentLifecycleState.RepairRecommended or
                ComponentLifecycleState.Conflict or ComponentLifecycleState.Unknown)
            return null;

        var usableArtifact = artifact is { Selection: not null } ? artifact : null;

        if (usableArtifact is not null &&
            report.State is ComponentLifecycleState.NotInstalled or ComponentLifecycleState.Unsupported)
        {
            var explanation = resolution.RenoDxCompatibility switch
            {
                RenoDxCompatibilityState.InProgress =>
                    "Exact RenoDX addon found (in progress).",
                RenoDxCompatibilityState.ExactAddonAvailableFromOfficialSnapshotRelease =>
                    usableArtifact.CacheState == ArtifactCacheState.Cached
                        ? $"Exact RenoDX addon ready from official snapshot release: {DeployName(usableArtifact)}."
                        : $"Exact RenoDX addon available from official snapshot release: {DeployName(usableArtifact)}.",
                RenoDxCompatibilityState.ExactAddonAvailableFromOfficialDiscussion =>
                    usableArtifact.CacheState == ArtifactCacheState.Cached
                        ? $"Exact RenoDX addon ready from official Discussion: {DeployName(usableArtifact)}."
                        : $"Exact RenoDX addon available from official Discussion: {DeployName(usableArtifact)}.",
                RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable =>
                    "Exact RenoDX addon available for another executable.",
                RenoDxCompatibilityState.GenericUnityAddonAvailable =>
                    "Generic Unity RenoDX addon available.",
                RenoDxCompatibilityState.GenericUnrealAddonAvailable =>
                    "Generic Unreal RenoDX addon available.",
                _ => usableArtifact.CacheState == ArtifactCacheState.Cached
                    ? $"Exact RenoDX addon ready: {DeployName(usableArtifact)}."
                    : $"Exact RenoDX addon found: {DeployName(usableArtifact)}."
            };
            return report with
            {
                State = ComponentLifecycleState.NotInstalled,
                Version = usableArtifact.Version,
                Update = UpdateAvailability.NotInstalled,
                Explanation = explanation,
                Diagnostic = FormatRenoDiagnostics(resolution, profile),
                Compatibility = CompatibilityStatus.Compatible,
                Evidence = report.Evidence with
                {
                    AvailableArtifactIdentity = ArtifactIdentity(usableArtifact),
                    ReasonCode = "renodx.artifact_available"
                }
            };
        }

        if (usableArtifact is null &&
            report.State is ComponentLifecycleState.NotInstalled or ComponentLifecycleState.Unsupported)
        {
            var (state, compatibility, explanation) = MapMissingRenoState(resolution);
            return report with
            {
                State = state,
                Update = UpdateAvailability.NotInstalled,
                Explanation = explanation,
                Diagnostic = FormatRenoDiagnostics(resolution, profile),
                Compatibility = compatibility,
                Evidence = report.Evidence with { ReasonCode = $"renodx.{resolution.RenoDxCompatibility}" }
            };
        }

        return null;
    }

    private static (ComponentLifecycleState State, CompatibilityStatus Compatibility, string Explanation)
        MapMissingRenoState(GameArtifactResolution resolution) =>
        resolution.RenoDxCompatibility switch
        {
            RenoDxCompatibilityState.ListedManualDownloadRequired =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Listed by RenoDX, manual download required"),
            RenoDxCompatibilityState.OfficialPageAvailableNoDirectAddon =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Official RenoDX page found. No direct addon download is available."),
            RenoDxCompatibilityState.MultipleOfficialFilesRequireConfirmation =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Multiple official Discussion addon files require confirmation."),
            RenoDxCompatibilityState.AddonArchitectureMismatch =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Addon found, but no compatible executable architecture is available."),
            RenoDxCompatibilityState.OfficialSourceTemporarilyUnavailable =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Official RenoDX Discussion source is temporarily unavailable."),
            RenoDxCompatibilityState.UnsafeArtifactRejected =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Unsafe or invalid Discussion artifact rejected."),
            RenoDxCompatibilityState.AmbiguousMatch =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Multiple RenoDX catalog candidates require confirmation."),
            RenoDxCompatibilityState.AddonAvailableExecutableSelectionRequired =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Addon found, but no compatible executable was selected"),
            RenoDxCompatibilityState.ExactAddonAvailableForAnotherExecutable =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Exact RenoDX addon available for another executable."),
            RenoDxCompatibilityState.AddonAvailableArchitectureUnconfirmed =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    "Architecture could not be confirmed"),
            RenoDxCompatibilityState.MetadataUnavailable =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Unknown, "RenoDX catalog unavailable"),
            RenoDxCompatibilityState.OfflineCatalogInUse =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Unknown,
                    "Offline RenoDX catalog did not list this game."),
            RenoDxCompatibilityState.AntiCheatOrSafetyRestriction =>
                (ComponentLifecycleState.Unsupported, CompatibilityStatus.Unsupported,
                    resolution.RenoDxMatch?.RejectedReason ??
                    "Anti-cheat or safety restriction blocks RenoDX."),
            RenoDxCompatibilityState.UnsupportedEngineOrApi =>
                (ComponentLifecycleState.Unsupported, CompatibilityStatus.Unsupported,
                    resolution.RenoDxMatch?.RejectedReason ??
                    "Unsupported engine or API for RenoDX."),
            RenoDxCompatibilityState.Unsupported =>
                (ComponentLifecycleState.Unsupported, CompatibilityStatus.Unsupported,
                    resolution.RenoDxMatch?.RejectedReason ??
                    "RenoDX cannot safely apply to this game."),
            RenoDxCompatibilityState.SupersededByGenericAddon =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Compatible,
                    resolution.RenoDxMatch?.RejectedReason ??
                    "Exact profile was superseded by a generic RenoDX addon"),
            RenoDxCompatibilityState.NotListed or RenoDxCompatibilityState.NoAddonFound =>
                (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Unknown, "No RenoDX addon found"),
            _ => (ComponentLifecycleState.NotInstalled, CompatibilityStatus.Unknown, "No RenoDX addon found")
        };

    private static bool IsInstalledLifecycle(ComponentLifecycleState state) =>
        state is ComponentLifecycleState.InstalledHealthy or ComponentLifecycleState.InstalledWithWarnings or
            ComponentLifecycleState.InstalledMetadataIncomplete or ComponentLifecycleState.InstalledUnmanaged or
            ComponentLifecycleState.UpdateAvailable;

    private static bool IsRolling(string? value) =>
        value is not null &&
        (value.Equals("snapshot", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("rolling", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("snapshot", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("discussion-", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        return trimmed.Trim();
    }

    private static string NormalizeIdentity(string value)
    {
        var normalized = NormalizeVersion(value);
        var query = normalized.IndexOf('?', StringComparison.Ordinal);
        return query >= 0 ? normalized[..query] : normalized;
    }

    private static string StripPrerelease(string value)
    {
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? value[..dash] : value;
    }

    private static string ArtifactIdentity(ResolvedArtifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.ReleaseTag) && !IsRolling(artifact.ReleaseTag))
            return artifact.ReleaseTag!;
        if (!string.IsNullOrWhiteSpace(artifact.Version) && !IsRolling(artifact.Version))
            return artifact.Version!;
        if (!string.IsNullOrWhiteSpace(artifact.Sha256)) return artifact.Sha256;
        if (!string.IsNullOrWhiteSpace(artifact.ReleaseTag)) return artifact.ReleaseTag!;
        return artifact.Version ?? string.Empty;
    }

    private static string BuildUpdateExplanation(ComponentStateReport report, ResolvedArtifact artifact)
    {
        if (report.Version is not null &&
            !IsRolling(report.Version) &&
            !IsRolling(artifact.Version) &&
            !NormalizeVersion(report.Version).Equals(NormalizeVersion(artifact.Version ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            return $"Installed {report.Version}; official release {artifact.Version} is available.";
        return "A newer official build is available.";
    }

    private static bool HasMatchingInstalledHash(ComponentStateReport report, ResolvedArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Sha256)) return false;
        return TryGetInstalledImmutableHash(report, out var hash) &&
               hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetInstalledImmutableHash(ComponentStateReport report, out string hash)
    {
        hash = string.Empty;
        foreach (var file in report.Evidence.DetectedFiles)
        {
            if (!File.Exists(file)) continue;
            if (!StackDetector.IsRecognizedRuntime(file, report.Component)) continue;
            if (!IsRuntimeBinaryPath(file, report.Component)) continue;
            try
            {
                using var stream = File.OpenRead(file);
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static string FormatRenoDiagnostics(GameArtifactResolution resolution, GameProfileMatch profile)
    {
        var match = resolution.RenoDxMatch;
        var parts = new List<string>
        {
            $"Steam title: {profile.Profile.CanonicalName}",
            $"Match type: {match?.MatchType.ToString() ?? "None"}",
            $"Compatibility: {resolution.RenoDxCompatibility}",
            $"Confidence: {match?.Confidence:0.00}",
            $"Reason: {match?.ReasonCode ?? "none"}"
        };
        if (match?.Entry is { } entry)
        {
            parts.Add($"Wiki title: {entry.CanonicalName}");
            parts.Add($"Expected filename: {entry.ExpectedAddonFileName ?? entry.ArtifactFileName ?? "none"}");
            parts.Add($"Source type: {entry.SourceType}");
            parts.Add($"Status: {entry.Status}");
            if (entry.DiscussionUrl is not null)
                parts.Add($"Official page: {entry.DiscussionUrl}");
            if (entry.DeploymentRelativeDirectory is not null)
                parts.Add($"Deployment directory: {entry.DeploymentRelativeDirectory}");
        }
        if (resolution.SnapshotResolution is { } snapshot)
        {
            parts.Add($"Snapshot release: {snapshot.Index?.ReleaseTag ?? "none"}");
            if (snapshot.Index is not null) parts.Add($"Release ID: {snapshot.Index.ReleaseId}");
            if (snapshot.ReleaseCommit is not null) parts.Add($"Release commit: {snapshot.ReleaseCommit}");
            if (snapshot.Asset is not null)
            {
                parts.Add($"Asset ID: {snapshot.Asset.Id}");
                parts.Add($"Asset filename: {snapshot.Asset.Name}");
            }
            if (snapshot.PublishedDigest is not null) parts.Add($"Published digest: {snapshot.PublishedDigest}");
            parts.Add($"Selection reason: {snapshot.SelectionReason}");
            parts.Add(snapshot.UsedStructuredMetadata
                ? "Structured snapshot metadata mapped this game."
                : "Structured snapshot metadata did not map this game.");
            if (snapshot.AmbiguousCandidates.Count > 0)
                parts.Add("Ambiguous candidates: " + string.Join(", ", snapshot.AmbiguousCandidates));
        }
        if (resolution.ExpectedAddonFileName is not null)
            parts.Add($"Resolved expected filename: {resolution.ExpectedAddonFileName}");
        if (resolution.CompatibilityNotes.Count > 0)
            parts.Add("Compatibility notes: " + string.Join("; ", resolution.CompatibilityNotes));
        if (resolution.RenoDxCatalogAge is { } age)
            parts.Add($"Catalog cache age: {FormatAge(age)}");
        if (resolution.SuggestedExecutable is { } suggested)
            parts.Add($"Suggested executable: {suggested}");
        if (match?.Evidence is { Count: > 0 })
            parts.Add("Evidence: " + string.Join("; ", match.Evidence.Select(item => item.Detail)));
        if (match?.RejectedReason is { } rejected)
            parts.Add($"Rejected: {rejected}");
        if (match is not null)
            parts.Add(match.UsedEngineFallback
                ? "Generic engine fallback was used."
                : "Generic engine fallback was not used.");
        return string.Join('\n', parts);
    }

    private static string DeployName(ResolvedArtifact artifact) =>
        artifact.Selection?.DeployFileName ?? artifact.AssetFileName ?? artifact.Component.ToString();

    private static string FormatAge(TimeSpan age) =>
        age.TotalMinutes < 1 ? "just now" :
        age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes" :
        age.TotalDays < 1 ? $"{(int)age.TotalHours} hours" :
        $"{(int)age.TotalDays} days";
}
