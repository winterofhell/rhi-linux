using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhiLinux.Core;

[JsonConverter(typeof(GameInstallIdJsonConverter))]
public readonly record struct GameInstallId(string Value) : IComparable<GameInstallId>, IEquatable<GameInstallId>
{
    public static GameInstallId Empty { get; } = new(string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;

    public int CompareTo(GameInstallId other) =>
        StringComparer.Ordinal.Compare(Value, other.Value);

    public bool Equals(GameInstallId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

    public static GameInstallId FromSteam(uint steamAppId, string canonicalInstallRoot, string? executable = null)
    {
        var digest = Digest(GameIdentity.NormalizePath(canonicalInstallRoot), GameIdentity.NormalizePath(executable ?? string.Empty));
        return new($"steam:{steamAppId}:{digest}");
    }

    public static GameInstallId Create(
        GameStore store,
        GameLauncher launcher,
        string? externalId,
        string canonicalInstallRoot,
        string? executable = null)
    {
        if (store == GameStore.Steam &&
            uint.TryParse(externalId, out var steamAppId) &&
            steamAppId != 0)
            return FromSteam(steamAppId, canonicalInstallRoot, executable);

        var storeKey = store.ToString().ToLowerInvariant();
        var launcherKey = launcher.ToString().ToLowerInvariant();
        var external = string.IsNullOrWhiteSpace(externalId) ? "_" : externalId.Trim();
        var digest = Digest(
            GameIdentity.NormalizePath(canonicalInstallRoot),
            GameIdentity.NormalizePath(executable ?? string.Empty));
        return new($"{launcherKey}:{storeKey}:{external}:{digest}");
    }

    public static GameInstallId LegacySteam(uint steamAppId) => new($"steam:{steamAppId}:legacy");

    public static bool TryParse(string? value, out GameInstallId id)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            id = Empty;
            return false;
        }

        id = new(value.Trim());
        return true;
    }

    public static bool TryParseSteamAppId(GameInstallId id, out uint steamAppId)
    {
        steamAppId = 0;
        var value = id.Value;
        if (!value.StartsWith("steam:", StringComparison.Ordinal))
            return false;
        var rest = value["steam:".Length..];
        var colon = rest.IndexOf(':');
        var appIdText = colon >= 0 ? rest[..colon] : rest;
        return uint.TryParse(appIdText, out steamAppId) && steamAppId != 0;
    }

    private static string Digest(string installRoot, string executable)
    {
        var payload = installRoot + "\n" + executable;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

public sealed class GameInstallIdJsonConverter : JsonConverter<GameInstallId>
{
    public override GameInstallId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, GameInstallId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed record StoreGameIdentity(GameStore Store, string ExternalId);

public sealed record SourceRecordId(string ProviderId, string RootId, string ExternalId)
{
    public string Value => $"{ProviderId}:{RootId}:{ExternalId}";
}

public sealed record FieldSelectionEvidence(
    string Field,
    string SelectedSource,
    string Reason,
    DetectionConfidence Confidence,
    string? RejectedAlternative = null);

public sealed record DeploymentTarget(
    GameInstallId InstallId,
    string Name,
    string InstallRoot,
    string? Executable,
    string DeploymentDirectory,
    string? Prefix,
    uint? SteamAppId,
    GameStore Store,
    GameLauncher Launcher,
    string? ExternalId,
    DetectionConfidence Confidence,
    string SelectionReason,
    GameEngine Engine,
    IReadOnlyList<ExecutableCandidate> Candidates,
    bool RequiresConfirmation = false,
    bool IsNativeLinux = false,
    bool HasProtonPrefix = true,
    GameBinaryPlatform Platform = GameBinaryPlatform.Windows,
    CompatibilityEnvironment Environment = CompatibilityEnvironment.Proton,
    bool IsActionable = true,
    string? UnsupportedReason = null,
    bool IsStale = false)
{
    public string GameRoot => InstallRoot;
    public string ProtonPrefix => Prefix ?? string.Empty;
}

public static class DeploymentTargetConversions
{
    public static DeploymentTarget AsTarget(this SteamGame game) => game.ToDeploymentTarget();

    public static DeploymentTarget ToDeploymentTarget(this SteamGame game)
    {
        var installId = GameInstallId.TryParse(game.InstallId, out var parsed) && !parsed.IsEmpty
            ? parsed
            : game.Store == GameStore.Steam && game.AppId != 0
                ? GameInstallId.FromSteam(game.AppId, game.GameRoot, game.Executable)
                : GameInstallId.Create(game.Store, game.Launcher, game.ExternalId, game.GameRoot, game.Executable);

        uint? steamAppId = game.Store == GameStore.Steam && game.AppId != 0 ? game.AppId : null;
        return new DeploymentTarget(
            installId,
            game.Name,
            game.GameRoot,
            game.Executable,
            game.DeploymentDirectory,
            string.IsNullOrWhiteSpace(game.ProtonPrefix) ? null : game.ProtonPrefix,
            steamAppId,
            game.Store,
            game.Launcher,
            game.ExternalId ?? steamAppId?.ToString(),
            game.Confidence,
            game.SelectionReason,
            game.Engine,
            game.Candidates,
            game.RequiresConfirmation,
            game.IsNativeLinux,
            game.HasProtonPrefix,
            game.Platform,
            game.Environment,
            game.IsActionable,
            game.UnsupportedReason);
    }
}
