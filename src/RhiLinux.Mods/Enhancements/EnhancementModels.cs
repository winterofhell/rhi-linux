using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum EnhancementKind { ShaderPacks, ReLimiter, DisplayCommander, ReFramework, OptiPatcher }

public enum EnhancementPayload { DirectFile, ZipMember }

public sealed record EnhancementDefinition(
    EnhancementKind Kind,
    string Name,
    string Summary,
    string Repository,
    string? Tag,
    string AssetName64,
    string? AssetName32,
    EnhancementPayload Payload,
    string? PayloadMember,
    string TargetName64,
    string? TargetName32,
    string? TargetSubdirectory,
    IReadOnlyList<string> LegacyFileNames,
    EnhancementKind? ExclusiveWith,
    string? DllOverride,
    bool RequiresPeValidation)
{
    public Uri ProjectUrl => new($"https://github.com/{Repository}");
    public bool Supports32Bit => AssetName32 is not null && TargetName32 is not null;
}

public static class EnhancementCatalog
{
    public static EnhancementDefinition ReLimiter { get; } = new(
        EnhancementKind.ReLimiter,
        "ReLimiter",
        "Low-latency frame limiter add-on for ReShade",
        "RankFTW/ReLimiter",
        null,
        "relimiter.addon64",
        "relimiter.addon32",
        EnhancementPayload.DirectFile,
        null,
        "relimiter.addon64",
        "relimiter.addon32",
        null,
        ["ultra_limiter.addon64", "ultra_limiter.addon32"],
        EnhancementKind.DisplayCommander,
        null,
        true);

    public static EnhancementDefinition DisplayCommander { get; } = new(
        EnhancementKind.DisplayCommander,
        "Display Commander",
        "Frame pacing, window and display control add-on for ReShade",
        "pmnoxx/display-commander",
        "latest",
        "zzz_display_commander.addon64",
        "zzz_display_commander.addon32",
        EnhancementPayload.DirectFile,
        null,
        "zzz_display_commander.addon64",
        "zzz_display_commander.addon32",
        null,
        ["zzz_display_commander_lite.addon64", "zzz_display_commander_lite.addon32"],
        EnhancementKind.ReLimiter,
        null,
        true);

    public static EnhancementDefinition ReFramework { get; } = new(
        EnhancementKind.ReFramework,
        "REFramework",
        "Scripting, camera and graphics framework for RE Engine games",
        "praydog/REFramework-nightly",
        null,
        "REFramework.zip",
        null,
        EnhancementPayload.ZipMember,
        "dinput8.dll",
        "dinput8.dll",
        null,
        null,
        [],
        null,
        "dinput8",
        true);

    public static EnhancementDefinition OptiPatcher { get; } = new(
        EnhancementKind.OptiPatcher,
        "OptiPatcher",
        "ASI plugin that unlocks upscaler inputs for OptiScaler on AMD GPUs",
        "optiscaler/OptiPatcher",
        "rolling",
        "OptiPatcher.asi",
        null,
        EnhancementPayload.DirectFile,
        null,
        "OptiPatcher.asi",
        null,
        "plugins",
        [],
        null,
        null,
        true);

    public static IReadOnlyList<EnhancementDefinition> Components { get; } =
        [ReLimiter, DisplayCommander, ReFramework, OptiPatcher];

    public static EnhancementDefinition Get(EnhancementKind kind) =>
        Components.FirstOrDefault(definition => definition.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "This enhancement is not a file component.");
}

public sealed record EnhancementFileRecord(
    EnhancementKind Kind,
    string RelativePath,
    string Sha256,
    string Version,
    string SourceUrl,
    DateTimeOffset InstalledAt);

public sealed record InstalledShaderPack(string Id, string Version);

public sealed record ShaderTreeRecord(
    string RelativePath,
    IReadOnlyList<InstalledShaderPack> Packs,
    string? PreservedOriginalRelativePath,
    DateTimeOffset InstalledAt);

public sealed record IniPatchRecord(
    string RelativePath,
    string Section,
    string Key,
    string Value,
    string? PreviousValue,
    bool CreatedFile);

public sealed class EnhancementState
{
    public int SchemaVersion { get; set; } = 1;
    public string? InstallId { get; set; }
    public List<EnhancementFileRecord> Files { get; set; } = [];
    public ShaderTreeRecord? Shaders { get; set; }
    public List<IniPatchRecord> IniPatches { get; set; } = [];

    public EnhancementState Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        InstallId = InstallId,
        Files = [.. Files],
        Shaders = Shaders,
        IniPatches = [.. IniPatches]
    };

    public EnhancementFileRecord? Find(EnhancementKind kind) =>
        Files.FirstOrDefault(file => file.Kind == kind);
}

public enum EnhancementStepType
{
    PlaceFile,
    RemoveFile,
    PreserveOriginalShaders,
    InstallShaderTree,
    RemoveShaderTree,
    RestoreOriginalShaders,
    SetIniValue,
    RestoreIniValue
}

public sealed record EnhancementStep(
    EnhancementStepType Type,
    EnhancementKind Kind,
    string Target,
    string Description,
    string? Source = null,
    string? Section = null,
    string? Key = null,
    string? Value = null,
    string? ExpectedSha256 = null);

public sealed record EnhancementDownload(string Name, string Version, Uri Source, long? Size, bool Cached);

public sealed class EnhancementPlan
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DeploymentTarget Game { get; init; }
    public required EnhancementState ResultingState { get; init; }
    public List<EnhancementStep> Steps { get; } = [];
    public List<EnhancementDownload> Downloads { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Blockers { get; } = [];
    public string? StagingDirectory { get; init; }
    public bool IsEmpty => Steps.Count == 0;
    public bool CanExecute => Blockers.Count == 0 && Steps.Count > 0;
}

public sealed record EnhancementExecutionResult(bool Succeeded, bool DryRun, IReadOnlyList<string> Log, string? Error);

public sealed record EnhancementComponentStatus(
    EnhancementKind Kind,
    string Name,
    string Summary,
    bool Applicable,
    string? NotApplicableReason,
    bool Installed,
    bool Managed,
    string? Version,
    string? Conflict);

public sealed record EnhancementSnapshot(
    IReadOnlyList<EnhancementComponentStatus> Components,
    IReadOnlyList<InstalledShaderPack> ShaderPacks,
    bool ShaderTreeManaged,
    bool ForeignShaderTree,
    IReadOnlyList<string> AdditionalDllOverrides,
    bool RecoveryPending);
