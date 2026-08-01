using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record OptiScalerIniChange(string Section, string Key, string? PreviousValue, string ResultingValue);

public sealed record OptiScalerConfigurationSchema(
    string Name,
    string Version,
    string ReShadeSection,
    string AsiSection,
    string? ReShadeFileNameKey,
    IReadOnlyList<OptiScalerIniChange> Changes);

public static class OptiScalerConfigurationAdapter
{
    private static readonly string[] FileNameKeys = ["ReShadeDllName", "ReShadePath", "ReShadeDllPath"];

    public static OptiScalerConfigurationSchema Detect(
        string? version,
        string? installedIniPath,
        string? templateIniPath,
        bool enableReShade,
        bool enableAsiPlugins)
    {
        var schemaPath = Existing(templateIniPath) ?? Existing(installedIniPath);
        IniDocument? schemaSource = schemaPath is null ? null : IniDocument.Parse(File.ReadAllText(schemaPath));
        var valuePath = Existing(installedIniPath) ?? schemaPath;
        IniDocument? values = null;
        if (valuePath is not null)
        {
            try { values = IniDocument.Parse(File.ReadAllText(valuePath)); }
            catch (InvalidDataException) when (schemaPath is not null && !schemaPath.Equals(valuePath, StringComparison.Ordinal))
            {
                // A validated release template supplies the values after the planner replaces a
                // malformed managed INI; an unrelated malformed INI remains a blocking collision.
                values = schemaSource;
            }
        }
        var reshadeSection = schemaSource?.FindSectionContaining("LoadReshade");
        var asiSection = schemaSource?.FindSectionContaining("LoadAsiPlugins");

        if (reshadeSection is null || asiSection is null)
        {
            if (!IsKnownPluginsSchema(version))
                throw new NotSupportedException($"OptiScaler {version ?? "unknown"} does not expose a recognized configuration schema.");
            reshadeSection ??= "Plugins";
            asiSection ??= "Plugins";
        }

        var fileNameKey = FileNameKeys.FirstOrDefault(key => schemaSource?.FindSectionContaining(key) is not null);
        var changes = new List<OptiScalerIniChange>
        {
            new(reshadeSection, "LoadReshade", values?.Get(reshadeSection, "LoadReshade"), enableReShade ? "true" : "false"),
            new(asiSection, "LoadAsiPlugins", values?.Get(asiSection, "LoadAsiPlugins"), enableAsiPlugins ? "true" : "false")
        };
        if (fileNameKey is not null && enableReShade)
        {
            var section = schemaSource!.FindSectionContaining(fileNameKey) ?? reshadeSection;
            changes.Add(new(section, fileNameKey, values?.Get(section, fileNameKey), "ReShade64.dll"));
        }
        return new("plugins-v1", version ?? "template-detected", reshadeSection, asiSection, fileNameKey, changes);
    }

    private static bool IsKnownPluginsSchema(string? version)
    {
        if (version is null) return false;
        var normalized = version.TrimStart('v', 'V');
        var semantic = normalized.Split('-', '+')[0];
        return Version.TryParse(semantic, out var parsed) && parsed.Major == 0 && parsed.Minor is 7 or 9;
    }

    private static string? Existing(string? path) => path is not null && File.Exists(path) ? path : null;
}
