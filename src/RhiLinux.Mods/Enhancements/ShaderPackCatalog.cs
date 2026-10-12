namespace RhiLinux.Mods;

public enum ShaderPackCategory { Essential, Recommended, Extra }

public enum ShaderPackSourceKind { GitHubRelease, BranchArchive }

public sealed record ShaderPackDefinition(
    string Id,
    string Name,
    string Description,
    ShaderPackCategory Category,
    ShaderPackSourceKind SourceKind,
    string Repository,
    string Reference,
    IReadOnlyList<string> Requires)
{
    public Uri ProjectUrl => new($"https://github.com/{Repository}");

    public Uri ArchiveUrl => SourceKind == ShaderPackSourceKind.BranchArchive
        ? new($"https://github.com/{Repository}/archive/refs/heads/{Reference}.zip")
        : new($"https://api.github.com/repos/{Repository}/releases/latest");
}

public static class ShaderPackCatalog
{
    public static IReadOnlyList<ShaderPackDefinition> All { get; } =
    [
        Release("Lilium", "Lilium HDR Shaders", "HDR analysis, tone mapping and inverse tone mapping", ShaderPackCategory.Essential, "EndlesslyFlowering/ReShade_HDR_shaders", ".7z"),
        Branch("CrosireMaster", "ReShade standard effects", "Official ReShade effects from the master branch", ShaderPackCategory.Recommended, "crosire/reshade-shaders", "master"),
        Release("PumboAutoHDR", "Pumbo AutoHDR", "Automatic HDR conversion for SDR games", ShaderPackCategory.Recommended, "Filoppi/PumboAutoHDR", ".zip"),
        Branch("MaxG2DSimpleHDR", "Simple HDR Shaders", "HDR bloom, lens flare and tone mapping", ShaderPackCategory.Recommended, "MaxG2D/ReshadeSimpleHDRShaders", "main"),
        Branch("ClshortfuseShaders", "clshortfuse shaders", "HDR and color correction shaders for RenoDX", ShaderPackCategory.Recommended, "clshortfuse/reshade-shaders", "main"),
        Branch("RenoFXHDRToolkit", "RenoFX HDR Toolkit", "SDR to HDR conversion and grading for games without a RenoDX mod", ShaderPackCategory.Recommended, "clshortfuse/renofx", "main"),
        Branch("CrosireLegacy", "ReShade legacy effects", "Older effects removed from the master branch", ShaderPackCategory.Extra, "crosire/reshade-shaders", "legacy"),
        Branch("SmolbbsoopShaders", "smolbbsoop shaders", "HDR utility shaders and effects", ShaderPackCategory.Extra, "smolbbsoop/smolbbsoopshaders", "main"),
        Branch("PotatoFX", "potatoFX", "Lightweight post-processing for low-end hardware", ShaderPackCategory.Extra, "CreepySasquatch/potatoFX", "main"),
        Branch("Azen", "Azen", "Experimental effects collection by Zenteon", ShaderPackCategory.Extra, "Zenteon/Azen", "main", "SmolbbsoopShaders"),
        Branch("SweetFX", "SweetFX", "Classic color grading, sharpening and bloom", ShaderPackCategory.Extra, "CeeJayDK/SweetFX", "master"),
        Branch("OtisFX", "OtisFX", "Depth of field, light rays and camera effects", ShaderPackCategory.Extra, "FransBouma/OtisFX", "master"),
        Branch("Depth3D", "Depth3D", "Stereoscopic 3D and depth-based effects", ShaderPackCategory.Extra, "BlueSkyDefender/Depth3D", "master"),
        Branch("DaodanShaders", "Daodan shaders", "Comic, crosshatch and artistic styles", ShaderPackCategory.Extra, "Daodan317081/reshade-shaders", "master"),
        Branch("BrussellShaders", "brussell shaders", "Halftone, sketch and stylized rendering", ShaderPackCategory.Extra, "brussell1/Shaders", "master"),
        Branch("FubaxShaders", "fubax shaders", "Lens distortion and chromatic aberration", ShaderPackCategory.Extra, "Fubaxiusz/fubax-shaders", "master"),
        Branch("qUINT", "qUINT", "MXAO, ADOF, Lightroom and screen-space reflections", ShaderPackCategory.Extra, "martymcmodding/qUINT", "master"),
        Branch("AlucardDH", "dh shaders", "Ambient occlusion, undither and color enhancement", ShaderPackCategory.Extra, "AlucardDH/dh-reshade-shaders", "master"),
        Branch("WarpFX", "Warp-FX", "Screen warp, swirl and distortion", ShaderPackCategory.Extra, "Radegast-FFXIV/Warp-FX", "master"),
        Branch("Prod80", "prod80 color effects", "Color grading, curves and tone tools", ShaderPackCategory.Extra, "prod80/prod80-ReShade-Repository", "master"),
        Branch("CorgiFX", "CorgiFX", "Screenshot and virtual photography tools", ShaderPackCategory.Extra, "originalnicodr/CorgiFX", "master"),
        Branch("InsaneShaders", "Insane Shaders", "Dithering, fog removal and edge detection", ShaderPackCategory.Extra, "LordOfLunacy/Insane-Shaders", "master"),
        Branch("CobraFX", "CobraFX", "Gravity, auto focus and ray traced effects", ShaderPackCategory.Extra, "LordKobra/CobraFX", "master"),
        Branch("AstrayFX", "AstrayFX", "Depth-based fog, haze and atmosphere", ShaderPackCategory.Extra, "BlueSkyDefender/AstrayFX", "master"),
        Branch("CRTRoyale", "CRT-Royale", "CRT simulation with phosphor and scanline emulation", ShaderPackCategory.Extra, "akgunter/crt-royale-reshade", "master"),
        Branch("RSRetroArch", "RSRetroArch", "RetroArch ports: CRT, LCD and retro filters", ShaderPackCategory.Extra, "Matsilagi/RSRetroArch", "main"),
        Branch("VRToolkit", "VRToolkit", "Sharpening and clarity tuned for VR headsets", ShaderPackCategory.Extra, "retroluxfilm/reshade-vrtoolkit", "main"),
        Branch("FGFX", "FGFX", "Film grain, multi-LUT and cinematic processing", ShaderPackCategory.Extra, "AlexTuduran/FGFX", "main"),
        Branch("CShade", "CShade", "Optical flow, motion blur and convolution", ShaderPackCategory.Extra, "papadanku/CShade", "main"),
        Branch("iMMERSE", "iMMERSE", "RTGI, MXAO and anti-aliasing suite", ShaderPackCategory.Extra, "martymcmodding/iMMERSE", "main"),
        Branch("VortShaders", "vort shaders", "Sharpening, color correction and depth effects", ShaderPackCategory.Extra, "vortigern11/vort_Shaders", "main"),
        Branch("BXShade", "BX-Shade", "Bloom, exposure and color enhancement", ShaderPackCategory.Extra, "liuxd17thu/BX-Shade", "main"),
        Branch("SHADERDECK", "SHADERDECK", "Curated color and lighting effects", ShaderPackCategory.Extra, "IAmTreyM/SHADERDECK", "main"),
        Branch("METEOR", "METEOR", "Denoiser and image reconstruction", ShaderPackCategory.Extra, "martymcmodding/METEOR", "main"),
        Branch("AnnReShade", "Ann-ReShade", "Soft bloom, grading and ambient light", ShaderPackCategory.Extra, "AnastasiaGals/Ann-ReShade", "main"),
        Branch("ZenteonFX", "ZenteonFX", "Global illumination, SSR and path tracing", ShaderPackCategory.Extra, "Zenteon/ZenteonFX", "main"),
        Branch("GShadeShaders", "GShade shaders", "Large community collection from GShade", ShaderPackCategory.Extra, "Mortalitas/GShade-Shaders", "master"),
        Branch("PthoFX", "Ptho-FX", "Cinematic grading and film emulation", ShaderPackCategory.Extra, "PthoEastCoast/Ptho-FX", "main"),
        Branch("Anagrama", "Anagrama Collection", "Artistic and experimental effects", ShaderPackCategory.Extra, "nullfrctl/reshade-shaders", "main"),
        Branch("BarbatosShaders", "Barbatos shaders", "Ambient occlusion, bloom and color", ShaderPackCategory.Extra, "BarbatosBachiko/Reshade-Shaders", "main"),
        Branch("BFBFX", "BFBFX", "Stylized post-processing effects", ShaderPackCategory.Extra, "yplebedev/BFBFX", "main"),
        Branch("Rendepth", "Rendepth", "Depth-based 3D and stereo rendering", ShaderPackCategory.Extra, "outmode/rendepth-reshade", "main"),
        Branch("CropAndResize", "Crop and Resize", "Cropping, scaling and aspect ratio tools", ShaderPackCategory.Extra, "P0NYSLAYSTATION/Scaling-Shaders", "main"),
        Branch("FXShaders", "FXShaders", "Bloom, grain, dithering and utilities", ShaderPackCategory.Extra, "luluco250/FXShaders", "master"),
        Branch("LumeniteFX", "LumeniteFX", "Lighting, bloom and atmospheric glow", ShaderPackCategory.Extra, "umar-afzaal/LumeniteFX", "mainline"),
        Branch("NNShaders", "NN-Shaders", "Neural network image processing", ShaderPackCategory.Extra, "Sarenya/NN-Shaders", "master"),
        Branch("QdOledAplFixer", "QD-OLED APL Fixer", "HDR brightness boost against QD-OLED ABL dimming", ShaderPackCategory.Extra, "mspeedo/QD-OLED-APL-FIXER", "main"),
        Branch("GlamaryeFX", "Glamarye Fast Effects", "Single-pass sharpening, AO, lighting and color", ShaderPackCategory.Extra, "rj200/Glamarye_Fast_Effects_for_ReShade", "main"),
        Branch("LumaBoost", "LumaBoost", "Lifts midtones to counter OLED brightness limiting", ShaderPackCategory.Extra, "Valadore/LumaBoost", "main")
    ];

