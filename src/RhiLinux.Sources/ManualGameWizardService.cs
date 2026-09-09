using System.Text.Json;
using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Sources;

public sealed class ManualGameWizardService : IManualGameWizardService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string manualGamesPath;
    private readonly ExecutableDetector detector;
    private readonly Func<DeploymentTarget, CancellationToken, Task<IReadOnlyList<ComponentStatus>>> detectComponents;

    public ManualGameWizardService(
        string manualGamesPath,
        ExecutableDetector? executableDetector = null,
        Func<DeploymentTarget, CancellationToken, Task<IReadOnlyList<ComponentStatus>>>? detectComponents = null)
    {
        this.manualGamesPath = manualGamesPath;
        detector = executableDetector ?? new ExecutableDetector();
        this.detectComponents = detectComponents ?? ((_, _) => Task.FromResult<IReadOnlyList<ComponentStatus>>([]));
    }

    public async Task<ManualGameScanResult> ScanAsync(
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            throw new ArgumentException("Choose an install directory.", nameof(installDirectory));
        var root = Path.GetFullPath(installDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected install directory does not exist.");
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = await Task.Run(() => detector.Rank(root, Path.GetFileName(root)), cancellationToken)
            .ConfigureAwait(false);
        var executableRows = candidates.Select(candidate => new ManualExecutableCandidate(
            Path.GetFileName(candidate.Path),
            Path.GetRelativePath(root, candidate.Path),
            candidate.Path,
            candidate.Architecture,
            candidate.Confidence,
            candidate.Reasons.Count == 0 ? "Windows executable" : string.Join("; ", candidate.Reasons),
            candidate.Score)).ToArray();
        var target = new DeploymentTarget(
            GameInstallId.Create(GameStore.Other, GameLauncher.Manual, Path.GetFileName(root), root,
                candidates.FirstOrDefault()?.Path),
            Path.GetFileName(root),
            root,
            candidates.FirstOrDefault()?.Path,
            candidates.FirstOrDefault() is { } selected ? Path.GetDirectoryName(selected.Path) ?? root : root,
            null,
            null,
            GameStore.Other,
            GameLauncher.Manual,
            Path.GetFileName(root),
            candidates.FirstOrDefault()?.Confidence ?? DetectionConfidence.None,
            "Selected through the Manual Game Wizard.",
            detector.DetectEngine(root),
            candidates,
            Environment: CompatibilityEnvironment.Wine);
        var components = await detectComponents(target, cancellationToken).ConfigureAwait(false);
        return new(root, executableRows, FindPrefixes(root), target.Engine, components);
    }

    public async Task SaveAsync(ManualGameRegistration registration, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(registration.InstallDirectory);
        var executable = Path.GetFullPath(registration.Executable);
        var deployment = Path.GetFullPath(registration.DeploymentDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The install directory does not exist.");
        if (!File.Exists(executable)) throw new FileNotFoundException("The selected executable does not exist.", executable);
        if (!IsWithin(root, executable)) throw new InvalidDataException("The executable must be inside the selected install directory.");
        if (!Directory.Exists(deployment)) throw new DirectoryNotFoundException("The deployment directory does not exist.");
        if (!IsWithin(root, deployment)) throw new InvalidDataException("The deployment directory must be inside the selected install directory.");
        if (!string.IsNullOrWhiteSpace(registration.Prefix) && !IsWinePrefix(registration.Prefix))
            throw new InvalidDataException("The selected Wine prefix does not have the expected structure.");

        var records = await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
        var next = new ManualEntry(
            registration.Name.Trim(),
            root,
            executable,
            deployment,
            string.IsNullOrWhiteSpace(registration.Prefix) ? null : Path.GetFullPath(registration.Prefix),
            string.IsNullOrWhiteSpace(registration.ExternalId)
                ? StableHash.Hex(StableHash.Ordinal(root))
                : registration.ExternalId.Trim(),
            "other",
            "windows");
        records.RemoveAll(entry => string.Equals(entry.ExternalId, next.ExternalId, StringComparison.Ordinal));
        records.Add(next);
        await SaveEntriesAsync(records, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ManualEntry>> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(manualGamesPath)) return [];
        await using var input = File.OpenRead(manualGamesPath);
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        var element = document.RootElement.ValueKind == JsonValueKind.Object &&
                      document.RootElement.TryGetProperty("games", out var games)
            ? games
            : document.RootElement;
        return element.ValueKind == JsonValueKind.Array
            ? element.Deserialize<List<ManualEntry>>(Options) ?? []
            : [];
    }

    private async Task SaveEntriesAsync(List<ManualEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(manualGamesPath)
            ?? throw new InvalidOperationException("Manual game path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(manualGamesPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(output, new ManualDocument(entries.OrderBy(item => item.Name).ToArray()),
                    Options, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, manualGamesPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IReadOnlyList<string> FindPrefixes(string root)
    {
        var result = new List<string>();
        var current = new DirectoryInfo(root);
        for (var depth = 0; current is not null && depth < 4; depth++, current = current.Parent)
        {
            if (IsWinePrefix(current.FullName)) result.Add(current.FullName);
            var pfx = Path.Combine(current.FullName, "pfx");
            if (IsWinePrefix(pfx)) result.Add(pfx);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsWinePrefix(string path) =>
        Directory.Exists(Path.Combine(path, "drive_c")) &&
        (Directory.Exists(Path.Combine(path, "dosdevices")) || File.Exists(Path.Combine(path, "system.reg")));

    private static bool IsWithin(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        return canonicalPath.Equals(canonicalRoot, StringComparison.Ordinal) ||
               canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed record ManualDocument(IReadOnlyList<ManualEntry> Games);
    private sealed record ManualEntry(
        string Name,
        string InstallRoot,
        string Executable,
        string DeploymentDirectory,
        string? Prefix,
        string ExternalId,
        string Store,
        string Platform);
}
