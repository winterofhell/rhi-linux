using System.Text.Json;
using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum GameProfileSupport { Supported, EngineFallback, Experimental, Unsupported }

public sealed record RenoDxSource(
    Uri Url,
    string FileName,
    string Version,
    PeArchitecture Architecture,
    bool IsGameSpecific);

public sealed record GameProfile(
    string Id,
    uint? SteamAppId,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    string? ExecutableRelativePath,
    string? DeploymentRelativePath,
    GameEngine Engine,
    string Renderer,
    IReadOnlyList<string> PreferredProxyNames,
    IReadOnlyList<string> RejectedProxyNames,
    IReadOnlyList<string> KnownGameOwnedDlls,
    RenoDxSource? RenoDx,
    GameProfileSupport RenoDxSupport,
    bool OptiScalerCompatible,
    IReadOnlyDictionary<string, string> RequiredIniValues,
    IReadOnlyList<string> KnownConflicts,
    IReadOnlyList<string> Warnings,
    string? ProtonNotes);

public sealed record GameProfileMatch(GameProfile Profile, string MatchReason, bool ExactAppId)
{
    public bool IsExact => Profile.RenoDxSupport == GameProfileSupport.Supported;
    public bool IsFallback => Profile.RenoDxSupport == GameProfileSupport.EngineFallback;
}