    public static IReadOnlyList<string> RecommendedSelection { get; } =
        All.Where(pack => pack.Category is ShaderPackCategory.Essential or ShaderPackCategory.Recommended)
            .Select(pack => pack.Id)
            .ToArray();

    public static ShaderPackDefinition? Find(string id) =>
        All.FirstOrDefault(pack => pack.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ShaderPackDefinition> ResolveWithDependencies(IEnumerable<string> ids)
    {
        var ordered = new List<ShaderPackDefinition>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids) Visit(id);
        return ordered;

        void Visit(string id)
        {
            if (!visited.Add(id)) return;
            var pack = Find(id) ?? throw new InvalidDataException($"Unknown shader pack '{id}'.");
            foreach (var dependency in pack.Requires) Visit(dependency);
            ordered.Add(pack);
        }
    }

    private static ShaderPackDefinition Branch(
        string id, string name, string description, ShaderPackCategory category,
        string repository, string branch, params string[] requires) =>
        new(id, name, description, category, ShaderPackSourceKind.BranchArchive, repository, branch, requires);

    private static ShaderPackDefinition Release(
        string id, string name, string description, ShaderPackCategory category,
        string repository, string assetExtension, params string[] requires) =>
        new(id, name, description, category, ShaderPackSourceKind.GitHubRelease, repository, assetExtension, requires);
}
