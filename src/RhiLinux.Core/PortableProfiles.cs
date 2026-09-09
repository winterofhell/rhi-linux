using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhiLinux.Core;

public sealed record PortableGameProfile(
    string Id,
    GameStore Store,
    GameLauncher Launcher,
    string? ExternalId,
    string DisplayName,
    string? PreferredRecommendationId,
    string? PreferredProxy,
    string? ExecutableRelativePath,
    string? DeploymentRelativePath,
    bool AutoUseRecommendation);

public sealed record PortableProfileDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<PortableGameProfile> Profiles);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileImportMatchState
{
    ExactMatch,
    LikelyMatch,
    NoLocalMatch,
    Conflict
}

public sealed record ProfileImportEntry(
    PortableGameProfile Profile,
    ProfileImportMatchState State,
    InstalledGame? MatchedGame,
    IReadOnlyList<InstalledGame> Candidates,
    string Explanation)
{
    public bool RequiresReview => State is ProfileImportMatchState.LikelyMatch or ProfileImportMatchState.Conflict;
}

public sealed record ProfileImportPreview(
    PortableProfileDocument Document,
    IReadOnlyList<ProfileImportEntry> Entries,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record ProfileImportApproval(string PortableProfileId, GameInstallId InstallId);

public interface IPortableProfileService
{
    Task ExportAsync(
        string path,
        IEnumerable<UserGameProfile> profiles,
        IReadOnlyDictionary<GameInstallId, InstalledGame> games,
        CancellationToken cancellationToken = default);

    Task<ProfileImportPreview> PreviewImportAsync(
        string path,
        IReadOnlyList<InstalledGame> games,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserGameProfile>> ApplyImportAsync(
        ProfileImportPreview preview,
        IReadOnlyCollection<ProfileImportApproval> approvals,
        IReadOnlyDictionary<GameInstallId, UserGameProfile> existing,
        IUserGameProfileStore store,
        CancellationToken cancellationToken = default);
}

public sealed class PortableProfileService : IPortableProfileService
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportAsync(
        string path,
        IEnumerable<UserGameProfile> profiles,
        IReadOnlyDictionary<GameInstallId, InstalledGame> games,
        CancellationToken cancellationToken = default)
    {
        var portable = new List<PortableGameProfile>();
        foreach (var profile in profiles.OrderBy(item => item.InstallId))
        {
            if (!games.TryGetValue(profile.InstallId, out var game)) continue;
            portable.Add(new(
                Guid.NewGuid().ToString("N"),
                game.Store,
                game.PrimaryLauncher,
                SafeExternalId(game.ExternalId),
                game.Name,
                profile.PreferredRecommendationId,
                profile.PreferredProxy,
                RelativePath(game.GameRoot, profile.PreferredExecutable),
                RelativePath(game.GameRoot, profile.PreferredDeploymentDirectory),
                profile.AutoUseRecommendation));
        }
        var document = new PortableProfileDocument(CurrentSchemaVersion, DateTimeOffset.UtcNow, portable);
        await AtomicJsonWriteAsync(path, document, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfileImportPreview> PreviewImportAsync(
        string path,
        IReadOnlyList<InstalledGame> games,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        PortableProfileDocument document;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonSerializer.DeserializeAsync<PortableProfileDocument>(input, Options, cancellationToken)
                .ConfigureAwait(false) ?? new(0, DateTimeOffset.MinValue, []);
        }
        catch (JsonException exception)
        {
            return new(new(0, DateTimeOffset.MinValue, []), [], [$"The profile file is not valid JSON: {exception.Message}"]);
        }
        if (document.SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Profile schema {document.SchemaVersion} is not supported.");
        if (document.Profiles.Count > 10000) errors.Add("The profile file contains too many entries.");
        if (document.Profiles.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != document.Profiles.Count)
            errors.Add("The profile file contains duplicate entry identifiers.");
        foreach (var profile in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DisplayName))
                errors.Add("Every imported profile must have an identifier and display name.");
            if (!SafeRelative(profile.ExecutableRelativePath) || !SafeRelative(profile.DeploymentRelativePath))
                errors.Add($"{profile.DisplayName} contains a path outside its game installation.");
        }
        var entries = document.Profiles.Select(profile => Match(profile, games)).ToArray();
        return new(document, entries, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<IReadOnlyList<UserGameProfile>> ApplyImportAsync(
        ProfileImportPreview preview,
        IReadOnlyCollection<ProfileImportApproval> approvals,
        IReadOnlyDictionary<GameInstallId, UserGameProfile> existing,
        IUserGameProfileStore store,
        CancellationToken cancellationToken = default)
    {
        if (!preview.IsValid) throw new InvalidDataException("The profile import preview contains validation errors.");
        var approved = approvals.ToDictionary(item => item.PortableProfileId, StringComparer.Ordinal);
        var result = existing.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var entry in preview.Entries)
        {
            InstalledGame? game = null;
            if (entry.State == ProfileImportMatchState.ExactMatch)
                game = entry.MatchedGame;
            else if (approved.TryGetValue(entry.Profile.Id, out var approval))
                game = entry.Candidates.FirstOrDefault(candidate => candidate.InstallId.Equals(approval.InstallId));
            if (game is null) continue;
            result[game.InstallId] = new(
                game.InstallId,
                ResolveRelative(game.GameRoot, entry.Profile.ExecutableRelativePath),
                ResolveRelative(game.GameRoot, entry.Profile.DeploymentRelativePath),
                entry.Profile.PreferredRecommendationId,
                entry.Profile.PreferredProxy,
                entry.Profile.AutoUseRecommendation,
                DateTimeOffset.UtcNow);
        }
        var values = result.Values.OrderBy(item => item.InstallId).ToArray();
        await store.SaveAsync(values, cancellationToken).ConfigureAwait(false);
        return values;
    }

    private static ProfileImportEntry Match(PortableGameProfile profile, IReadOnlyList<InstalledGame> games)
    {
        var exact = games.Where(game => game.Store == profile.Store &&
            game.PrimaryLauncher == profile.Launcher &&
            !string.IsNullOrWhiteSpace(profile.ExternalId) &&
            string.Equals(game.ExternalId, profile.ExternalId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1)
            return new(profile, ProfileImportMatchState.ExactMatch, exact[0], exact,
                $"Matched to the {exact[0].PrimaryLauncher} installation by store identity.");
        if (exact.Length > 1)
            return new(profile, ProfileImportMatchState.Conflict, null, exact,
                "More than one local installation has the same external identity.");

        var likely = games.Where(game => game.Store == profile.Store &&
            NormalizeName(game.Name) == NormalizeName(profile.DisplayName)).ToArray();
        if (likely.Length == 1)
            return new(profile, ProfileImportMatchState.LikelyMatch, likely[0], likely,
                $"The name and store match a {likely[0].PrimaryLauncher} installation. Review before importing.");
        if (likely.Length > 1)
            return new(profile, ProfileImportMatchState.Conflict, null, likely,
                "Several local installations have the same name and store.");
        return new(profile, ProfileImportMatchState.NoLocalMatch, null, [],
            "No matching local installation was found. This profile will be skipped.");
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? RelativePath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!GameOverrideValidator.IsContained(root, path)) return null;
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative == "." ? string.Empty : relative;
    }

    private static string? ResolveRelative(string root, string? relative)
    {
        if (relative is null) return null;
        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!GameOverrideValidator.IsContained(root, combined))
            throw new InvalidDataException("An imported profile path escapes the game installation.");
        return combined;
    }

    private static bool SafeRelative(string? path)
    {
        if (path is null) return true;
        if (Path.IsPathRooted(path) || path.IndexOf('\0') >= 0) return false;
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.All(segment => segment is not "." and not "..");
    }

    private static string? SafeExternalId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return null;
        return value.Any(char.IsControl) ? null : value;
    }

    private static async Task AtomicJsonWriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Export path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(output, value, Options, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
