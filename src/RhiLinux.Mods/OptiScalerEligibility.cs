using RhiLinux.Core;

namespace RhiLinux.Mods;

public enum OptiScalerCompatibilityLevel
{
    Verified,
    KnownCompatible,
    Experimental,
    Unsupported,
    BlockedByAntiCheat,
    BlockedByUnresolvedFileConflict
}

public sealed record OptiScalerEligibility(
    OptiScalerCompatibilityLevel Level,
    bool CanInstall,
    bool RequiresExplicitConfirmation,
    string Explanation);

public static class OptiScalerEligibilityService
{
    public static OptiScalerEligibility Evaluate(
        SteamGame game,
        GameProfileMatch profile,
        ProxySelectionResult proxy) =>
        Evaluate(game.ToDeploymentTarget(), profile, proxy);

    public static OptiScalerEligibility Evaluate(
        DeploymentTarget game,
        GameProfileMatch profile,
        ProxySelectionResult proxy)
    {
        if (game.RequiresConfirmation)
            return new(OptiScalerCompatibilityLevel.BlockedByAntiCheat, false, false,
                "Installation is blocked because anti-cheat files require an explicit compatibility decision.");

        if (game.Executable is null ||
            !Path.GetExtension(game.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(OptiScalerCompatibilityLevel.Unsupported, false, false,
                "A Windows executable is required.");

        var architecture = SelectedArchitecture(game);
        if (architecture != PeArchitecture.X64)
            return new(OptiScalerCompatibilityLevel.Unsupported, false, false,
                $"OptiScaler automation currently requires an x64 Windows executable; detected {architecture}.");

        var gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.GameRoot));
        var deploymentDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.DeploymentDirectory));
        if (!Directory.Exists(game.DeploymentDirectory) ||
            !deploymentDirectory.Equals(gameRoot, StringComparison.Ordinal) &&
            !deploymentDirectory.StartsWith(gameRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return new(OptiScalerCompatibilityLevel.Unsupported, false, false,
                "The selected deployment directory is not a safe directory inside the game installation.");

        if (!proxy.HasSafeProxy)
            return new(OptiScalerCompatibilityLevel.BlockedByUnresolvedFileConflict, false, false,
                "Installation is blocked because all supported loading methods are already in use. No files will be changed.");

        if (profile.Profile.KnownConflicts.Any(x => x.Contains("OptiScaler incompatible", StringComparison.OrdinalIgnoreCase)))
            return new(OptiScalerCompatibilityLevel.Unsupported, false, false,
                "This game profile records a known OptiScaler incompatibility.");

        if (profile.Profile.OptiScalerCompatible)
        {
            var level = profile.ExactAppId ? OptiScalerCompatibilityLevel.Verified : OptiScalerCompatibilityLevel.KnownCompatible;
            return new(level, true, false,
                level == OptiScalerCompatibilityLevel.Verified
                    ? "This game has a verified OptiScaler profile."
                    : "This game is known to be compatible with OptiScaler.");
        }

        return new(OptiScalerCompatibilityLevel.Experimental, true, true,
            "OptiScaler compatibility is unverified for this game. Review the compatibility summary before confirming installation.");
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
            return reader.ReadUInt16() == 0x8664 ? PeArchitecture.X64 : PeArchitecture.X86;
        }
        catch (IOException) { return PeArchitecture.Unknown; }
    }
}
