using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RhiLinux.Core;

namespace RhiLinux.Gui;

public sealed partial class DeploymentPlanDialog : Window
{
    private MainViewModel mainViewModel = null!;
    private DeploymentPlan plan = null!;
    private DeploymentPlanDialogViewModel dialogViewModel = null!;
    private CancellationTokenSource? cancellation;

    public DeploymentPlanDialog() => AvaloniaXamlLoader.Load(this);

    public DeploymentPlanDialog(MainViewModel mainViewModel, DeploymentPlan plan) : this()
    {
        this.mainViewModel = mainViewModel;
        this.plan = plan;
        dialogViewModel = new DeploymentPlanDialogViewModel(plan);
        DataContext = dialogViewModel;
        Closing += (_, eventArgs) => { if (dialogViewModel.IsRunning) eventArgs.Cancel = true; };
    }

    private async void Execute_Click(object? sender, RoutedEventArgs eventArgs)
    {
        cancellation = new CancellationTokenSource();
        dialogViewModel.Begin();
        try
        {
            var result = await mainViewModel.ExecuteAsync(plan, false, cancellation.Token);
            dialogViewModel.Complete(result);
        }
        catch (Exception exception) { dialogViewModel.Fail(exception.Message); }
        finally { cancellation.Dispose(); cancellation = null; }
    }

    private void CancelOperation_Click(object? sender, RoutedEventArgs eventArgs)
    {
        dialogViewModel.ProgressText = "Cancelling safely…";
        cancellation?.Cancel();
    }
    private void Close_Click(object? sender, RoutedEventArgs eventArgs) => Close();
}

public sealed record PlanOperationViewModel(int Number, string Description);

public sealed class DeploymentPlanDialogViewModel : INotifyPropertyChanged
{
    private bool isRunning;
    private bool hasResult;
    private string resultTitle = string.Empty;
    private string resultMessage = string.Empty;
    private string rollbackMessage = string.Empty;
    private string progressText = "Preparing deployment…";

    public DeploymentPlanDialogViewModel(DeploymentPlan plan)
    {
        var action = plan.Action.ToLowerInvariant();
        ActionButtonText = action.Contains("remove", StringComparison.Ordinal) &&
            action.Contains("recommended", StringComparison.Ordinal)
            ? "Remove all managed components"
            : plan.RequiresRepair || action.Contains("repair", StringComparison.Ordinal) ? "Repair" :
            action.Contains("remove", StringComparison.Ordinal) ? "Remove" :
            action.Contains("update", StringComparison.Ordinal) ? "Update" :
            action.Contains("restore", StringComparison.Ordinal) ? "Restore" : "Install";
        Title = $"Ready to {ActionButtonText.ToLowerInvariant()}";
        Subtitle = plan.SteamAppId is { } steamAppId
            ? $"{plan.Action} · Steam AppID {steamAppId}"
            : $"{plan.Action} · {plan.InstallId}";
        CompatibilityMessage = plan.CompatibilityMessage ?? string.Empty;
        Warnings = plan.Warnings;
        Operations = plan.Operations.Select((operation, index) => new PlanOperationViewModel(index + 1, DeploymentExecutor.Describe(operation))).ToArray();
        SummaryOperations = BuildSummary(plan).Select((description, index) => new PlanOperationViewModel(index + 1, description)).ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get; }
    public string ActionButtonText { get; }
    public string Subtitle { get; }
    public string CompatibilityMessage { get; }
    public bool HasCompatibilityMessage => CompatibilityMessage.Length > 0;
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<PlanOperationViewModel> Operations { get; }
    public IReadOnlyList<PlanOperationViewModel> SummaryOperations { get; }
    public bool HasWarnings => Warnings.Count > 0;
    public string OperationCount => Operations.Count == 1 ? "1 operation" : $"{Operations.Count} operations";
    public string SummaryCount => SummaryOperations.Count == 1 ? "1 step" : $"{SummaryOperations.Count} steps";
    public bool IsRunning { get => isRunning; private set { if (Set(ref isRunning, value)) RaiseAvailability(); } }
    public bool HasResult
    {
        get => hasResult;
        private set
        {
            if (Set(ref hasResult, value)) RaiseAvailability();
        }
    }
    public string ResultTitle { get => resultTitle; private set => Set(ref resultTitle, value); }
    public string ResultMessage { get => resultMessage; private set => Set(ref resultMessage, value); }
    public string RollbackMessage { get => rollbackMessage; private set { if (Set(ref rollbackMessage, value)) OnPropertyChanged(nameof(HasRollbackMessage)); } }
    public string ProgressText { get => progressText; set => Set(ref progressText, value); }
    public bool HasRollbackMessage => RollbackMessage.Length > 0;
    public bool CanExecute => !IsRunning && !HasResult;
    public bool CanClose => !IsRunning;
    public string CloseText => HasResult ? "Close" : "Cancel";
    public string ExecutionHint => "Changed files are backed up first. Confirm to apply the plan.";

    public void Begin()
    {
        IsRunning = true;
        ProgressText = "Applying changes safely…";
    }
    public void Complete(ExecutionResult result)
    {
        IsRunning = false;
        HasResult = true;
        var succeeded = result.IsSuccessfulOutcome;
        ResultTitle = succeeded
            ? result.State == OperationLifecycleState.SucceededWithWarning
                ? "Installed successfully"
                : "Changes completed"
            : "Operation failed";
        ResultMessage = succeeded
            ? result.State == OperationLifecycleState.SucceededWithWarning
                ? result.Warning ?? "The files were verified. Component status is refreshing."
                : "The files, settings, and installed state were verified successfully."
            : result.Error ?? "The operation did not complete.";
        RollbackMessage = result.RolledBack ? "Completed operations were rolled back in reverse order. Review the error before trying again." : string.Empty;
    }
    public void Fail(string message) => Complete(new ExecutionResult(false, false, false, [], message));

    private static IReadOnlyList<string> BuildSummary(DeploymentPlan plan)
    {
        var summary = new List<string>();
        void Add(string text) { if (!summary.Contains(text, StringComparer.Ordinal)) summary.Add(text); }
        foreach (var operation in plan.Operations)
        {
            if (operation.Type == DeploymentOperationType.Backup) Add("Create backups of files that will be replaced");
            if (operation.Type == DeploymentOperationType.Move || operation.Type == DeploymentOperationType.WriteIniValue)
                Add("Configure compatibility between installed components");
            if (operation.Type == DeploymentOperationType.DeleteManagedFile) Add("Remove managed component files");
            if (operation.Type == DeploymentOperationType.RestoreBackup) Add("Restore the original backed-up files");
            if (operation.Type != DeploymentOperationType.Copy) continue;
            Add(operation.Component switch
            {
                ComponentKind.ReShade => "Install the required ReShade build",
                ComponentKind.RenoDx => "Install the RenoDX profile for this game",
                ComponentKind.OptiScaler => "Install OptiScaler compatibility files",
                _ => "Install required files"
            });
        }
        if (summary.Count == 0) Add("Validate the selected game and installation state");
        return summary;
    }

    private void RaiseAvailability()
    {
        OnPropertyChanged(nameof(CanExecute)); OnPropertyChanged(nameof(CanClose)); OnPropertyChanged(nameof(CloseText));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(propertyName); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
