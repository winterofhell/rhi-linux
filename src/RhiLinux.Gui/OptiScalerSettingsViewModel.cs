using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed record SettingChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

public sealed class OptiScalerSettingsViewModel
{
    public OptiScalerSettingsViewModel(OptiScalerSettings settings)
    {
        Dx11Choices = WithCurrent(Upscalers("xess_12"), settings.Dx11Upscaler);
        Dx12Choices = WithCurrent(Upscalers("xess"), settings.Dx12Upscaler);
        VulkanChoices = WithCurrent(Upscalers("xess"), settings.VulkanUpscaler);
        DlssChoices = WithCurrent(Switches(), settings.DlssInputs);
        FrameGenerationChoices = WithCurrent(Switches(), settings.FrameGeneration);
        InputChoices = WithCurrent(
        [
            new("Automatic", "auto"), new("Disabled", "nofg"),
            new("Upscaler motion vectors", "upscaler"), new("Native FSR 3.1", "fsrfg"), new("Native FSR 3.0", "fsrfg30")
        ], settings.FrameGenerationInput);
        OutputChoices = WithCurrent([new("Automatic", "auto"), new("Disabled", "nofg"), new("FSR 3", "fsrfg")], settings.FrameGenerationOutput);
        Dx11 = Select(Dx11Choices, settings.Dx11Upscaler);
        Dx12 = Select(Dx12Choices, settings.Dx12Upscaler);
        Vulkan = Select(VulkanChoices, settings.VulkanUpscaler);
        Dlss = Select(DlssChoices, settings.DlssInputs);
        FrameGeneration = Select(FrameGenerationChoices, settings.FrameGeneration);
        Input = Select(InputChoices, settings.FrameGenerationInput);
        Output = Select(OutputChoices, settings.FrameGenerationOutput);
    }

    public IReadOnlyList<SettingChoice> Dx11Choices { get; }
    public IReadOnlyList<SettingChoice> Dx12Choices { get; }
    public IReadOnlyList<SettingChoice> VulkanChoices { get; }
    public IReadOnlyList<SettingChoice> DlssChoices { get; }
    public IReadOnlyList<SettingChoice> FrameGenerationChoices { get; }
    public IReadOnlyList<SettingChoice> InputChoices { get; }
    public IReadOnlyList<SettingChoice> OutputChoices { get; }
    public SettingChoice Dx11 { get; set; }
    public SettingChoice Dx12 { get; set; }
    public SettingChoice Vulkan { get; set; }
    public SettingChoice Dlss { get; set; }
    public SettingChoice FrameGeneration { get; set; }
    public SettingChoice Input { get; set; }
    public SettingChoice Output { get; set; }
    public OptiScalerSettings ToSettings() => new(
        Dx11.Value, Dx12.Value, Vulkan.Value, Dlss.Value, FrameGeneration.Value, Input.Value, Output.Value);

    private static SettingChoice[] Upscalers(string xess) =>
    [
        new("Automatic", "auto"), new("AMD FSR 2.2", "fsr22"), new("AMD FSR 3.1", "fsr31"), new("Intel XeSS", xess)
    ];

    private static SettingChoice[] Switches() =>
        [new("Automatic", "auto"), new("Enabled", "true"), new("Disabled", "false")];

    private static IReadOnlyList<SettingChoice> WithCurrent(IReadOnlyList<SettingChoice> choices, string current) =>
        choices.Any(choice => choice.Value.Equals(current, StringComparison.OrdinalIgnoreCase))
            ? choices
            : choices.Append(new SettingChoice($"Keep existing ({current})", current)).ToArray();

    private static SettingChoice Select(IReadOnlyList<SettingChoice> choices, string value) =>
        choices.First(choice => choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
}
