using System.Security.Cryptography;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed partial class DeploymentPlanner
{
    public async Task<DeploymentPlan> BuildOptiScalerSettingsPlanAsync(
        DeploymentTarget game,
        OptiScalerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var (manifest, path, document) = await ReadManagedConfigurationAsync(
            game, ComponentKind.OptiScaler, "OptiScaler.ini", cancellationToken);
        OptiScalerSettingsService.ValidateDocument(document);
        var changes = settings.Values.Where(change =>
            !string.Equals(document.Get(change.Section, change.Key), change.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var change in changes) OptiScalerSettingsService.ValidateChange(change.Key, change.Value);
        if (changes.Length == 0) throw new InvalidOperationException("The selected settings already match OptiScaler.ini.");
        OptiScalerSettingsService.ValidateDependencies(game, settings);
        var plan = NewPlan(game, "configure OptiScaler");
        foreach (var change in changes)
            AddConfigurationChange(plan, manifest, game, path, ComponentKind.OptiScaler,
                change.Section, change.Key, change.Value, "optiscaler-amd-v1");
        plan.Warnings.Add("Restart the game to load the new settings. Frame generation compatibility depends on the game, graphics API, and Proton version.");
        CompleteConfigurationPlan(plan, manifest, game);
        return plan;
    }

    public async Task<DeploymentPlan> BuildReShadePresetPlanAsync(
        DeploymentTarget game,
        string? presetSource,
        CancellationToken cancellationToken = default)
    {
        var (manifest, host, _) = await ReadManagedConfigurationAsync(
            game, ComponentKind.ReShade, ReShadePresetService.HostIniFileName, cancellationToken);
        var target = ReShadePresetService.CleanPresetPath(game);
        if (File.Exists(target) && !ManagedAt(manifest, game, target, ComponentKind.ReShade))
            throw new DeploymentConflictException("The existing ReShade preset is not managed. Choose it in ReShade directly to preserve its ownership.");
        var plan = NewPlan(game, presetSource is null ? "reset ReShade preset" : "import ReShade preset");
        if (presetSource is null)
        {
            AddConfigurationChange(plan, manifest, game, target, ComponentKind.ReShade,
                string.Empty, "Techniques", string.Empty, ReShadePresetService.ConfigurationSchema);
            AddConfigurationChange(plan, manifest, game, target, ComponentKind.ReShade,
                string.Empty, "TechniqueSorting", string.Empty, ReShadePresetService.ConfigurationSchema);
        }
        else
        {
            var source = Path.GetFullPath(presetSource);
            if (source.Equals(Path.GetFullPath(target), StringComparison.Ordinal))
                throw new InvalidOperationException("This preset is already the deployed preset.");
            var bytes = await ReShadePresetService.ReadValidatedPresetAsync(source, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var backup = ConfigurationBackupPath(plan, game, target);
            if (File.Exists(target))
                plan.Operations.Add(new(DeploymentOperationType.Backup, target, backup,
                    ExpectedSha256: HashPath(target), Component: ComponentKind.ReShade,
                    Description: "Preserve the previous ReShade preset"));
            plan.Operations.Add(new(DeploymentOperationType.Copy, target, source,
                ExpectedSha256: hash, BackupPath: File.Exists(target) ? backup : null,
                Component: ComponentKind.ReShade, Purpose: "reshade-imported-preset",
                Description: "Import the selected ReShade preset", PreserveOriginalBackup: true));
            plan.Operations.Add(new(DeploymentOperationType.VerifySha256, target,
                ExpectedSha256: hash, Description: "Verify the imported preset"));
            plan.Warnings.Add("The preset's shader files must already be installed in ReShade's effect search paths. This import does not download shaders.");
        }
        AddConfigurationChange(plan, manifest, game, host, ComponentKind.ReShade,
            "GENERAL", "PresetPath", $".\\{ReShadePresetService.CleanPresetFileName}", ReShadePresetService.ConfigurationSchema);
        CompleteConfigurationPlan(plan, manifest, game);
        return plan;
    }

    private static async Task<(GameManifest Manifest, string Path, IniDocument Document)> ReadManagedConfigurationAsync(
        DeploymentTarget game,
        ComponentKind component,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!game.IsActionable || game.IsNativeLinux || game.Executable is null)
            throw new InvalidOperationException("Select an actionable Windows game before changing mod settings.");
        var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
        EnsureManifestBelongsToGame(manifest, game);
        var path = Path.Combine(game.DeploymentDirectory, fileName);
        _ = ResolveGamePath(game, Path.GetRelativePath(game.GameRoot, path));
        if (!ManagedAt(manifest, game, path, component) || !File.Exists(path))
            throw new DeploymentConflictException($"Install or repair managed {component} files before changing {fileName}.");
        var runtimeFiles = manifest.Files.Where(file => file.Component == component &&
            file.FileClass == ManagedFileClass.ImmutableRuntimeBinary).ToArray();
        if (runtimeFiles.Length == 0 || runtimeFiles.Any(file =>
            !File.Exists(ResolveGamePath(game, file.RelativePath)) ||
            !HashPath(ResolveGamePath(game, file.RelativePath)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new DeploymentConflictException($"The managed {component} runtime needs repair before settings can change.");
        var document = IniDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        if (document.HasFatalIssues)
            throw new InvalidDataException($"{fileName} is corrupt. Repair the installation first.");
        return (manifest, path, document);
    }

    private static void AddConfigurationChange(
        DeploymentPlan plan,
        GameManifest manifest,
        DeploymentTarget game,
        string path,
        ComponentKind component,
        string section,
        string key,
        string value,
        string schema)
    {
        if (File.Exists(path) && !IniDocument.Parse(File.ReadAllBytes(path)).CanSafelyEditManagedKeys([(section, key)]))
            throw new InvalidDataException($"The {section}.{key} setting cannot be safely edited in {Path.GetFileName(path)}.");
        var backup = File.Exists(path) ? ConfigurationBackupPath(plan, game, path) : null;
        plan.Operations.Add(new(DeploymentOperationType.WriteIniValue, path,
            Value: $"{section}:{key}={value}", BackupPath: backup, Component: component,
            ConfigurationSchema: schema, ConfigurationVersion: VersionOf(manifest, component),
            Description: $"Set {key} to {value}", PreserveOriginalBackup: true));
        plan.Operations.Add(new(DeploymentOperationType.VerifyIniValue, path,
            Value: $"{section}:{key}={value}", Component: component,
            Description: $"Verify {key}"));
    }

    private static string ConfigurationBackupPath(DeploymentPlan plan, DeploymentTarget game, string path) =>
        Path.Combine(game.GameRoot, ".rhi-linux", "backups", plan.Id, Path.GetRelativePath(game.GameRoot, path));

    private static void CompleteConfigurationPlan(DeploymentPlan plan, GameManifest manifest, DeploymentTarget game)
    {
        if (game.RequiresConfirmation)
            plan.Warnings.Add("Anti-cheat files were detected. Explicit compatibility confirmation is required.");
        var proxy = manifest.Files.FirstOrDefault(file =>
            (file.Component is ComponentKind.OptiScaler or ComponentKind.ReShade) &&
            SupportedProxyNames.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase));
        if (proxy is not null)
            plan.LaunchOption = GenerateLaunchOption(game, Path.GetFileName(proxy.RelativePath),
                manifest.Files.Any(file => file.Component == ComponentKind.RenoDx));
        AddManifest(plan, game);
    }
}
