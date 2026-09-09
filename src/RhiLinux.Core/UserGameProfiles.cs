using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhiLinux.Core;

public sealed record UserGameProfile(
    GameInstallId InstallId,
    string? PreferredExecutable,
    string? PreferredDeploymentDirectory,
    string? PreferredRecommendationId,
    string? PreferredProxy,
    bool AutoUseRecommendation,
    DateTimeOffset UpdatedAt);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserGameProfileState
{
    Valid,
    NeedsReview
}

public sealed record UserGameProfileValidation(
    UserGameProfile Profile,
    UserGameProfileState State,
    InstalledGame EffectiveGame,
    IReadOnlyList<string> Issues);

public interface IUserGameProfileStore
{
    Task<IReadOnlyDictionary<GameInstallId, UserGameProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IEnumerable<UserGameProfile> profiles, CancellationToken cancellationToken = default);
}

public sealed class JsonUserGameProfileStore(string path) : IUserGameProfileStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyDictionary<GameInstallId, UserGameProfile>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new Dictionary<GameInstallId, UserGameProfile>();
        await using var input = File.OpenRead(path);
        var profiles = await JsonSerializer.DeserializeAsync<List<UserGameProfile>>(input, Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
        return profiles.Where(item => !item.InstallId.IsEmpty)
            .GroupBy(item => item.InstallId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First());
    }

    public async Task SaveAsync(IEnumerable<UserGameProfile> profiles, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Profile path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(output,
                    profiles.OrderBy(item => item.InstallId).ToArray(), Options, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class UserGameProfileValidator
{
    public static UserGameProfileValidation Validate(
        InstalledGame game,
        UserGameProfile profile,
        RecommendedSetupResult? recommendations = null)
    {
        var issues = new List<string>();
        var executable = game.Executable;
        var deployment = game.DeploymentDirectory;
        if (!profile.InstallId.Equals(game.InstallId))
            issues.Add("The profile belongs to a different installation identity.");
        if (!string.IsNullOrWhiteSpace(profile.PreferredExecutable))
        {
            if (File.Exists(profile.PreferredExecutable) &&
                GameOverrideValidator.IsContained(game.GameRoot, profile.PreferredExecutable))
                executable = Path.GetFullPath(profile.PreferredExecutable);
            else if (File.Exists(profile.PreferredExecutable))
                issues.Add("The preferred executable is outside the game installation.");
            else issues.Add("The preferred executable no longer exists.");
        }
        if (!string.IsNullOrWhiteSpace(profile.PreferredDeploymentDirectory))
        {
            if (Directory.Exists(profile.PreferredDeploymentDirectory) &&
                GameOverrideValidator.IsContained(game.GameRoot, profile.PreferredDeploymentDirectory))
                deployment = Path.GetFullPath(profile.PreferredDeploymentDirectory);
            else if (Directory.Exists(profile.PreferredDeploymentDirectory))
                issues.Add("The preferred deployment directory is outside the game installation.");
            else
                issues.Add("The preferred deployment directory no longer exists.");
        }
        if (!string.IsNullOrWhiteSpace(profile.PreferredRecommendationId) && recommendations is not null &&
            recommendations.Options.All(option => option.Id != profile.PreferredRecommendationId || !option.IsSupported))
            issues.Add("The preferred recommendation is no longer supported.");
        if (!string.IsNullOrWhiteSpace(profile.PreferredProxy) &&
            !DeploymentPlannerProxyNames.Contains(profile.PreferredProxy))
            issues.Add("The preferred proxy name is not supported.");

        return new(
            profile,
            issues.Count == 0 ? UserGameProfileState.Valid : UserGameProfileState.NeedsReview,
            game with { Executable = executable, DeploymentDirectory = deployment },
            issues);
    }

    private static readonly HashSet<string> DeploymentPlannerProxyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "winmm.dll", "version.dll"
    };
}

public static class UserGameProfileRelinker
{
    public static UserGameProfile? TryRelink(
        UserGameProfile profile,
        InstalledGame previous,
        InstalledGame current,
        DetectionConfidence confidence)
    {
        if (!profile.InstallId.Equals(previous.InstallId) || confidence != DetectionConfidence.High)
            return null;
        if (previous.Store != current.Store || previous.PrimaryLauncher != current.PrimaryLauncher ||
            string.IsNullOrWhiteSpace(previous.ExternalId) ||
            !string.Equals(previous.ExternalId, current.ExternalId, StringComparison.OrdinalIgnoreCase))
            return null;
        return profile with
        {
            InstallId = current.InstallId,
            PreferredExecutable = RelinkPath(profile.PreferredExecutable, previous.GameRoot, current.GameRoot),
            PreferredDeploymentDirectory = RelinkPath(
                profile.PreferredDeploymentDirectory, previous.GameRoot, current.GameRoot),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static string? RelinkPath(string? path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var canonicalOld = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot));
        var canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.Equals(canonicalOld, StringComparison.Ordinal) &&
            !canonicalPath.StartsWith(canonicalOld + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return path;
        return Path.GetFullPath(Path.Combine(newRoot, Path.GetRelativePath(canonicalOld, canonicalPath)));
    }
}
