namespace RhiLinux.Core;

public sealed record ManualExecutableCandidate(
    string FileName,
    string RelativePath,
    string FullPath,
    PeArchitecture Architecture,
    DetectionConfidence Confidence,
    string Reason,
    int Score);

public sealed record ManualGameScanResult(
    string InstallDirectory,
    IReadOnlyList<ManualExecutableCandidate> Executables,
    IReadOnlyList<string> PrefixCandidates,
    GameEngine Engine,
    IReadOnlyList<ComponentStatus> Components);

public sealed record ManualGameRegistration(
    string Name,
    string InstallDirectory,
    string Executable,
    string DeploymentDirectory,
    string? Prefix,
    string ExternalId);

public interface IManualGameWizardService
{
    Task<ManualGameScanResult> ScanAsync(string installDirectory, CancellationToken cancellationToken = default);
    Task SaveAsync(ManualGameRegistration registration, CancellationToken cancellationToken = default);
}
