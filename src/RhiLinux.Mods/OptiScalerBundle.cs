using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record OptiScalerBundleFile(
    string RelativePath,
    string Sha256,
    long Size,
    bool RuntimeRequired,
    string Role,
    DeploymentFileRequirement Requirement = DeploymentFileRequirement.UnrelatedArchiveContent,
    string? RequirementReason = null,
    string? Feature = null,
    bool CanOmitOnCollision = false);

public sealed record OptiScalerBundleManifest(
    int SchemaVersion,
    string ReleaseVersion,
    string ReleaseTag,
    string ArchiveAssetName,
    string ArchiveSha256,
    IReadOnlyList<string> ContainedPaths,
    IReadOnlyList<OptiScalerBundleFile> Files,
    string DefaultIniPath,
    IReadOnlyList<string> SupportedProxyNames,
    IReadOnlyList<string> SetupScriptDecisions,
    IReadOnlyList<string> BundledAuxiliaryComponents,
    IReadOnlyList<string> ObsoleteManagedPaths)
{
    public const int CurrentSchemaVersion = 1;
    public IReadOnlyList<OptiScalerBundleFile> RuntimeFiles => Files.Where(x => x.RuntimeRequired).ToArray();
}

public static partial class OptiScalerBundleParser
{
    private static readonly string[] Recognized09RuntimeFiles =
    [
        "OptiScaler.dll",
        "OptiScaler.ini",
        "fakenvapi.dll",
        "fakenvapi.ini",
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_framegeneration_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_vk.dll"
    ];

    private static readonly string[] KnownObsoletePaths =
    [
        "nvapi64.dll",
        "nvngx.dll",
        "OptiScaler.asi",
        "Remove OptiScaler.bat",
        "Remove_OptiScaler.bat",
        "remove_optiscaler.sh"
    ];