public sealed class GameProfileCatalog
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? cachePath;
    private IReadOnlyList<GameProfile>? cachedProfiles;

    public GameProfileCatalog(XdgPaths? paths = null)
    {
        if (paths is not null)
            cachePath = Path.Combine(paths.AppCacheDirectory, "metadata", "game-profiles", "catalog.json");
    }

    public async Task<IReadOnlyList<GameProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (cachedProfiles is not null) return cachedProfiles;
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                await using var input = File.OpenRead(cachePath);
                var document = await JsonSerializer.DeserializeAsync<GameProfileDocument>(input, JsonOptions, cancellationToken);
                if (document is not null) cachedProfiles = Validate(document).Profiles;
            }
            catch (JsonException) { }
            catch (InvalidDataException) { }
        }
        return cachedProfiles ?? BuiltInProfiles;
    }

    public async Task UpdateCacheAsync(Stream json, CancellationToken cancellationToken = default)
    {
        if (cachePath is null) throw new InvalidOperationException("A cache path is required to update the profile catalog.");
        var document = await JsonSerializer.DeserializeAsync<GameProfileDocument>(json, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The game profile catalog is empty.");
        document = Validate(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporary = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, cachePath, true);
            cachedProfiles = document.Profiles;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<GameProfileMatch> MatchAsync(SteamGame game, CancellationToken cancellationToken = default) =>
        MatchAsync(game.ToDeploymentTarget(), cancellationToken);

    public Task<GameProfileMatch> MatchAsync(InstalledGame game, CancellationToken cancellationToken = default) =>
        MatchAsync(game.ToDeploymentTarget(), cancellationToken);

    public async Task<GameProfileMatch> MatchAsync(DeploymentTarget game, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadAsync(cancellationToken);
        if (game.SteamAppId is { } steamAppId)
        {
            var exact = profiles.SingleOrDefault(x => x.SteamAppId == steamAppId);
            if (exact is not null) return EnsureEligible(game, exact, $"Exact Steam AppID {steamAppId}", true);
        }

        var executableName = game.Executable is null ? null : Path.GetFileNameWithoutExtension(game.Executable);
        var executableMatches = profiles.Where(x => x.SteamAppId is null && x.RenoDxSupport == GameProfileSupport.Supported &&
            x.ExecutableRelativePath is not null && executableName is not null &&
            Path.GetFileNameWithoutExtension(x.ExecutableRelativePath).Equals(executableName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (executableMatches.Length > 1)
            return new(UnsupportedProfile(game), "Multiple game profiles match the selected executable; automatic selection is unsafe", false);
        if (executableMatches.SingleOrDefault() is { } executable)
            return EnsureEligible(game, executable, $"Exact executable profile for '{Path.GetFileName(game.Executable)}'", false);

        var aliasMatches = profiles.Where(x => x.SteamAppId is null && x.RenoDxSupport == GameProfileSupport.Supported &&
            x.Aliases.Contains(game.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (aliasMatches.Length > 1)
            return new(UnsupportedProfile(game), "Multiple game profiles contain the same alias; automatic selection is unsafe", false);
        if (aliasMatches.SingleOrDefault() is { } alias)
            return EnsureEligible(game, alias, $"Known game alias profile for '{game.Name}'", false);

        var engine = profiles.SingleOrDefault(x => x.SteamAppId is null && x.Engine == game.Engine && x.RenoDxSupport == GameProfileSupport.EngineFallback);
        if (engine is not null)
        {
            var architecture = SelectedArchitecture(game);
            var source = engine.RenoDx;
            if (source is not null && engine.Engine == GameEngine.Unity && architecture == PeArchitecture.X86)
            {
                source = source with
                {
                    Url = new Uri("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon32"),
                    FileName = "renodx-unityengine.addon32",
                    Architecture = PeArchitecture.X86
                };
                engine = engine with { RenoDx = source };
            }
            return EnsureEligible(game, engine, $"Supported {game.Engine} engine fallback; no exact profile matched", false);
        }

        return new(UnsupportedProfile(game), "No exact AppID, alias, executable, or approved engine profile matched", false);
    }

    private static GameProfileMatch EnsureEligible(DeploymentTarget game, GameProfile profile, string reason, bool exactAppId)
    {
        if (game.Executable is null || !Path.GetExtension(game.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(UnsupportedProfile(game), "No Windows game executable was selected; native Linux games are not eligible", false);
        if (game.Confidence is DetectionConfidence.None or DetectionConfidence.Low)
            return new(UnsupportedProfile(game), "The game executable or deployment layout is ambiguous", false);
        if (game.RequiresConfirmation)
            return new(UnsupportedProfile(game), "Injection is blocked because anti-cheat files require an explicit compatibility decision", false);
        if (game.Engine == GameEngine.UnrealLegacy && profile.RenoDxSupport == GameProfileSupport.EngineFallback)
            return new(UnsupportedProfile(game), "Legacy Unreal Engine versions do not support the official generic RenoDX addon", false);
        var architecture = SelectedArchitecture(game);
        if (architecture is not PeArchitecture.X86 and not PeArchitecture.X64)
            return new(UnsupportedProfile(game), "The Windows game executable architecture could not be determined safely", false);
        if (profile.RenoDx is { } source && source.Architecture != architecture)
            return new(UnsupportedProfile(game), $"The mapped RenoDX addon is {source.Architecture}, but the selected game executable is {architecture}", false);
        return new(profile, reason, exactAppId);
    }

    private static PeArchitecture SelectedArchitecture(DeploymentTarget game)
    {
        var candidate = game.Candidates.FirstOrDefault(x => game.Executable is not null &&
            Path.GetFullPath(x.Path).Equals(Path.GetFullPath(game.Executable), StringComparison.Ordinal))
            ?? game.Candidates.FirstOrDefault();
        if (candidate is not null) return candidate.Architecture;
        if (game.Executable is null || !File.Exists(game.Executable)) return PeArchitecture.Unknown;
        try
        {
            using var stream = File.OpenRead(game.Executable);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return PeArchitecture.Unknown;
            stream.Position = 0x3c;
            var offset = reader.ReadUInt32();
            if (offset > stream.Length - 6) return PeArchitecture.Unknown;
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) return PeArchitecture.Unknown;
            return reader.ReadUInt16() switch { 0x014c => PeArchitecture.X86, 0x8664 => PeArchitecture.X64, _ => PeArchitecture.Unknown };
        }
        catch (IOException) { return PeArchitecture.Unknown; }
    }

    private static GameProfileDocument Validate(GameProfileDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException($"Unsupported game profile schema {document.SchemaVersion}.");
        if (document.Profiles.Count == 0) throw new InvalidDataException("The game profile catalog contains no profiles.");
        if (document.Profiles.Where(x => x.SteamAppId is not null).GroupBy(x => x.SteamAppId).Any(x => x.Count() > 1))
            throw new InvalidDataException("The game profile catalog contains duplicate Steam AppIDs.");
        foreach (var profile in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.CanonicalName))
                throw new InvalidDataException("Every game profile requires an ID and canonical name.");
            if (profile.PreferredProxyNames.Any(x => !DeploymentPlanner.SupportedProxyNames.Contains(x, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Profile '{profile.Id}' contains an unsupported proxy name.");
            if (!IsSafeRelativePath(profile.ExecutableRelativePath) || !IsSafeRelativePath(profile.DeploymentRelativePath))
                throw new InvalidDataException($"Profile '{profile.Id}' contains a path outside the game root.");
            if (profile.RenoDx is { } source && (!source.Url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(source.FileName) != source.FileName ||
                (!Path.GetExtension(source.FileName).Equals(".addon64", StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetExtension(source.FileName).Equals(".addon32", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException($"Profile '{profile.Id}' contains an invalid RenoDX source.");
        }
        return document;
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (path is null) return true;
        var normalized = path.Replace('\\', '/');
        return !Path.IsPathRooted(path) && !normalized.Split('/').Contains("..", StringComparer.Ordinal);
    }

    private static GameProfile UnsupportedProfile(DeploymentTarget game) => new(
        $"unsupported-{game.InstallId.Value}", game.SteamAppId, game.Name, [], null, null, game.Engine, "Unknown",
        DeploymentPlanner.SupportedProxyNames, [], [], null, GameProfileSupport.Unsupported, false,
        new Dictionary<string, string>(), [], ["No supported game profile is available."], null);

    private static readonly IReadOnlyList<GameProfile> BuiltInProfiles =
    [
        new(
            "steam-1091500-cyberpunk-2077", 1091500, "Cyberpunk 2077", ["Cyberpunk2077"],
            "bin/x64/Cyberpunk2077.exe", "bin/x64", GameEngine.Unknown, "DirectX 12",
            ["dxgi.dll", "winmm.dll", "version.dll", "d3d12.dll", "winhttp.dll", "wininet.dll", "dbghelp.dll"],
            ["dbghelp.dll"],
            [
                "amd_ags_x64.dll",
                "amd_fidelityfx_dx12.dll",
                "dbghelp.dll",
                "dbgcore.dll",
                "libxess.dll",
                "nvngx_dlss.dll",
                "nvngx_dlssd.dll",
                "nvngx_dlssg.dll"
            ],
            new(new("https://clshortfuse.github.io/renodx/renodx-cp2077.addon64"), "renodx-cp2077.addon64", "snapshot", PeArchitecture.X64, true),
            GameProfileSupport.Supported, true,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LoadReshade"] = "true",
                ["LoadAsiPlugins"] = "true"
            },
            ["The game ships debugging and renderer/upscaler runtime DLLs in bin/x64; these files are game-owned and must not be replaced."],
            ["RenoDX and OptiScaler support changes with game and Proton updates; review the confirmation summary."],
            "RenoDX with DLSS on Wayland may require gamescope or PROTON_DISABLE_NVAPI=1."),
        new(
            "steam-3668370-night-swarm", 3668370, "Night Swarm", [], null, null,
            GameEngine.Unity, "DirectX 11", ["dxgi.dll", "winmm.dll", "version.dll", "winhttp.dll"], [], [],
            new(new("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64"), "renodx-unityengine.addon64", "snapshot", PeArchitecture.X64, false),
            GameProfileSupport.EngineFallback, false, new Dictionary<string, string>(), [],
            ["The AppID identifies Night Swarm, but RenoDX uses the official generic Unity x64 fallback; this is not an exact Night Swarm addon or a game-specific compatibility claim."], null),
        new(
            "engine-unity-x64", null, "Unity 64-bit fallback", [], null, null, GameEngine.Unity, "DirectX",
            ["dxgi.dll", "winmm.dll", "version.dll"], [], [],
            new(new("https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64"), "renodx-unityengine.addon64", "snapshot", PeArchitecture.X64, false),
            GameProfileSupport.EngineFallback, false, new Dictionary<string, string>(), [],
            ["This game uses the official generic Unity RenoDX addon."], null),
        new(
            "engine-unreal-x64", null, "Unreal Engine 4/5 64-bit fallback", [], null, null, GameEngine.Unreal, "DirectX",
            ["dxgi.dll", "winmm.dll", "version.dll"], [], [],
            new(new("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-unrealengine.addon64"), "renodx-unrealengine.addon64", "snapshot", PeArchitecture.X64, false),
            GameProfileSupport.EngineFallback, false, new Dictionary<string, string>(), [],
            ["This game uses the official generic Unreal Engine RenoDX addon."], null)
    ];

    public sealed class GameProfileDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<GameProfile> Profiles { get; set; } = [];
    }
}
