using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed class EnhancementPlanner(EnhancementSourceClient sources, XdgPaths paths)
{
    public const string ShaderDirectoryName = "reshade-shaders";
    public const string PreservedShaderDirectoryName = "reshade-shaders-original";
    public const string ReShadeIniFileName = "ReShade.ini";
    public const string EffectSearchPaths = @".\reshade-shaders\Shaders\**";
    public const string TextureSearchPaths = @".\reshade-shaders\Textures\**";

    public async Task<EnhancementSnapshot> InspectAsync(DeploymentTarget game, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(game, cancellationToken);
        var hasReShade = await HasReShadeAsync(game, cancellationToken);
        var hasOptiScaler = HasOptiScaler(game);
        var architecture = DeploymentPlanner.SelectedArchitecture(game);
        var components = new List<EnhancementComponentStatus>();
        foreach (var definition in EnhancementCatalog.Components)
        {
            var reason = NotApplicableReason(definition, game, architecture, hasReShade, hasOptiScaler);
            var target = TargetPath(game, definition, architecture);
            var record = state.Find(definition.Kind);
            var managed = record is not null && File.Exists(Path.Combine(game.GameRoot, record.RelativePath));
            var installed = managed || File.Exists(target);
            string? conflict = null;
            if (definition.ExclusiveWith is { } exclusive)
            {
                var other = EnhancementCatalog.Get(exclusive);
                if (File.Exists(TargetPath(game, other, architecture))) conflict = $"{other.Name} is installed";
            }
            components.Add(new(definition.Kind, definition.Name, definition.Summary, reason is null, reason,
                installed, managed, record?.Version, conflict));
        }
        var shaderRoot = Path.Combine(game.DeploymentDirectory, ShaderDirectoryName);
        var treeManaged = state.Shaders is not null && File.Exists(Path.Combine(shaderRoot, EnhancementStateStore.ShaderMarkerFileName));
        var overrides = new List<string>();
        foreach (var definition in EnhancementCatalog.Components)
        {
            if (definition.DllOverride is null) continue;
            var record = state.Find(definition.Kind);
            if (record is not null && File.Exists(Path.Combine(game.GameRoot, record.RelativePath)))
                overrides.Add(definition.DllOverride);
        }
        return new(components, treeManaged ? state.Shaders!.Packs : [], treeManaged,
            Directory.Exists(shaderRoot) && !treeManaged, overrides, EnhancementExecutor.HasPendingRecovery(game.GameRoot));
    }

    public async Task<EnhancementPlan> BuildComponentInstallAsync(
        DeploymentTarget game,
        EnhancementKind kind,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var definition = EnhancementCatalog.Get(kind);
        var state = await LoadStateAsync(game, cancellationToken);
        var resulting = state.Clone();
        var architecture = DeploymentPlanner.SelectedArchitecture(game);
        var plan = CreatePlan(game, $"Install {definition.Name}", resulting, out var stagingDirectory);
        AddCommonWarnings(game, plan);
        var reason = NotApplicableReason(definition, game, architecture,
            await HasReShadeAsync(game, cancellationToken), HasOptiScaler(game));
        if (reason is not null)
        {
            plan.Blockers.Add(reason);
            return plan;
        }
        var target = TargetPath(game, definition, architecture);
        var relative = Relative(game, target);
        if (relative is null)
        {
            plan.Blockers.Add("The deployment directory is outside the game installation.");
            return plan;
        }
        var record = state.Find(kind);
        var managedHere = record is not null && record.RelativePath.Equals(relative, StringComparison.Ordinal);
        if (File.Exists(target) && !managedHere)
        {
            plan.Blockers.Add($"{Path.GetFileName(target)} already exists and was not installed by RHI Linux. It will not be overwritten.");
            return plan;
        }

        if (definition.ExclusiveWith is { } exclusive)
        {
            var other = EnhancementCatalog.Get(exclusive);
            var otherRecord = state.Find(exclusive);
            var otherTarget = TargetPath(game, other, architecture);
            if (otherRecord is not null)
            {
                var otherPath = Path.Combine(game.GameRoot, otherRecord.RelativePath);
                if (File.Exists(otherPath))
                    plan.Steps.Add(new(EnhancementStepType.RemoveFile, exclusive, otherPath,
                        $"Remove {other.Name} because it cannot run alongside {definition.Name}"));
                resulting.Files.RemoveAll(file => file.Kind == exclusive);
            }
            else if (File.Exists(otherTarget))
            {
                plan.Blockers.Add($"{other.Name} is installed outside RHI Linux. Remove it before installing {definition.Name}.");
                return plan;
            }
        }

        foreach (var legacy in definition.LegacyFileNames)
        {
            var legacyPath = Path.Combine(Path.GetDirectoryName(target)!, legacy);
            if (File.Exists(legacyPath))
                plan.Warnings.Add($"{legacy} from an older release is present. Remove it so two copies do not load.");
        }

        ResolvedEnhancementArtifact artifact;
        try
        {
            artifact = await sources.ResolveComponentAsync(definition, architecture, cancellationToken, forceRefresh);
        }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            plan.Blockers.Add($"Could not reach the official {definition.Name} release: {exception.Message}");
            return plan;
        }
        plan.Downloads.Add(new(artifact.Name, artifact.Version, artifact.Source, artifact.Size, sources.IsCached(artifact)));

        if (managedHere && File.Exists(target) && record!.Version == artifact.Version &&
            (await EnhancementExecutor.Sha256Async(target, cancellationToken)).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            plan.Downloads.Clear();
            plan.Warnings.Add($"{definition.Name} {artifact.Version} is already installed and intact.");
            return plan;
        }

        string staged;
        try
        {
            staged = await StageComponentAsync(definition, artifact, architecture, stagingDirectory, cancellationToken);
        }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken) || exception is InvalidDataException or IOException)
        {
            plan.Blockers.Add($"{definition.Name} could not be prepared: {exception.Message}");
            return plan;
        }
        var sha = await EnhancementExecutor.Sha256Async(staged, cancellationToken);
        var verb = managedHere && File.Exists(target) ? "Update" : "Install";
        plan.Steps.Add(new(EnhancementStepType.PlaceFile, kind, target,
            $"{verb} {definition.Name} {artifact.Version} as {relative}", staged, ExpectedSha256: sha));
        resulting.Files.RemoveAll(file => file.Kind == kind);
        resulting.Files.Add(new(kind, relative, sha, artifact.Version, artifact.Source.ToString(), DateTimeOffset.UtcNow));
        if (definition.DllOverride is not null)
            plan.Warnings.Add($"The Steam launch option must include {definition.DllOverride}=n,b. RHI Linux adds it to the generated option.");
        return plan;
    }

    public async Task<EnhancementPlan> BuildComponentRemoveAsync(
        DeploymentTarget game,
        EnhancementKind kind,
        CancellationToken cancellationToken = default)
    {
        var definition = EnhancementCatalog.Get(kind);
        var state = await LoadStateAsync(game, cancellationToken);
        var resulting = state.Clone();
        var plan = CreatePlan(game, $"Remove {definition.Name}", resulting, out _);
        var record = state.Find(kind);
        if (record is null)
        {
            var target = TargetPath(game, definition, DeploymentPlanner.SelectedArchitecture(game));
            if (File.Exists(target))
                plan.Blockers.Add($"{Path.GetFileName(target)} was not installed by RHI Linux and will not be removed.");
            else
                plan.Warnings.Add($"{definition.Name} is not installed.");
            return plan;
        }
        var path = Path.Combine(game.GameRoot, record.RelativePath);
        if (File.Exists(path))
        {
            if (!(await EnhancementExecutor.Sha256Async(path, cancellationToken)).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                plan.Warnings.Add($"{record.RelativePath} changed after installation. It will still be removed because RHI Linux owns this path.");
            plan.Steps.Add(new(EnhancementStepType.RemoveFile, kind, path, $"Remove {definition.Name} ({record.RelativePath})"));
        }
        else
        {
            plan.Warnings.Add($"{record.RelativePath} is already missing; only the record will be cleared.");
            plan.Steps.Add(new(EnhancementStepType.RemoveFile, kind, path, $"Clear the {definition.Name} record"));
        }
        resulting.Files.RemoveAll(file => file.Kind == kind);
        return plan;
    }

    public async Task<EnhancementPlan> BuildShaderPlanAsync(
        DeploymentTarget game,
        IReadOnlyCollection<string> packIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var state = await LoadStateAsync(game, cancellationToken);
        var resulting = state.Clone();
        var packs = ShaderPackCatalog.ResolveWithDependencies(packIds);
        var plan = CreatePlan(game, packs.Count == 0 ? "Remove shader packs" : "Install shader packs", resulting, out var stagingDirectory);
        AddCommonWarnings(game, plan);
        var shaderRoot = Path.Combine(game.DeploymentDirectory, ShaderDirectoryName);
        var preservedRoot = Path.Combine(game.DeploymentDirectory, PreservedShaderDirectoryName);
        if (Relative(game, shaderRoot) is null)
        {
            plan.Blockers.Add("The deployment directory is outside the game installation.");
            return plan;
        }
        var managedTree = state.Shaders is not null && File.Exists(Path.Combine(shaderRoot, EnhancementStateStore.ShaderMarkerFileName));

        if (packs.Count == 0)
        {
            if (state.Shaders is null)
            {
                if (Directory.Exists(shaderRoot))
                    plan.Blockers.Add("The reshade-shaders folder was not created by RHI Linux and will not be removed.");
                else plan.Warnings.Add("No managed shader packs are installed.");
                return plan;
            }
            if (managedTree)
                plan.Steps.Add(new(EnhancementStepType.RemoveShaderTree, EnhancementKind.ShaderPacks, shaderRoot,
                    $"Remove the managed reshade-shaders folder ({state.Shaders.Packs.Count} packs)"));
            else if (Directory.Exists(shaderRoot))
                plan.Warnings.Add("The reshade-shaders folder no longer carries the RHI Linux marker and will be left in place.");
            if (state.Shaders.PreservedOriginalRelativePath is { } preserved)
            {
                var originalPath = Path.Combine(game.GameRoot, preserved);
                if (Directory.Exists(originalPath) && (managedTree || !Directory.Exists(shaderRoot)))
                    plan.Steps.Add(new(EnhancementStepType.RestoreOriginalShaders, EnhancementKind.ShaderPacks, shaderRoot,
                        "Restore the original reshade-shaders folder", originalPath));
            }
            foreach (var patch in state.IniPatches.Where(item => IsShaderPatch(item)).Reverse())
                plan.Steps.Add(RestoreIniStep(game, patch));
            if (plan.Steps.Count == 0)
                plan.Steps.Add(new(EnhancementStepType.RemoveShaderTree, EnhancementKind.ShaderPacks, shaderRoot,
                    "Clear the shader pack record"));
            resulting.Shaders = null;
            resulting.IniPatches.RemoveAll(IsShaderPatch);
            return plan;
        }

        if (!await HasReShadeAsync(game, cancellationToken))
        {
            plan.Blockers.Add("Install ReShade before adding shader packs.");
            return plan;
        }
        if (Directory.Exists(shaderRoot) && !managedTree && Directory.Exists(preservedRoot))
        {
            plan.Blockers.Add("Both reshade-shaders and reshade-shaders-original already exist. Move one of them before continuing.");
            return plan;
        }

        var treeRoot = Path.Combine(stagingDirectory, ShaderDirectoryName);
        Directory.CreateDirectory(Path.Combine(treeRoot, ShaderArchiveStager.ShadersDirectoryName));
        Directory.CreateDirectory(Path.Combine(treeRoot, ShaderArchiveStager.TexturesDirectoryName));
        var installed = new List<InstalledShaderPack>();
        var index = 0;
        foreach (var pack in packs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Preparing {pack.Name} ({++index}/{packs.Count})");
            try
            {
                var artifact = await sources.ResolveShaderPackAsync(pack, cancellationToken, forceRefresh);
                plan.Downloads.Add(new(pack.Name, artifact.Version, artifact.Source, artifact.Size, sources.IsCached(artifact)));
                var archive = await sources.DownloadAsync(artifact, cancellationToken);
                var files = await ShaderArchiveStager.StageAsync(archive, pack.Id, treeRoot, cancellationToken);
                if (files == 0)
                {
                    plan.Warnings.Add($"{pack.Name} contained no shader files and was skipped.");
                    continue;
                }
                installed.Add(new(pack.Id, artifact.Version));
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken) || exception is InvalidDataException or IOException or InvalidOperationException)
            {
                plan.Warnings.Add($"{pack.Name} was skipped: {exception.Message}");
            }
        }
        if (installed.Count == 0)
        {
            plan.Blockers.Add("None of the selected shader packs could be prepared.");
            return plan;
        }
        await File.WriteAllBytesAsync(Path.Combine(treeRoot, EnhancementStateStore.ShaderMarkerFileName),
            JsonSerializer.SerializeToUtf8Bytes(new { packs = installed, createdAt = DateTimeOffset.UtcNow }), cancellationToken);

        string? preservedRelative = state.Shaders?.PreservedOriginalRelativePath;
        if (Directory.Exists(shaderRoot) && !managedTree)
        {
            plan.Steps.Add(new(EnhancementStepType.PreserveOriginalShaders, EnhancementKind.ShaderPacks, preservedRoot,
                "Keep your existing reshade-shaders folder as reshade-shaders-original", shaderRoot));
            preservedRelative = Relative(game, preservedRoot);
        }
        var summary = string.Join(", ", installed.Select(item => ShaderPackCatalog.Find(item.Id)?.Name ?? item.Id));
        plan.Steps.Add(new(EnhancementStepType.InstallShaderTree, EnhancementKind.ShaderPacks, shaderRoot,
            $"{(managedTree ? "Replace" : "Install")} reshade-shaders with {installed.Count} packs: {summary}", treeRoot));

        var iniPath = Path.Combine(game.DeploymentDirectory, ReShadeIniFileName);
        var iniRelative = Relative(game, iniPath)!;
        var current = ReadIni(iniPath);
        foreach (var (key, value) in new[] { ("EffectSearchPaths", EffectSearchPaths), ("TextureSearchPaths", TextureSearchPaths) })
        {
            var existingPatch = state.IniPatches.FirstOrDefault(item =>
                item.RelativePath == iniRelative && item.Section == "GENERAL" && item.Key == key);
            if (existingPatch is not null && current?.Get("GENERAL", key) == value) continue;
            plan.Steps.Add(new(EnhancementStepType.SetIniValue, EnhancementKind.ShaderPacks, iniPath,
                $"Point ReShade {key} at the managed shader folder", Section: "GENERAL", Key: key, Value: value));
            resulting.IniPatches.RemoveAll(item => item.RelativePath == iniRelative && item.Section == "GENERAL" && item.Key == key);
            resulting.IniPatches.Add(new(iniRelative, "GENERAL", key, value,
                existingPatch?.PreviousValue ?? current?.Get("GENERAL", key),
                existingPatch?.CreatedFile ?? current is null));
        }
        resulting.Shaders = new(Relative(game, shaderRoot)!, installed, preservedRelative, DateTimeOffset.UtcNow);
        return plan;
    }

    public async Task<EnhancementPlan> BuildRemoveEverythingAsync(DeploymentTarget game, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(game, cancellationToken);
        var combined = CreatePlan(game, "Remove all enhancements", new EnhancementState { InstallId = state.InstallId }, out _);
        if (state.Shaders is not null)
        {
            var shaders = await BuildShaderPlanAsync(game, [], null, cancellationToken);
            combined.Steps.AddRange(shaders.Steps);
            combined.Warnings.AddRange(shaders.Warnings);
            combined.Blockers.AddRange(shaders.Blockers);
        }
        foreach (var record in state.Files)
        {
            var path = Path.Combine(game.GameRoot, record.RelativePath);
            combined.Steps.Add(new(EnhancementStepType.RemoveFile, record.Kind, path,
                $"Remove {EnhancementCatalog.Get(record.Kind).Name} ({record.RelativePath})"));
        }
        foreach (var patch in state.IniPatches.Where(patch => !IsShaderPatch(patch)).Reverse())
            combined.Steps.Add(RestoreIniStep(game, patch));
        return combined;
    }

    public async Task<IReadOnlyList<EnhancementUpdate>> CheckUpdatesAsync(
        DeploymentTarget game,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var state = await LoadStateAsync(game, cancellationToken);
        var updates = new List<EnhancementUpdate>();
        var architecture = DeploymentPlanner.SelectedArchitecture(game);
        foreach (var record in state.Files)
        {
            try
            {
                var artifact = await sources.ResolveComponentAsync(EnhancementCatalog.Get(record.Kind), architecture, cancellationToken, forceRefresh);
                if (artifact.Version != record.Version)
                    updates.Add(new(record.Kind, EnhancementCatalog.Get(record.Kind).Name, record.Version, artifact.Version));
            }
            catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken) || exception is InvalidDataException)
            {
            }
        }
        if (state.Shaders is not null)
        {
            var outdated = 0;
            foreach (var pack in state.Shaders.Packs)
            {
                var definition = ShaderPackCatalog.Find(pack.Id);
                if (definition is null) continue;
                try
                {
                    var artifact = await sources.ResolveShaderPackAsync(definition, cancellationToken, forceRefresh);
                    if (artifact.Version != pack.Version) outdated++;
                }
                catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken) || exception is InvalidDataException)
                {
                }
            }
            if (outdated > 0)
                updates.Add(new(EnhancementKind.ShaderPacks, "Shader packs", $"{state.Shaders.Packs.Count} packs", $"{outdated} updated"));
        }
        return updates;
    }

    public static string ComposeLaunchOption(string? launchOption, IReadOnlyCollection<string> additionalOverrides)
    {
        var extras = additionalOverrides
            .Where(name => name.Length > 0 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            .Select(name => name.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (extras.Length == 0) return launchOption ?? string.Empty;
        if (string.IsNullOrWhiteSpace(launchOption))
            return $"WINEDLLOVERRIDES=\"{string.Join(';', extras.Select(name => name + "=n,b"))}\" %command%";
        const string marker = "WINEDLLOVERRIDES=\"";
        var start = launchOption.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            var command = launchOption.IndexOf("%command%", StringComparison.Ordinal);
            var fragment = $"WINEDLLOVERRIDES=\"{string.Join(';', extras.Select(name => name + "=n,b"))}\" ";
            return command < 0 ? fragment + launchOption : launchOption.Insert(command, fragment);
        }
        var valueStart = start + marker.Length;
        var end = launchOption.IndexOf('"', valueStart);
        if (end < 0) return launchOption;
        var existing = launchOption[valueStart..end]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var name in extras)
        {
            if (existing.Any(item => item.Split('=')[0].Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            existing.Add(name + "=n,b");
        }
        return launchOption[..valueStart] + string.Join(';', existing) + launchOption[end..];
    }

    public static void Discard(EnhancementPlan plan)
    {
        if (plan.StagingDirectory is not { } staging || !Directory.Exists(staging)) return;
        try
        {
            Directory.Delete(staging, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string TargetPath(DeploymentTarget game, EnhancementDefinition definition, PeArchitecture architecture)
    {
        var name = architecture == PeArchitecture.X86 && definition.TargetName32 is not null
            ? definition.TargetName32
            : definition.TargetName64;
        return definition.TargetSubdirectory is null
            ? Path.Combine(game.DeploymentDirectory, name)
            : Path.Combine(game.DeploymentDirectory, definition.TargetSubdirectory, name);
    }

    private async Task<string> StageComponentAsync(
        EnhancementDefinition definition,
        ResolvedEnhancementArtifact artifact,
        PeArchitecture architecture,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var downloaded = await sources.DownloadAsync(artifact, cancellationToken);
        Directory.CreateDirectory(stagingDirectory);
        var targetName = architecture == PeArchitecture.X86 && definition.TargetName32 is not null
            ? definition.TargetName32
            : definition.TargetName64;
        var staged = Path.Combine(stagingDirectory, targetName);
        if (definition.Payload == EnhancementPayload.ZipMember)
        {
            var format = ManagedArchiveExtractor.DetectFormat(downloaded, ArtifactArchiveKind.Zip);
            if (format is not ArtifactFormat.ZipContainer and not ArtifactFormat.SevenZipContainer)
                throw new InvalidDataException($"The {definition.Name} download is not a valid archive.");
            ManagedArchiveExtractor.Inspect(downloaded, format);
            await ManagedArchiveExtractor.ExtractSingleNamedFileAsync(downloaded, format, definition.PayloadMember!, staged, cancellationToken);
        }
        else
        {
            File.Copy(downloaded, staged, true);
        }
        if (definition.RequiresPeValidation)
        {
            var expected = architecture == PeArchitecture.X86 ? PeArchitecture.X86 : PeArchitecture.X64;
            ArtifactValidator.ValidatePe(staged, expected);
        }
        return staged;
    }

    private EnhancementPlan CreatePlan(DeploymentTarget game, string title, EnhancementState resulting, out string stagingDirectory)
    {
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..27];
        stagingDirectory = Path.Combine(paths.AppCacheDirectory, "enhancements", "staging", id);
        return new EnhancementPlan
        {
            Id = id,
            Title = title,
            Game = game,
            ResultingState = resulting,
            StagingDirectory = stagingDirectory
        };
    }

    private static void AddCommonWarnings(DeploymentTarget game, EnhancementPlan plan)
    {
        if (game.RequiresConfirmation)
            plan.Warnings.Add("This game uses anti-cheat. Injecting add-ons can get your account banned in online modes.");
        if (game.IsNativeLinux)
            plan.Blockers.Add("This is a native Linux build. Enhancements require the Windows version running through Proton.");
    }

    private static string? NotApplicableReason(
        EnhancementDefinition definition,
        DeploymentTarget game,
        PeArchitecture architecture,
        bool hasReShade,
        bool hasOptiScaler)
    {
        if (game.IsNativeLinux) return "Native Linux builds cannot load Windows add-ons.";
        if (architecture == PeArchitecture.X86 && !definition.Supports32Bit) return $"{definition.Name} supports 64-bit games only.";
        return definition.Kind switch
        {
            EnhancementKind.ReLimiter or EnhancementKind.DisplayCommander when !hasReShade => "Install ReShade first.",
            EnhancementKind.ReFramework when game.Engine != GameEngine.ReEngine => "Only RE Engine games are supported.",
            EnhancementKind.OptiPatcher when !hasOptiScaler => "Install OptiScaler first.",
            _ => null
        };
    }

    private static async Task<bool> HasReShadeAsync(DeploymentTarget game, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(game.DeploymentDirectory, "ReShade64.dll")) ||
            File.Exists(Path.Combine(game.DeploymentDirectory, "ReShade32.dll")) ||
            File.Exists(Path.Combine(game.DeploymentDirectory, ReShadeIniFileName)))
            return true;
        try
        {
            var manifest = await ComponentDetector.LoadManifestAsync(game.GameRoot, cancellationToken);
            return manifest.Files.Any(file => file.Component == ComponentKind.ReShade &&
                File.Exists(Path.Combine(game.GameRoot, file.RelativePath)));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasOptiScaler(DeploymentTarget game) =>
        File.Exists(Path.Combine(game.DeploymentDirectory, "OptiScaler.ini")) ||
        File.Exists(Path.Combine(game.DeploymentDirectory, "OptiScaler.dll"));

    private static async Task<EnhancementState> LoadStateAsync(DeploymentTarget game, CancellationToken cancellationToken)
    {
        var state = await EnhancementStateStore.LoadAsync(game.GameRoot, cancellationToken);
        if (state.InstallId is not null && !game.InstallId.IsEmpty && state.InstallId != game.InstallId.Value)
            throw new InvalidDataException("The enhancement record belongs to a different game installation.");
        return state;
    }

    private static string? Relative(DeploymentTarget game, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(game.GameRoot), Path.GetFullPath(path));
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }

    private static bool IsShaderPatch(IniPatchRecord patch) =>
        patch.Section == "GENERAL" && patch.Key is "EffectSearchPaths" or "TextureSearchPaths";

    private static EnhancementStep RestoreIniStep(DeploymentTarget game, IniPatchRecord patch) =>
        new(EnhancementStepType.RestoreIniValue, EnhancementKind.ShaderPacks, Path.Combine(game.GameRoot, patch.RelativePath),
            patch.PreviousValue is null ? $"Remove {patch.Key} from {Path.GetFileName(patch.RelativePath)}" : $"Restore the previous {patch.Key}",
            patch.CreatedFile ? "delete-if-empty" : null, patch.Section, patch.Key, patch.PreviousValue);

    private static IniDocument? ReadIni(string path)
    {
        try
        {
            return File.Exists(path) ? IniDocument.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException or KeyNotFoundException ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
}

public sealed record EnhancementUpdate(EnhancementKind Kind, string Name, string InstalledVersion, string AvailableVersion);
