using RhiLinux.Core;

namespace RhiLinux.Mods;

public sealed record OptiScalerSettings(
    string Dx11Upscaler,
    string Dx12Upscaler,
    string VulkanUpscaler,
    string DlssInputs,
    string FrameGeneration,
    string FrameGenerationInput,
    string FrameGenerationOutput)
{
    public static OptiScalerSettings FromDocument(IniDocument document) => new(
        document.Get("Upscalers", "Dx11Upscaler") ?? "auto",
        document.Get("Upscalers", "Dx12Upscaler") ?? "auto",
        document.Get("Upscalers", "VulkanUpscaler") ?? "auto",
        document.Get("Inputs", "EnableDlssInputs") ?? "auto",
        document.Get("FrameGen", "Enabled") ?? "auto",
        document.Get("FrameGen", "FGInput") ?? "auto",
        document.Get("FrameGen", "FGOutput") ?? "auto");

    public IReadOnlyList<(string Section, string Key, string Value)> Values =>
    [
        ("Upscalers", "Dx11Upscaler", Dx11Upscaler),
        ("Upscalers", "Dx12Upscaler", Dx12Upscaler),
        ("Upscalers", "VulkanUpscaler", VulkanUpscaler),
        ("Inputs", "EnableDlssInputs", DlssInputs),
        ("FrameGen", "Enabled", FrameGeneration),
        ("FrameGen", "FGInput", FrameGenerationInput),
        ("FrameGen", "FGOutput", FrameGenerationOutput)
    ];
}

public static class OptiScalerSettingsService
{
    public static async Task<OptiScalerSettings> ReadAsync(DeploymentTarget game, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(game.DeploymentDirectory, "OptiScaler.ini");
        var document = IniDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        ValidateDocument(document);
        return OptiScalerSettings.FromDocument(document);
    }

    public static void ValidateDocument(IniDocument document)
    {
        var keys = new OptiScalerSettings("auto", "auto", "auto", "auto", "auto", "auto", "auto").Values;
        if (document.HasFatalIssues || !document.CanSafelyEditManagedKeys(keys.Select(key => (key.Section, key.Key))))
            throw new InvalidDataException("OptiScaler.ini is malformed. Repair the installation before changing settings.");
        if (keys.Any(key => document.Get(key.Section, key.Key) is null))
            throw new NotSupportedException("This OptiScaler release does not expose the supported upscaling and frame generation settings. Update OptiScaler first.");
    }

    public static void ValidateChange(string key, string value)
    {
        string[] supported = key switch
        {
            "Dx11Upscaler" => ["auto", "fsr22", "fsr31", "xess_12"],
            "Dx12Upscaler" or "VulkanUpscaler" => ["auto", "fsr22", "fsr31", "xess"],
            "EnableDlssInputs" or "Enabled" => ["auto", "true", "false"],
            "FGInput" => ["auto", "nofg", "upscaler", "fsrfg", "fsrfg30"],
            "FGOutput" => ["auto", "nofg", "fsrfg"],
            _ => []
        };
        if (!supported.Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"The {key} value '{value}' is not a supported AMD setting.");
    }

    public static void ValidateDependencies(DeploymentTarget game, OptiScalerSettings settings)
    {
        settings = settings with
        {
            Dx11Upscaler = settings.Dx11Upscaler.ToLowerInvariant(),
            Dx12Upscaler = settings.Dx12Upscaler.ToLowerInvariant(),
            VulkanUpscaler = settings.VulkanUpscaler.ToLowerInvariant(),
            FrameGeneration = settings.FrameGeneration.ToLowerInvariant(),
            FrameGenerationInput = settings.FrameGenerationInput.ToLowerInvariant(),
            FrameGenerationOutput = settings.FrameGenerationOutput.ToLowerInvariant()
        };
        if (settings.Dx11Upscaler == "xess_12" || settings.Dx12Upscaler == "xess" || settings.VulkanUpscaler == "xess")
            RequireRuntime(game, "libxess.dll");
        if (settings.FrameGeneration == "true")
        {
            ValidateChange("FGInput", settings.FrameGenerationInput);
            ValidateChange("FGOutput", settings.FrameGenerationOutput);
            if (settings.FrameGenerationInput is "nofg" || settings.FrameGenerationOutput is "nofg")
                throw new InvalidOperationException("Enabled frame generation requires an active input and output.");
            if (settings.FrameGenerationOutput == "auto")
                throw new InvalidOperationException("Select FSR 3 as the frame generation output before enabling it.");
            if (settings.FrameGenerationInput == "auto")
                throw new InvalidOperationException("Select the frame generation input before enabling it.");
        }
        if (settings.FrameGenerationOutput == "fsrfg")
        {
            if (FindRuntime(game, "amd_fidelityfx_dx12.dll") is { } monolithic)
                ArtifactValidator.ValidatePe(monolithic, PeArchitecture.X64);
            else
            {
                RequireRuntime(game, "amd_fidelityfx_loader_dx12.dll");
                RequireRuntime(game, "amd_fidelityfx_framegeneration_dx12.dll");
            }
        }
    }

    private static void RequireRuntime(DeploymentTarget game, string name)
    {
        var path = FindRuntime(game, name) ?? throw new InvalidOperationException(
            $"This mode requires {name} next to the game executable. Update or repair the official OptiScaler bundle first.");
        ArtifactValidator.ValidatePe(path, PeArchitecture.X64);
    }

    private static string? FindRuntime(DeploymentTarget game, string name) =>
        Directory.EnumerateFiles(game.DeploymentDirectory).FirstOrDefault(path =>
            Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
}
