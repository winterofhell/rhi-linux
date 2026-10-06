using RhiLinux.Core;

namespace RhiLinux.Mods;

public static class ReShadePresetService
{
    public static async Task<byte[]> ReadValidatedPresetAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a ReShade preset with the .ini extension.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The ReShade preset exceeds the 4 MiB size limit.");
        using var buffer = new MemoryStream();
        await ArtifactValidator.CopyWithLimitAsync(stream, buffer, 4 * 1024 * 1024, cancellationToken);
        var bytes = buffer.ToArray();
        var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        var document = IniDocument.Parse(text);
        if (document.HasFatalIssues || document.HasRecoverableIssues)
            throw new InvalidDataException("The selected preset contains malformed INI data.");
        var techniques = document.Get(string.Empty, "Techniques");
        if (techniques is null)
            throw new InvalidDataException("The selected file is not a ReShade preset: Techniques is missing.");
        foreach (var technique in techniques.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = technique.Split('@');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length <= 3 ||
                !parts[1].EndsWith(".fx", StringComparison.OrdinalIgnoreCase) ||
                technique.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || technique.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("The preset contains an invalid technique or shader filename.");
        }
        return bytes;
    }

    public const string HostIniFileName = "ReShade.ini";
    public const string CleanPresetFileName = "ReShadePreset.ini";
    public const string CleanPresetPurpose = "reshade-clean-preset";
    public const string HostConfigPurpose = "reshade-host-config";
    public const string ConfigurationSchema = "reshade-clean-v1";

    public static readonly string[] PreviouslyObservedDefaultEffects =
    [
        "SMAA",
        "Clarity"
    ];

    public static string HostIniPath(SteamGame game) => HostIniPath(game.ToDeploymentTarget());

    public static string HostIniPath(InstalledGame game) => HostIniPath(game.ToDeploymentTarget());

    public static string HostIniPath(DeploymentTarget game) =>
        Path.Combine(game.DeploymentDirectory, HostIniFileName);

    public static string CleanPresetPath(SteamGame game) => CleanPresetPath(game.ToDeploymentTarget());

    public static string CleanPresetPath(InstalledGame game) => CleanPresetPath(game.ToDeploymentTarget());

    public static string CleanPresetPath(DeploymentTarget game) =>
        Path.Combine(game.DeploymentDirectory, CleanPresetFileName);

    public static void ConfigureFreshInstallation(
        DeploymentPlan plan,
        GameManifest manifest,
        SteamGame game,
        bool forceReset = false) =>
        ConfigureFreshInstallation(plan, manifest, game.ToDeploymentTarget(), forceReset);

    public static void ConfigureFreshInstallation(
        DeploymentPlan plan,
        GameManifest manifest,
        InstalledGame game,
        bool forceReset = false) =>
        ConfigureFreshInstallation(plan, manifest, game.ToDeploymentTarget(), forceReset);

    public static void ConfigureFreshInstallation(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentTarget game,
        bool forceReset = false)
    {
        var hostPath = HostIniPath(game);
        var presetPath = CleanPresetPath(game);
        var relativePreset = Path.GetRelativePath(game.GameRoot, presetPath);
        var relativeHost = Path.GetRelativePath(game.GameRoot, hostPath);

        var managedPreset = FindManaged(manifest, relativePreset, CleanPresetPurpose);
        var managedHost = FindManaged(manifest, relativeHost, HostConfigPurpose);

        var presetCustomized = IsUserCustomizedPreset(presetPath, managedPreset);
        var shouldWritePreset = forceReset ||
            !File.Exists(presetPath) ||
            (!presetCustomized && IsDirtyDefaultPreset(presetPath));

        if (shouldWritePreset && (forceReset || !presetCustomized || !File.Exists(presetPath)))
        {
            AddIni(plan, presetPath, string.Empty, "Techniques", string.Empty, ComponentKind.ReShade,
                CleanPresetPurpose, "Create managed ReShade preset with no ordinary shaders enabled");
            AddIni(plan, presetPath, string.Empty, "TechniqueSorting", string.Empty, ComponentKind.ReShade,
                CleanPresetPurpose, "Keep managed ReShade technique sorting empty");
            plan.Operations.Add(new(
                DeploymentOperationType.VerifyIniValue,
                presetPath,
                Value: ":Techniques=",
                Component: ComponentKind.ReShade,
                Description: "Verify the managed ReShade preset enables no ordinary shaders",
                Purpose: CleanPresetPurpose));
        }

        var hostCustomized = IsUserCustomizedHost(hostPath, managedHost);
        var shouldWriteHost = forceReset ||
            !File.Exists(hostPath) ||
            (managedHost is not null && !hostCustomized);

        if (shouldWriteHost && (forceReset || !hostCustomized || !File.Exists(hostPath)))
        {
            if (!(hostCustomized && !forceReset && managedHost is null))
            {
                AddIni(plan, hostPath, "GENERAL", "PresetPath", $".\\{CleanPresetFileName}", ComponentKind.ReShade,
                    HostConfigPurpose, "Point ReShade at the managed clean preset");
                AddIni(plan, hostPath, "ADDON", "AddonPath", ".", ComponentKind.ReShade,
                    HostConfigPurpose, "Keep ReShade add-on loading enabled for RenoDX");
            }
        }
        else if (File.Exists(hostPath) && managedHost is null && !hostCustomized)
        {
            var document = SafeParse(hostPath);
            if (document?.Get("ADDON", "AddonPath") is null)
                AddIni(plan, hostPath, "ADDON", "AddonPath", ".", ComponentKind.ReShade,
                    HostConfigPurpose, "Enable ReShade add-on loading without changing the user preset");
        }
    }

    public static bool IsCleanTechniquesValue(string? value) =>
        string.IsNullOrWhiteSpace(value);

    public static IReadOnlyList<string> ParseTechniqueNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                var at = item.IndexOf('@');
                return at < 0 ? item : item[..at];
            })
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static void AddIni(
        DeploymentPlan plan,
        string path,
        string section,
        string key,
        string value,
        ComponentKind component,
        string purpose,
        string description)
    {
        var expression = $"{section}:{key}={value}";
        plan.Operations.Add(new(
            DeploymentOperationType.WriteIniValue,
            path,
            Value: expression,
            Component: component,
            Description: description,
            ConfigurationSchema: ConfigurationSchema,
            ConfigurationVersion: "1",
            Purpose: purpose,
            BackupPath: Path.Combine(
                plan.GameRoot, ".rhi-linux", "backups", plan.Id,
                Path.GetRelativePath(plan.GameRoot, path))));
    }

    private static ManagedFile? FindManaged(GameManifest manifest, string relativePath, string purpose) =>
        manifest.Files.FirstOrDefault(file =>
            file.RelativePath.Equals(relativePath, StringComparison.Ordinal) &&
            (file.Purpose is null || file.Purpose.Equals(purpose, StringComparison.Ordinal)));

    private static bool IsUserCustomizedPreset(string presetPath, ManagedFile? managed)
    {
        if (!File.Exists(presetPath)) return false;
        var document = SafeParse(presetPath);
        if (document is null) return true;
        var techniques = document.Get(string.Empty, "Techniques") ?? document.Get("GENERAL", "Techniques");
        if (IsCleanTechniquesValue(techniques)) return false;
        if (MatchesObservedDefaultEffects(ParseTechniqueNames(techniques))) return false;
        if (managed?.Purpose == CleanPresetPurpose) return true;
        return managed is null;
    }

    private static bool IsDirtyDefaultPreset(string presetPath)
    {
        if (!File.Exists(presetPath)) return false;
        var document = SafeParse(presetPath);
        if (document is null) return false;
        return MatchesObservedDefaultEffects(ParseTechniqueNames(
            document.Get(string.Empty, "Techniques") ?? document.Get("GENERAL", "Techniques")));
    }

    private static bool MatchesObservedDefaultEffects(IReadOnlyList<string> techniques)
    {
        if (techniques.Count == 0 || techniques.Count != PreviouslyObservedDefaultEffects.Length)
            return false;
        var observed = PreviouslyObservedDefaultEffects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return techniques.All(observed.Contains) &&
            PreviouslyObservedDefaultEffects.All(name =>
                techniques.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsUserCustomizedHost(string hostPath, ManagedFile? managed)
    {
        if (!File.Exists(hostPath)) return false;
        if (managed?.Purpose == HostConfigPurpose) return false;
        var document = SafeParse(hostPath);
        if (document is null) return true;
        var presetPath = document.Get("GENERAL", "PresetPath") ?? document.Get("GENERAL", "CurrentPresetPath");
        if (string.IsNullOrWhiteSpace(presetPath)) return false;
        var fileName = Path.GetFileName(presetPath.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar));
        return !fileName.Equals(CleanPresetFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static IniDocument? SafeParse(string path)
    {
        try { return IniDocument.Parse(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
