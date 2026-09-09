using RhiLinux.Core;

namespace RhiLinux.Gui;

public sealed class ManualGameWizardViewModel : PageViewModel
{
    private readonly IManualGameWizardService service;
    private ManualGameScanResult? scan;
    private ManualExecutableCandidate? executable;
    private string? prefix;
    private string name = string.Empty;
    private string deploymentDirectory = string.Empty;
    private bool isBusy;

    public ManualGameWizardViewModel(IManualGameWizardService service) => this.service = service;

    public ManualGameScanResult? Scan { get => scan; private set => Set(ref scan, value); }
    public ManualExecutableCandidate? Executable
    {
        get => executable;
        set { if (Set(ref executable, value)) Changed(nameof(CanSave)); }
    }
    public string? Prefix { get => prefix; set => Set(ref prefix, value); }
    public string Name
    {
        get => name;
        set { if (Set(ref name, value)) Changed(nameof(CanSave)); }
    }
    public string DeploymentDirectory
    {
        get => deploymentDirectory;
        set { if (Set(ref deploymentDirectory, value)) Changed(nameof(CanSave)); }
    }
    public bool IsBusy
    {
        get => isBusy;
        private set { if (Set(ref isBusy, value)) Changed(nameof(CanSave)); }
    }
    public bool CanSave => !IsBusy && Scan is not null && Executable is not null && Name.Trim().Length > 0 &&
                           Directory.Exists(DeploymentDirectory);

    public async Task ScanAsync(string directory, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            Scan = await service.ScanAsync(directory, cancellationToken);
            Executable = Scan.Executables.FirstOrDefault();
            Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Scan.InstallDirectory));
            DeploymentDirectory = Executable is null
                ? Scan.InstallDirectory
                : Path.GetDirectoryName(Executable.FullPath) ?? Scan.InstallDirectory;
            Prefix = Scan.PrefixCandidates.FirstOrDefault();
            Changed(nameof(CanSave));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave || Scan is null || Executable is null)
            throw new InvalidOperationException("Complete the manual game fields before saving.");
        await service.SaveAsync(new(
            Name,
            Scan.InstallDirectory,
            Executable.FullPath,
            DeploymentDirectory,
            Prefix,
            StableHash.Hex(StableHash.Ordinal(Scan.InstallDirectory))), cancellationToken);
    }
}
