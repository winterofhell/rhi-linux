using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed partial class EnhancementPlanDialog : Window
{
    private readonly MainViewModel? owner;
    private readonly EnhancementPlanDialogViewModel model;
    private readonly Func<IProgress<string>, CancellationToken, Task<EnhancementPlan>>? build;
    private readonly CancellationTokenSource cancellation = new();
    private bool executed;

    public EnhancementPlanDialog()
    {
        model = new EnhancementPlanDialogViewModel("Review changes");
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
    }

    public EnhancementPlanDialog(
        MainViewModel owner,
        string title,
        Func<IProgress<string>, CancellationToken, Task<EnhancementPlan>> build)
    {
        this.owner = owner;
        this.build = build;
        model = new EnhancementPlanDialogViewModel(title);
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public bool Changed { get; private set; }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        if (build is null || owner is null) return;
        var progress = new Progress<string>(message => model.ProgressText = message);
        try
        {
            var plan = await build(progress, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                owner.DiscardEnhancementPlan(plan);
                return;
            }
            model.Load(plan);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              InvalidOperationException or HttpRequestException or ArgumentException or
                                              System.Text.Json.JsonException)
        {
            model.FailPreparation(exception.Message);
        }
    }

    private async void Apply_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (owner is null || model.Plan is not { } plan || !model.CanExecute) return;
        executed = true;
        model.Begin();
        var result = await owner.ExecuteEnhancementPlanAsync(plan, cancellation.Token);
        Changed = result.Succeeded;
        model.Complete(result);
    }

    private void Close_Click(object? sender, RoutedEventArgs eventArgs) => Close();

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (model.IsRunning) eventArgs.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        cancellation.Cancel();
        if (!executed && model.Plan is { } plan) owner?.DiscardEnhancementPlan(plan);
        cancellation.Dispose();
    }
}
