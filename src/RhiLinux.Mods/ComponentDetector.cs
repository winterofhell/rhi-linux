using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed class ComponentDetector(GameProfileCatalog? profiles = null, ProxyDiagnosticsService? proxyDiagnostics = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GameProfileCatalog profiles = profiles ?? new GameProfileCatalog();
    private readonly ProxyDiagnosticsService proxyDiagnostics = proxyDiagnostics ?? new ProxyDiagnosticsService(profiles);

    public async Task<IReadOnlyList<ComponentStatus>> DetectAsync(SteamGame game, CancellationToken cancellationToken = default)
    {
        GameManifest manifest;
        try
        {
            manifest = await LoadManifestAsync(game.GameRoot, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new[] { ComponentKind.ReShade, ComponentKind.RenoDx, ComponentKind.OptiScaler }
                .Select(component => new ComponentStatus(component, ComponentHealth.ManifestUnavailable, null, [],
                    $"The ownership manifest could not be verified: {exception.Message}"))
                .ToArray();
        }
        cancellationToken.ThrowIfCancellationRequested();
        var profile = await profiles.MatchAsync(game, cancellationToken);
        var proxy = await proxyDiagnostics.DiagnoseAsync(game, cancellationToken);
        var eligibility = OptiScalerEligibilityService.Evaluate(game, profile, proxy);
        var rootFiles = Directory.Exists(game.DeploymentDirectory)
            ? Directory.EnumerateFiles(game.DeploymentDirectory, "*", SearchOption.TopDirectoryOnly).ToArray()
            : [];

        var managedOptiProxy = ExistingManaged(manifest, game, ComponentKind.OptiScaler)
            .FirstOrDefault(x => DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase));
        var managedReShade = ExistingManaged(manifest, game, ComponentKind.ReShade);
        var coexistPath = Path.Combine(game.DeploymentDirectory, "ReShade64.dll");
        var managedCoexist = managedReShade.FirstOrDefault(x => Path.GetFullPath(x).Equals(Path.GetFullPath(coexistPath), StringComparison.Ordinal));
        var recoverableCoexist = File.Exists(coexistPath) && managedCoexist is null && manifest.Files
            .Where(x => x.Component == ComponentKind.ReShade && !File.Exists(Path.Combine(game.GameRoot, x.RelativePath)))
            .Any(x => Hash(coexistPath).Equals(x.Sha256, StringComparison.OrdinalIgnoreCase));
        var iniPath = Path.Combine(game.DeploymentDirectory, "OptiScaler.ini");
        var coexistConfigured = IsCoexistenceConfigured(iniPath);

        var reshadeDetected = rootFiles.Where(x =>
                DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase) &&
                ContainsMarker(x, "ReShade") && !ContainsMarker(x, "OptiScaler"))
            .Concat(File.Exists(coexistPath) ? [coexistPath] : [])
            .Concat(managedReShade)
            .Distinct(StringComparer.Ordinal).ToArray();
        var optiDetected = rootFiles.Where(x =>
            DeploymentPlanner.SupportedProxyNames.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase) &&
            ContainsMarker(x, "OptiScaler"))
            .Concat(managedOptiProxy is not null && manifest.Files.Any(file =>
                file.Component == ComponentKind.OptiScaler &&
                Path.GetFullPath(Path.Combine(game.GameRoot, file.RelativePath)).Equals(
                    Path.GetFullPath(managedOptiProxy), StringComparison.Ordinal) &&
                ManagedFileMatches(game, file)) ? [managedOptiProxy] : [])
            .Distinct(StringComparer.Ordinal).ToArray();
        var renoDetected = ExistingManaged(manifest, game, ComponentKind.RenoDx)
            .Concat(rootFiles.Where(IsRenoDx))
            .Distinct(StringComparer.Ordinal).ToArray();
        var reshade = BaseStatus(ComponentKind.ReShade, reshadeDetected, manifest, game);
        if (recoverableCoexist)
            reshade = reshade with
            {
                Health = ComponentHealth.IncorrectlyConfigured,
                Explanation = "A managed ReShade hash was found in the coexistence slot without OptiScaler. Repair can restore direct loading."
            };
        else if (File.Exists(coexistPath) && managedCoexist is not null && managedOptiProxy is null)
            reshade = reshade with
            {
                Health = ComponentHealth.IncorrectlyConfigured,
                Explanation = "Managed ReShade is stranded in the coexistence slot without a managed OptiScaler proxy. Repair can restore direct loading."
            };
        else if (File.Exists(coexistPath) && managedCoexist is null &&
                 (optiDetected.Length == 0 || !coexistConfigured))
            reshade = reshade with
            {
                Health = ComponentHealth.ForeignInstallation,
                Explanation = "A foreign ReShade64.dll layout was detected. It will not be renamed, overwritten, or removed."
            };
        else if (managedCoexist is not null && managedOptiProxy is not null && !coexistConfigured)
            reshade = reshade with
            {
                Health = ComponentHealth.IncorrectlyConfigured,
                Explanation = "Managed coexistence files exist, but OptiScaler is not configured to load ReShade. Repair is available."
            };
        else if (reshadeDetected.Any(path => ReadPeArchitecture(path) is { } architecture &&
                     architecture != PeArchitecture.Unknown && architecture != SelectedArchitecture(game)))
            reshade = reshade with
            {
                Health = ComponentHealth.IncorrectlyConfigured,
                Explanation = "The managed ReShade architecture does not match the selected game executable. Repair is required."
            };

        var reno = BaseStatus(ComponentKind.RenoDx, renoDetected, manifest, game);
        var managedRenoFiles = ExistingManaged(manifest, game, ComponentKind.RenoDx);
        var invalidReno = manifest.Files.Where(file => file.Component == ComponentKind.RenoDx)
            .FirstOrDefault(file =>
            {
                var path = Path.Combine(game.GameRoot, file.RelativePath);
                if (!File.Exists(path)) return false;
                var provenanceName = RenoDxProvenanceFileName(file);
                var expectedName = provenanceName ?? profile.Profile.RenoDx?.FileName;
                var expectedArchitecture = provenanceName is null
                    ? profile.Profile.RenoDx?.Architecture
                    : Path.GetExtension(provenanceName).Equals(".addon32", StringComparison.OrdinalIgnoreCase)
                        ? PeArchitecture.X86
                        : PeArchitecture.X64;
                return !HasExpectedRenoDxIdentity(path, expectedName, expectedArchitecture);
            });
        if (invalidReno is not null)
            reno = reno with
            {
                Health = ComponentHealth.IncorrectlyConfigured,
                Explanation = $"Managed RenoDX file '{Path.GetFileName(invalidReno.RelativePath)}' does not retain the expected addon filename or architecture. Repair is required."
            };
        else if (reno.Health is not (ComponentHealth.Broken or ComponentHealth.ForeignInstallation) &&
                 manifest.Files.Any(file => file.Component == ComponentKind.RenoDx) && renoDetected.Length == 0)
            reno = reno with
            {
                Health = ComponentHealth.PartiallyInstalled,
                Explanation = "The required RenoDX addon is missing. Repair is required.",
                Verification = InstallationVerification.RepairNeeded
            };
        else if (reno.Health == ComponentHealth.Installed && reshade.Health is not ComponentHealth.Installed and not ComponentHealth.Outdated)
            reno = reno with
            {
                Health = ComponentHealth.IncorrectlyConfigured,
                Explanation = "RenoDX is present, but its required full-addon ReShade host is not installed correctly. Repair is required."
            };
        else if (reno.Health == ComponentHealth.Available && profile.Profile.RenoDx is null)
            reno = reno with { Health = ComponentHealth.Unsupported, Explanation = profile.MatchReason };
        else if (reno.Health == ComponentHealth.Available && !proxy.HasSafeProxy)
            reno = reno with
            {
                Health = ComponentHealth.MissingDependency,
                Explanation = $"Full-addon ReShade has no safe active proxy: {proxy.Reason}"
            };
        else if (reno.Health == ComponentHealth.Available)
            reno = reno with
            {
                Health = ComponentHealth.DownloadRequired,
                Explanation = profile.Profile.RenoDx!.IsGameSpecific
                    ? "The official game-specific RenoDX addon and full-addon ReShade can be installed automatically."
                    : $"The approved {profile.Profile.Engine} RenoDX fallback and full-addon ReShade can be installed automatically."
            };

        var opti = BaseStatus(ComponentKind.OptiScaler, optiDetected, manifest, game);
        var patcherRecords = manifest.Files.Where(x => x.Component == ComponentKind.OptiPatcher).ToArray();
        var invalidPatcherRecords = patcherRecords.Where(x => !ManagedFileMatches(game, x)).ToArray();
        var hasOptiOwnership = manifest.Files.Any(file => file.Component == ComponentKind.OptiScaler);
        var recognizedOptiProxy = optiDetected.FirstOrDefault();
        var hasReShadeForChaining = File.Exists(coexistPath) && ContainsMarker(coexistPath, "ReShade");
        var expectsReShadeChaining = managedCoexist is not null || manifest.Files.Any(file =>
            file.Component == ComponentKind.ReShade &&
            Path.GetFileName(file.RelativePath).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
        if (opti.Health is not (ComponentHealth.Broken or ComponentHealth.ForeignInstallation) &&
            hasOptiOwnership && recognizedOptiProxy is null)
            opti = opti with
            {
                Health = ComponentHealth.PartiallyInstalled,
                Explanation = "The active OptiScaler proxy is missing or is not a recognized OptiScaler binary. Repair is required.",
                Verification = InstallationVerification.RepairNeeded
            };
        else if (recognizedOptiProxy is not null && expectsReShadeChaining && !hasReShadeForChaining)
            opti = opti with
            {
                Health = ComponentHealth.PartiallyInstalled,
                Explanation = "OptiScaler is active, but the required ReShade64.dll chaining file is missing. Repair is required.",
                Verification = InstallationVerification.RepairNeeded
            };
        else if (recognizedOptiProxy is not null && hasReShadeForChaining)
        {
            if (!TryReadOptiScalerPluginFlags(iniPath, out var chainReshade, out var chainAsi))
                opti = opti with
                {
                    Health = ComponentHealth.IncorrectlyConfigured,
                    Explanation = "OptiScaler.ini is malformed and cannot be verified. Repair can restore the required chaining keys while preserving other settings.",
                    Verification = InstallationVerification.RepairNeeded
                };
            else if (chainReshade != true || chainAsi != true)
                opti = opti with
                {
                    Health = ComponentHealth.Installed,
                    Explanation = "OptiScaler is installed. ReShade chaining keys in OptiScaler.ini differ from the recommended values; this mutable configuration change does not require repair.",
                    Verification = InstallationVerification.MetadataUnverified,
                    Diagnostic = "User-editable OptiScaler.ini chaining keys differ from the coexistence defaults. Runtime files remain usable."
                };
        }
        else if (recognizedOptiProxy is not null && !hasReShadeForChaining)
        {
            if (!TryReadOptiScalerPluginFlags(iniPath, out var soloReshade, out var soloAsi))
                opti = opti with
                {
                    Health = ComponentHealth.IncorrectlyConfigured,
                    Explanation = "OptiScaler.ini is malformed and cannot be verified. Repair can restore a valid standalone configuration while preserving other settings.",
                    Verification = InstallationVerification.RepairNeeded
                };
            else if (!IsDisabledOrAutomatic(soloReshade) || !IsDisabledOrAutomatic(soloAsi))
                opti = opti with
                {
                    Health = ComponentHealth.Installed,
                    Explanation = "OptiScaler is installed. Standalone OptiScaler.ini keys differ from the recommended defaults; this mutable configuration change does not require repair.",
                    Verification = InstallationVerification.MetadataUnverified,
                    Diagnostic = "User-editable OptiScaler.ini keys differ from standalone defaults. Runtime files remain usable."
                };
        }
        else if (opti.Health == ComponentHealth.Available)
            opti = eligibility.Level switch
            {
                OptiScalerCompatibilityLevel.Experimental => opti with { Health = ComponentHealth.Experimental, Explanation = eligibility.Explanation },
                OptiScalerCompatibilityLevel.Unsupported or OptiScalerCompatibilityLevel.BlockedByAntiCheat =>
                    opti with { Health = ComponentHealth.Unsupported, Explanation = eligibility.Explanation },
                OptiScalerCompatibilityLevel.BlockedByUnresolvedFileConflict =>
                    opti with { Health = ComponentHealth.Conflicting, Explanation = eligibility.Explanation },
                _ => opti with { Health = ComponentHealth.DownloadRequired, Explanation = eligibility.Explanation }
            };

        if (opti.Health == ComponentHealth.Installed && invalidPatcherRecords.Length > 0 &&
            opti.Verification is InstallationVerification.Managed or InstallationVerification.MetadataUnverified or InstallationVerification.None)
            opti = opti with
            {
                Verification = InstallationVerification.OptionalCleanupAvailable,
                Diagnostic = $"{invalidPatcherRecords.Length} legacy OptiPatcher ownership record(s) no longer match disk. OptiPatcher is not required by the current AMD layout; optional metadata cleanup is available."
            };

        return [reshade, reno, opti];
    }

    public static async Task<GameManifest> LoadManifestAsync(string gameRoot, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(gameRoot, ".rhi-linux", "manifest.json");
        if (!File.Exists(path)) return new GameManifest();
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<GameManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The ownership manifest is empty.");
        if (manifest.SchemaVersion is < 0 or > 1)
            throw new InvalidDataException($"Unsupported ownership manifest schema {manifest.SchemaVersion}.");
        if (manifest.SchemaVersion == 0)
        {
            manifest.SchemaVersion = 1;
            manifest.Files ??= [];
            manifest.ConfigurationPatches ??= [];
            manifest.TransactionIds ??= [];
            manifest.MetadataMigrated = true;
        }
        return manifest;
    }

    private static ComponentStatus BaseStatus(
        ComponentKind component,
        IReadOnlyList<string> detected,
        GameManifest manifest,
        SteamGame game)
    {
        var manifestMatchesGame = manifest.Files.Count == 0 || manifest.AppId == game.AppId;
        var recorded = manifest.Files.Where(x => x.Component == component).ToArray();
        if (!manifestMatchesGame && recorded.Length > 0)
            return new(component, ComponentHealth.ForeignInstallation, null,
                detected.Select(Path.GetFileName).OfType<string>().ToArray(),
                $"The ownership manifest belongs to Steam AppID {manifest.AppId}, not the selected game. The installation is treated as unknown.");
        var unsafeRecords = recorded.Where(x => !IsPathInsideGame(game, x.RelativePath)).ToArray();
        if (unsafeRecords.Length > 0)
            return new(component, ComponentHealth.Broken, recorded.Select(x => x.Version).FirstOrDefault(x => x is not null),
                detected.Select(Path.GetFileName).OfType<string>().ToArray(),
                "The ownership manifest contains an invalid path outside the selected game. Repair is blocked until the manifest is corrected.");
        var owned = recorded;
        var missing = owned.Where(x => !File.Exists(Path.Combine(game.GameRoot, x.RelativePath))).ToArray();
        var changedImmutable = owned.Where(x =>
        {
            if (!IsImmutableRuntimeRecord(x)) return false;
            var path = Path.Combine(game.GameRoot, x.RelativePath);
            return File.Exists(path) && !Hash(path).Equals(x.Sha256, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        var changedMutable = owned.Where(x =>
        {
            if (IsImmutableRuntimeRecord(x)) return false;
            var path = Path.Combine(game.GameRoot, x.RelativePath);
            return File.Exists(path) && !Hash(path).Equals(x.Sha256, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        var installed = detected.Count > 0;
        var metadataUnverified = installed && (missing.Length > 0 || changedImmutable.Length > 0 ||
            changedMutable.Length > 0 || manifest.MetadataMigrated);
        var verification = !installed ? InstallationVerification.None :
            owned.Length == 0 ? InstallationVerification.RecognizedExisting :
            metadataUnverified ? InstallationVerification.MetadataUnverified : InstallationVerification.Managed;
        var health = installed ? ComponentHealth.Installed : ComponentHealth.Available;
        var explanation = installed ? "Installed files form a recognized runtime layout." : "Not installed.";
        var diagnostic = verification switch
        {
            InstallationVerification.RecognizedExisting => "Runtime files are recognized, but no RHI Linux ownership record exists. Removal remains disabled until ownership can be established safely.",
            InstallationVerification.MetadataUnverified => $"Runtime verification succeeded. Ownership metadata differs from disk ({missing.Length} missing record(s), {changedImmutable.Length} immutable hash change(s), {changedMutable.Length} mutable configuration change(s)){(manifest.MetadataMigrated ? "; legacy schema was migrated in memory" : string.Empty)}. Mutable configuration drift does not require repair.",
            _ => null
        };
        return new(component, health, owned.Select(x => x.Version).FirstOrDefault(x => x is not null),
            detected.Select(Path.GetFileName).OfType<string>().ToArray(), explanation, verification, diagnostic);
    }

    private static bool IsImmutableRuntimeRecord(ManagedFile file)
    {
        if (file.FileClass is ManagedFileClass.MutableConfiguration or ManagedFileClass.UserEditableConfiguration or
            ManagedFileClass.ManagedGeneratedFile or ManagedFileClass.Backup)
            return false;
        if (file.FileClass == ManagedFileClass.ImmutableRuntimeBinary) return true;
        var extension = Path.GetExtension(file.RelativePath);
        return !extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) &&
               !extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) &&
               !extension.Equals(".toml", StringComparison.OrdinalIgnoreCase) &&
               !extension.Equals(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ExistingManaged(GameManifest manifest, SteamGame game, ComponentKind component) => manifest.Files
        .Where(x => x.Component == component)
        .Where(x => IsPathInsideGame(game, x.RelativePath))
        .Select(x => Path.Combine(game.GameRoot, x.RelativePath))
        .Where(File.Exists).ToArray();

    private static bool IsRenoDx(string path) =>
        (Path.GetExtension(path).Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".addon32", StringComparison.OrdinalIgnoreCase)) &&
        Path.GetFileName(path).Contains("renodx", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadOptiScalerPluginFlags(string iniPath, out bool? loadReshade, out bool? loadAsiPlugins)
    {
        loadReshade = null;
        loadAsiPlugins = null;
        if (!File.Exists(iniPath)) return false;
        try
        {
            var ini = IniDocument.Parse(File.ReadAllText(iniPath));
            var reshadeSection = ini.FindSectionContaining("LoadReshade");
            var asiSection = ini.FindSectionContaining("LoadAsiPlugins");
            if (reshadeSection is not null && ini.ValueCount(reshadeSection, "LoadReshade") != 1) return false;
            if (asiSection is not null && ini.ValueCount(asiSection, "LoadAsiPlugins") != 1) return false;
            loadReshade = ParseOptionalBool(reshadeSection is null ? null : ini.Get(reshadeSection, "LoadReshade"));
            loadAsiPlugins = ParseOptionalBool(asiSection is null ? null : ini.Get(asiSection, "LoadAsiPlugins"));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool? ParseOptionalBool(string? value)
    {
        if (value is null) return null;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static bool IsCoexistenceConfigured(string iniPath)
    {
        if (!TryReadOptiScalerPluginFlags(iniPath, out var loadReshade, out var loadAsiPlugins)) return false;
        return loadReshade == true && loadAsiPlugins == true;
    }

    private static bool IsStandaloneOptiScalerConfigured(string iniPath)
    {
        if (!TryReadOptiScalerPluginFlags(iniPath, out var loadReshade, out var loadAsiPlugins)) return false;
        return IsDisabledOrAutomatic(loadReshade) && IsDisabledOrAutomatic(loadAsiPlugins);
    }

    private static bool IsDisabledOrAutomatic(bool? value) => value is null or false;

    private static bool IsDisabledOrAutomatic(string? value) => value is null ||
        value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("auto", StringComparison.OrdinalIgnoreCase);

    private static bool HasExpectedRenoDxIdentity(
        string path,
        string? expectedFileName,
        PeArchitecture? expectedArchitecture)
    {
        if (expectedFileName is not null &&
            !Path.GetFileName(path).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) return false;
        var extensionArchitecture = extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
            ? PeArchitecture.X86 : PeArchitecture.X64;
        var peArchitecture = ReadPeArchitecture(path);
        return (expectedArchitecture is null or PeArchitecture.Unknown || extensionArchitecture == expectedArchitecture) &&
            (peArchitecture == PeArchitecture.Unknown || peArchitecture == extensionArchitecture);
    }

    private static string? RenoDxProvenanceFileName(ManagedFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.BundleRelativePath))
        {
            var bundled = Path.GetFileName(file.BundleRelativePath);
            if (Path.GetExtension(bundled).StartsWith(".addon", StringComparison.OrdinalIgnoreCase)) return bundled;
        }
        if (Uri.TryCreate(file.SourceUrl, UriKind.Absolute, out var source))
        {
            var sourceName = Path.GetFileName(Uri.UnescapeDataString(source.AbsolutePath));
            if (Path.GetExtension(sourceName).StartsWith(".addon", StringComparison.OrdinalIgnoreCase)) return sourceName;
        }
        return null;
    }

    private static bool ManagedFileMatches(SteamGame game, ManagedFile file)
    {
        if (!IsPathInsideGame(game, file.RelativePath)) return false;
        var path = Path.Combine(game.GameRoot, file.RelativePath);
        return File.Exists(path) && Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathInsideGame(SteamGame game, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return false;
        var gameRoot = Path.GetFullPath(game.GameRoot);
        var candidate = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
        return candidate.Equals(gameRoot, StringComparison.Ordinal) ||
            candidate.StartsWith(gameRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static PeArchitecture ReadPeArchitecture(string path)
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

    private static PeArchitecture SelectedArchitecture(SteamGame game) =>
        game.Candidates.FirstOrDefault(x => game.Executable is not null &&
            Path.GetFullPath(x.Path).Equals(Path.GetFullPath(game.Executable), StringComparison.Ordinal))?.Architecture ??
        (game.Executable is not null && File.Exists(game.Executable)
            ? ReadPeArchitecture(game.Executable)
            : PeArchitecture.Unknown);

    private static bool ContainsMarker(string path, string marker)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[Math.Min(stream.Length, 2 * 1024 * 1024)];
            _ = stream.Read(bytes);
            return Encoding.ASCII.GetString(bytes).Contains(marker, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