    public static async Task<OptiScalerBundleManifest> ParseAsync(
        string extractedRoot,
        string releaseVersion,
        string releaseTag,
        string archiveAssetName,
        string archiveSha256,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(extractedRoot)) throw new DirectoryNotFoundException(extractedRoot);
        var paths = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(extractedRoot, x).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        ValidatePaths(paths);
        if (!paths.Contains("OptiScaler.dll", StringComparer.OrdinalIgnoreCase) ||
            !paths.Contains("OptiScaler.ini", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The stable bundle must contain top-level OptiScaler.dll and OptiScaler.ini files.");

        var linuxScript = paths.FirstOrDefault(x => x.Equals("setup_linux.sh", StringComparison.OrdinalIgnoreCase));
        var windowsScript = paths.FirstOrDefault(x => x.Equals("setup_windows.bat", StringComparison.OrdinalIgnoreCase));
        if (linuxScript is null && windowsScript is null)
            throw new InvalidDataException("The bundle has no recognized official setup script; its format is unsupported.");

        var scriptText = string.Join('\n', new[] { linuxScript, windowsScript }.Where(x => x is not null)
            .Select(x => File.ReadAllText(Path.Combine(extractedRoot, x!.Replace('/', Path.DirectorySeparatorChar)))));
        var proxies = SupportedProxyRegex().Matches(scriptText).Select(x => x.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (DeploymentPlanner.SupportedProxyNames.Except(proxies, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The setup scripts do not advertise the recognized stable proxy filename set.");

        if (Is09OrLater(releaseVersion))
        {
            var missing = Recognized09RuntimeFiles.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"The OptiScaler 0.9+ bundle is missing recognized release files: {string.Join(", ", missing)}");
        }

        var files = new List<OptiScalerBundleFile>();
        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(extractedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var classification = Classify(relativePath);
            files.Add(new(relativePath, await HashAsync(fullPath, cancellationToken), new FileInfo(fullPath).Length,
                classification.Runtime, classification.Role, classification.Requirement, classification.Reason,
                classification.Feature, classification.CanOmitOnCollision));
        }

        var decisions = new List<string>
        {
            "Copy runtime DLL and INI files while preserving their bundle-relative paths.",
            "Rename only OptiScaler.dll to the selected supported proxy filename.",
            "Keep OptiScaler.ini at the deployment root.",
            "For AMD Proton, retain the official Dxgi=auto default unless a reviewed profile overrides it.",
            "Do not copy or execute setup scripts, uninstallers, readme files, or licenses."
        };
        if (scriptText.Contains("Dxgi=false", StringComparison.OrdinalIgnoreCase))
            decisions.Add("The official scripts disable Dxgi spoofing only when DLSS inputs are explicitly declined.");

        var auxiliary = files.Where(x => x.RuntimeRequired && x.Role is not "OptiScaler runtime" and not "Default configuration")
            .Select(x => x.Role).Distinct(StringComparer.Ordinal).ToArray();
        return new(OptiScalerBundleManifest.CurrentSchemaVersion, releaseVersion, releaseTag, archiveAssetName, archiveSha256,
            paths, files, "OptiScaler.ini", proxies, decisions, auxiliary, KnownObsoletePaths);
    }

    public static async Task WriteAsync(string path, OptiScalerBundleManifest manifest, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary,
                JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<OptiScalerBundleManifest> ReadAsync(string path, CancellationToken token = default)
    {
        await using var input = File.OpenRead(path);
        var result = await JsonSerializer.DeserializeAsync<OptiScalerBundleManifest>(input,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), token)
            ?? throw new InvalidDataException("OptiScaler bundle manifest is empty.");
        if (result.SchemaVersion != OptiScalerBundleManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported OptiScaler bundle manifest schema {result.SchemaVersion}.");
        ValidatePaths(result.Files.Select(file => file.RelativePath));
        var normalizedFiles = result.Files.Select(file =>
        {
            var classification = Classify(file.RelativePath);
            return file with
            {
                RuntimeRequired = classification.Runtime,
                Role = classification.Role,
                Requirement = classification.Requirement,
                RequirementReason = classification.Reason,
                Feature = classification.Feature,
                CanOmitOnCollision = classification.CanOmitOnCollision
            };
        }).ToArray();
        return result with { Files = normalizedFiles };
    }

    private static BundleFileClassification Classify(string relativePath)
    {
        if (relativePath.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase))
            return new(true, "OptiScaler runtime", DeploymentFileRequirement.Required,
                "The selected loading strategy requires the signed OptiScaler runtime.", "core", false);
        if (relativePath.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
            return new(true, "Default configuration", DeploymentFileRequirement.Required,
                "The installed release requires a parseable version-matched configuration file.", "configuration", false);
        if (relativePath.Equals("fakenvapi.dll", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals("fakenvapi.ini", StringComparison.OrdinalIgnoreCase))
            return new(true, "Bundled FakeNVAPI", DeploymentFileRequirement.Conditional,
                "Used on AMD when NVIDIA API spoofing is needed for DLSS inputs; OptiScaler can run without this feature.",
                "amd-dlss-inputs", true);
        if (relativePath.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase) &&
            relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new(true, "Bundled AMD FidelityFX runtime", DeploymentFileRequirement.Conditional,
                "Provides the release-matched FidelityFX feature backend; a game-supplied runtime is preserved and the feature copy can be omitted.",
                relativePath.Contains("_vk", StringComparison.OrdinalIgnoreCase) ? "fidelityfx-vulkan" : "fidelityfx-dx12", true);
        if (relativePath.Equals("dlssg_to_fsr3_amd_is_better.dll", StringComparison.OrdinalIgnoreCase))
            return new(true, "Bundled DLSSG-to-FSR3 runtime", DeploymentFileRequirement.Optional,
                "Enables an optional frame-generation path and is not required for core OptiScaler loading.",
                "dlssg-to-fsr3", true);
        if (relativePath.Equals("libxess_dx11.dll", StringComparison.OrdinalIgnoreCase))
            return new(true, "Bundled XeSS DX11 runtime", DeploymentFileRequirement.Conditional,
                "Provides XeSS for DirectX 11 games and can be omitted when that feature is not applicable.",
                "xess-dx11", true);
        if (relativePath.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals("libxess_fg.dll", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals("libxell.dll", StringComparison.OrdinalIgnoreCase))
            return new(true, "Bundled XeSS runtime", DeploymentFileRequirement.Optional,
                "Provides optional XeSS upscaling, frame generation, or latency features.", "xess", true);
        if (relativePath.StartsWith("D3D12_Optiscaler/", StringComparison.OrdinalIgnoreCase) &&
            relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new(true, "Bundled D3D12 runtime", DeploymentFileRequirement.Conditional,
                "Required only when the FsrAgilitySDKUpgrade feature is enabled.", "d3d12-agility-upgrade", true);
        if (relativePath.StartsWith("Licenses/", StringComparison.OrdinalIgnoreCase))
            return Unrelated(relativePath, "License material is retained in the cache and is not deployed into a game.");
        if (relativePath.Equals("setup_linux.sh", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals("setup_windows.bat", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return Unrelated(relativePath, "Setup scripts, uninstallers, and documentation are inspected but never copied or executed.");
        return Unrelated(relativePath, "The file is not part of a recognized stable runtime layout and is excluded from deployment.");
    }

    private static string Role(string path) => path switch
    {
        var x when x.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) => "OptiScaler runtime",
        var x when x.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) => "Default configuration",
        var x when x.Contains("fakenvapi", StringComparison.OrdinalIgnoreCase) => "Bundled FakeNVAPI",
        var x when x.Contains("fidelityfx", StringComparison.OrdinalIgnoreCase) => "Bundled AMD FidelityFX runtime",
        var x when x.Contains("xess", StringComparison.OrdinalIgnoreCase) || x.Contains("xell", StringComparison.OrdinalIgnoreCase) => "Bundled XeSS runtime",
        var x when x.Contains("dlssg_to_fsr3", StringComparison.OrdinalIgnoreCase) => "Bundled DLSSG-to-FSR3 runtime",
        var x when x.StartsWith("D3D12_Optiscaler/", StringComparison.OrdinalIgnoreCase) => "Bundled D3D12 runtime",
        _ => "Non-runtime bundle content"
    };

    private static BundleFileClassification Unrelated(string path, string reason) => new(false, Role(path),
        DeploymentFileRequirement.UnrelatedArchiveContent, reason, null, true);

    private static void ValidatePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Path.IsPathRooted(path) || path.Split('/').Contains("..", StringComparer.Ordinal))
                throw new InvalidDataException($"Bundle path escapes the extraction root: {path}");
        }
    }

    private static bool Is09OrLater(string version)
    {
        var parts = version.TrimStart('v', 'V').Split('.', '-', '+');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) &&
            (major > 0 || minor >= 9);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
    }

    [GeneratedRegex(@"(?i)(?:dxgi|winmm|version|dbghelp|d3d12|wininet|winhttp)\.dll")]
    private static partial Regex SupportedProxyRegex();

    private sealed record BundleFileClassification(
        bool Runtime,
        string Role,
        DeploymentFileRequirement Requirement,
        string Reason,
        string? Feature,
        bool CanOmitOnCollision);
}
