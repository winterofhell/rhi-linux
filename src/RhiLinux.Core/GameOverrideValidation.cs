namespace RhiLinux.Core;

public sealed record GameOverrideValidationResult(
    bool IsValid,
    GameOverride Override,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string DeploymentReason);

public static class GameOverrideValidator
{
    public static GameOverrideValidationResult Validate(
        InstalledGame game,
        string? executable,
        string? deploymentDirectory,
        string? prefix)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var gameRoot = Canonical(game.GameRoot);
        var selectedExecutable = NormalizeOptional(executable);
        var selectedDeployment = NormalizeOptional(deploymentDirectory);
        var selectedPrefix = NormalizeOptional(prefix);

        if (selectedExecutable is not null)
        {
            if (!File.Exists(selectedExecutable)) errors.Add("The selected executable does not exist.");
            else if (!selectedExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                errors.Add("Choose a Windows .exe file.");
            if (!IsContained(gameRoot, selectedExecutable))
                errors.Add("The executable must be inside the game installation.");
        }

        if (selectedDeployment is not null)
        {
            if (!Directory.Exists(selectedDeployment)) errors.Add("The deployment directory does not exist.");
            if (!IsContained(gameRoot, selectedDeployment))
                errors.Add("The deployment directory must be inside the game installation.");
            if (Path.GetPathRoot(selectedDeployment)?.Equals(selectedDeployment, StringComparison.Ordinal) == true)
                errors.Add("A filesystem root cannot be used as a deployment directory.");
            if (selectedPrefix is not null && PathsEqual(selectedDeployment, selectedPrefix))
                errors.Add("The compatibility prefix root is not a deployment directory.");
        }

        if (selectedPrefix is not null)
        {
            if (!Directory.Exists(selectedPrefix)) errors.Add("The compatibility prefix does not exist.");
            else if (!LooksLikePrefix(selectedPrefix))
                errors.Add("The selected directory does not look like a Wine or Proton prefix.");
        }

        var effectiveExecutable = selectedExecutable ?? game.Executable;
        var effectiveDeployment = selectedDeployment ??
            (selectedExecutable is null ? game.DeploymentDirectory : Path.GetDirectoryName(selectedExecutable));
        var reason = selectedDeployment is not null
            ? "Selected by a saved manual deployment-directory override."
            : effectiveExecutable is not null
                ? "Automatically selected as the directory containing the primary game executable."
                : "Using the game installation root because no primary executable is selected.";

        if (effectiveDeployment is not null && !CanRead(effectiveDeployment))
            warnings.Add("The deployment directory may not be readable by the current user.");

        return new(
            errors.Count == 0,
            new(selectedExecutable, selectedDeployment, selectedPrefix),
            errors,
            warnings,
            reason);
    }

    public static bool LooksLikePrefix(string path) =>
        Directory.Exists(Path.Combine(path, "drive_c")) &&
        (Directory.Exists(Path.Combine(path, "dosdevices")) ||
         File.Exists(Path.Combine(path, "system.reg")) ||
         File.Exists(Path.Combine(path, "user.reg")));

    public static bool IsContained(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Canonical(root));
        var canonicalPath = Canonical(path);
        return PathsEqual(canonicalRoot, canonicalPath) ||
               canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool CanRead(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? NormalizeOptional(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Canonical(path);

    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var info = Directory.Exists(full) ? new DirectoryInfo(full) as FileSystemInfo : new FileInfo(full);
            return info.ResolveLinkTarget(true)?.FullName ?? full;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return full;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal);
}
