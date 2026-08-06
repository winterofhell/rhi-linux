using RhiLinux.Core;

namespace RhiLinux.Sources;

internal static class SourceRecordFactory
{
    public static SourceGameRecord Create(
        string providerId,
        GameStore store,
        GameLauncher launcher,
        string externalId,
        string name,
        string? installRoot,
        string? executableHint,
        string? prefixHint,
        string? workingDirectoryHint,
        GameBinaryPlatform platform,
        CompatibilityEnvironment environment,
        string metadataPath,
        string? configurationPath,
        DateTimeOffset metadataModifiedAt,
        IReadOnlyDictionary<string, string>? attributes = null,
        List<SourceDiagnostic>? diagnostics = null,
        string? sourceFingerprint = null,
        bool requiresConfirmation = false,
        bool isActionable = true,
        string? unsupportedReason = null,
        bool allowOutsideRoot = false)
    {
        diagnostics ??= [];
        attributes ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var attrs = attributes is Dictionary<string, string> mutable
            ? mutable
            : new Dictionary<string, string>(attributes, StringComparer.Ordinal);

        if (allowOutsideRoot)
            attrs["allowOutsideRoot"] = "true";

        string? resolvedExecutable = null;
        if (!string.IsNullOrWhiteSpace(executableHint))
        {
            resolvedExecutable = GameIdentity.ResolveRelativePath(installRoot, executableHint) ?? executableHint;
            if (!string.IsNullOrWhiteSpace(installRoot) &&
                Path.IsPathRooted(resolvedExecutable) &&
                !GameIdentity.IsPathInsideRoot(installRoot, resolvedExecutable) &&
                !allowOutsideRoot)
            {
                diagnostics.Add(new(
                    providerId,
                    SourceDiagnosticCodes.ExecutableOutsideInstallRoot,
                    SourceDiagnosticSeverity.Warning,
                    $"Executable for '{name}' is outside the install root.",
                    resolvedExecutable,
                    metadataPath,
                    externalId));
                resolvedExecutable = null;
            }
            else if (!string.IsNullOrWhiteSpace(resolvedExecutable) && !File.Exists(resolvedExecutable))
            {
                diagnostics.Add(new(
                    providerId,
                    SourceDiagnosticCodes.ExecutableMissing,
                    SourceDiagnosticSeverity.Warning,
                    $"Executable for '{name}' was not found.",
                    resolvedExecutable,
                    metadataPath,
                    externalId));
            }
        }

        if (!string.IsNullOrWhiteSpace(installRoot) && !Directory.Exists(installRoot))
        {
            diagnostics.Add(new(
                providerId,
                SourceDiagnosticCodes.InstallRootMissing,
                SourceDiagnosticSeverity.Warning,
                $"Install root for '{name}' was not found.",
                installRoot,
                metadataPath,
                externalId));
        }

        if (!string.IsNullOrWhiteSpace(prefixHint) && !Directory.Exists(prefixHint))
        {
            diagnostics.Add(new(
                providerId,
                SourceDiagnosticCodes.PrefixMissing,
                SourceDiagnosticSeverity.Info,
                $"Prefix for '{name}' was not found.",
                prefixHint,
                metadataPath,
                externalId));
        }

        if (platform == GameBinaryPlatform.Linux)
        {
            isActionable = false;
            unsupportedReason ??= "Native Linux installation is not a Windows mod target.";
            diagnostics.Add(new(
                providerId,
                SourceDiagnosticCodes.UnsupportedPlatform,
                SourceDiagnosticSeverity.Info,
                unsupportedReason,
                null,
                metadataPath,
                externalId));
        }

        return new SourceGameRecord(
            providerId,
            store,
            launcher,
            externalId,
            name,
            string.IsNullOrWhiteSpace(installRoot) ? null : GameIdentity.NormalizePath(installRoot),
            resolvedExecutable is null ? null : GameIdentity.NormalizePath(resolvedExecutable),
            string.IsNullOrWhiteSpace(prefixHint) ? null : GameIdentity.NormalizePath(prefixHint),
            string.IsNullOrWhiteSpace(workingDirectoryHint) ? null : GameIdentity.NormalizePath(workingDirectoryHint),
            platform,
            environment,
            metadataPath,
            configurationPath,
            metadataModifiedAt,
            attrs,
            diagnostics,
            sourceFingerprint,
            requiresConfirmation,
            isActionable,
            unsupportedReason);
    }

    public static GameBinaryPlatform ParsePlatform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return GameBinaryPlatform.Unknown;
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "windows" or "win32" or "win64" or "win")
            return GameBinaryPlatform.Windows;
        if (normalized is "linux" or "native")
            return GameBinaryPlatform.Linux;
        return GameBinaryPlatform.Unknown;
    }

    public static bool LooksLikeBootstrapExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name is "launch" or "launcher" or "start" or "bootstrap" or "upc" or "origin" or "eadesktop" or
               "epicgameslauncher" or "ubisoftconnect" or "uplay";
    }

    public static bool LooksLikeLowConfidenceProgram(string? name, string? executable)
    {
        var haystack = $"{name} {executable}".ToLowerInvariant();
        string[] negatives =
        [
            "setup", "install", "uninstall", "unins", "launcher", "updater", "crash", "report",
            "redist", "redistributable", "vcredist", "directx", "control panel", "winecfg",
            "regedit", "browser", "chrome", "firefox", "steam.exe", "epicgameslauncher"
        ];
        return negatives.Any(token => haystack.Contains(token, StringComparison.Ordinal));
    }

    public static GameSourceScanResult EmptyScan(
        string providerId,
        GameSourceRoot root,
        IReadOnlyList<SourceDiagnostic> diagnostics,
        TimeSpan duration,
        string fingerprint = "",
        bool fromCache = false,
        bool sourceChanged = true) =>
        new(
            providerId,
            root,
            [],
            diagnostics,
            [],
            [],
            [],
            fingerprint,
            duration,
            fromCache,
            sourceChanged);

    public static GameSourceScanResult CachedScan(
        string providerId,
        GameSourceRoot root,
        string fingerprint,
        TimeSpan duration,
        IReadOnlyList<string>? metadataFiles = null) =>
        new(
            providerId,
            root,
            [],
            [],
            metadataFiles ?? [],
            [],
            [],
            fingerprint,
            duration,
            true,
            false);
}
