using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Sources;

internal static class LegendaryInstalledParser
{
    public static void Parse(
        string providerId,
        GameLauncher launcher,
        string metadataPath,
        JsonElement root,
        DateTimeOffset modifiedAt,
        Func<string, (string? Prefix, string? TargetExe, string? ConfigPath)>? gameConfigLookup,
        List<SourceGameRecord> games,
        List<SourceGameRecord> malformed,
        List<SourceGameRecord> skipped,
        List<SourceDiagnostic> diagnostics)
    {
        foreach (var element in SourceJson.EnumerateObjectOrArray(root))
        {
            try
            {
                var appName = SourceJson.GetString(element, "app_name", "appName", "AppName") ?? string.Empty;
                var title = SourceJson.GetString(element, "title", "Title", "display_name") ?? appName;
                var installPath = SourceJson.GetString(element, "install_path", "installPath", "path");
                var executable = SourceJson.GetString(element, "executable", "Executable");
                var platform = SourceRecordFactory.ParsePlatform(SourceJson.GetString(element, "platform", "Platform"));
                var isDlc = SourceJson.GetBool(element, "is_dlc", "isDlc") ?? false;
                var needsVerification = SourceJson.GetBool(element, "needs_verification", "needsVerification") ?? false;
                var attributes = SourceJson.CollectAttributes(element, "version", "launch_parameters", "manifest_path", "install_size");

                if (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(title))
                {
                    diagnostics.Add(new(
                        providerId,
                        SourceDiagnosticCodes.SourceRecordIncomplete,
                        SourceDiagnosticSeverity.Warning,
                        "Legendary installed record is missing identity fields.",
                        null,
                        metadataPath,
                        null));
                    malformed.Add(Incomplete(providerId, launcher, metadataPath, modifiedAt, "unknown"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(appName))
                    appName = title;

                if (isDlc)
                {
                    var dlcRecord = SourceRecordFactory.Create(
                        providerId, GameStore.Epic, launcher, appName, title ?? appName, installPath, executable,
                        null, null, platform, CompatibilityEnvironment.Wine, metadataPath, null, modifiedAt,
                        attributes, [new(
                            providerId,
                            SourceDiagnosticCodes.DlcSkipped,
                            SourceDiagnosticSeverity.Info,
                            $"Skipped DLC '{title}'.",
                            null,
                            metadataPath,
                            appName)],
                        isActionable: false,
                        unsupportedReason: "DLC is not independently launchable.");
                    skipped.Add(dlcRecord);
                    diagnostics.Add(dlcRecord.Diagnostics[0]);
                    continue;
                }

                string? prefix = null;
                string? configPath = null;
                if (gameConfigLookup is not null)
                {
                    var config = gameConfigLookup(appName);
                    prefix = config.Prefix;
                    configPath = config.ConfigPath;
                    if (!string.IsNullOrWhiteSpace(config.TargetExe))
                        executable = config.TargetExe;
                }

                if (SourceRecordFactory.LooksLikeBootstrapExecutable(executable))
                {
                    attributes["thirdPartyManaged"] = "true";
                }

                var recordDiagnostics = new List<SourceDiagnostic>();
                if (needsVerification)
                {
                    recordDiagnostics.Add(new(
                        providerId,
                        SourceDiagnosticCodes.SourceRecordIncomplete,
                        SourceDiagnosticSeverity.Info,
                        $"'{title}' still needs verification.",
                        null,
                        metadataPath,
                        appName));
                }

                if (attributes.ContainsKey("thirdPartyManaged") ||
                    (executable is not null && SourceRecordFactory.LooksLikeBootstrapExecutable(executable)))
                {
                    recordDiagnostics.Add(new(
                        providerId,
                        SourceDiagnosticCodes.ThirdPartyManaged,
                        SourceDiagnosticSeverity.Warning,
                        $"'{title}' appears to require a third-party launcher.",
                        executable,
                        metadataPath,
                        appName));
                }

                if (platform == GameBinaryPlatform.Unknown &&
                    !string.IsNullOrWhiteSpace(executable) &&
                    executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    platform = GameBinaryPlatform.Windows;

                var record = SourceRecordFactory.Create(
                    providerId,
                    GameStore.Epic,
                    launcher,
                    appName,
                    title ?? appName,
                    installPath,
                    executable,
                    prefix,
                    null,
                    platform == GameBinaryPlatform.Unknown ? GameBinaryPlatform.Windows : platform,
                    CompatibilityEnvironment.Wine,
                    metadataPath,
                    configPath,
                    modifiedAt,
                    attributes,
                    recordDiagnostics,
                    requiresConfirmation: attributes.ContainsKey("thirdPartyManaged"));
                games.Add(record);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
            {
                diagnostics.Add(new(
                    providerId,
                    SourceDiagnosticCodes.SourceMetadataMalformed,
                    SourceDiagnosticSeverity.Warning,
                    "Failed to parse a Legendary installed record.",
                    exception.Message,
                    metadataPath,
                    null));
                malformed.Add(Incomplete(providerId, launcher, metadataPath, modifiedAt, "malformed"));
            }
        }
    }

    private static SourceGameRecord Incomplete(
        string providerId,
        GameLauncher launcher,
        string metadataPath,
        DateTimeOffset modifiedAt,
        string externalId) =>
        new(
            providerId,
            GameStore.Epic,
            launcher,
            externalId,
            externalId,
            null,
            null,
            null,
            null,
            GameBinaryPlatform.Unknown,
            CompatibilityEnvironment.Unknown,
            metadataPath,
            null,
            modifiedAt,
            new Dictionary<string, string>(),
            [],
            IsActionable: false,
            UnsupportedReason: "Malformed or incomplete source record.");
}
