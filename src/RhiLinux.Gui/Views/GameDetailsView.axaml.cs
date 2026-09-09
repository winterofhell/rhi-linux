using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Gui.Views;

public sealed partial class GameDetailsView : UserControl
{
    private CancellationTokenSource? operationCancellation;

    public GameDetailsView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? ViewModel => DataContext as MainViewModel;
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    public void ResetPresentation()
    {
        if (this.FindControl<ScrollViewer>("GameContentScroll") is { } contentScroll)
            contentScroll.Offset = default;
        foreach (var name in new[] { "CompatibilityDetails", "AdvancedGameDetails", "AdvancedExecutableDetails", "ApplicationSettings" })
            if (this.FindControl<Expander>(name) is { } expander)
                expander.IsExpanded = false;
        ViewModel?.CollapseComponentDetails();
    }

    private async void ChangeExecutable_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { SelectedGame: { } game } viewModel || Host is not { } host) return;
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the primary Windows game executable",
            AllowMultiple = false,
            SuggestedStartLocation = await TryFolderAsync(game.DeploymentDirectory ?? game.GameRoot),
            FileTypeFilter = [new FilePickerFileType("Windows executable") { Patterns = ["*.exe"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await viewModel.SaveOverridesAsync(path, null);
    }

    private async void ChangeDeploymentFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { SelectedGame: { } game } viewModel || Host is not { } host) return;
        var folders = await host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the game folder for compatibility files",
            AllowMultiple = false,
            SuggestedStartLocation = await TryFolderAsync(game.DeploymentDirectory ?? game.GameRoot)
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await viewModel.SaveOverridesAsync(game.Executable, path);
    }

    private async void ChangePrefixFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { SelectedGame: { } game } viewModel || Host is not { } host) return;
        var folders = await host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Wine or Proton compatibility prefix",
            AllowMultiple = false,
            SuggestedStartLocation = game.Prefix is null ? null : await TryFolderAsync(game.Prefix)
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await viewModel.SaveOverridesAsync(game.Executable, game.DeploymentDirectory, path);
    }

    private async void ResetOverrides_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel) await viewModel.ResetOverridesAsync();
    }

    private async void OpenExecutableFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel?.SelectedGame?.Executable is not { } executable || Host is not { } host) return;
        var directory = Path.GetDirectoryName(executable);
        if (directory is not null) await host.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory));
    }

    private async void ComponentAction_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card } || ViewModel is not { } viewModel) return;
        if (card.ActionText == "Remove" || card.ActionText.StartsWith("Remove ", StringComparison.Ordinal))
        {
            try
            {
                await ShowPlanAsync(await viewModel.BuildRemovePlanAsync(card.Component));
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("Could not build removal plan", exception.Message);
            }
            return;
        }
        await BuildAutomaticComponentPlanAsync(card.Component);
    }

    private async void ComponentRemove_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card } || ViewModel is not { } viewModel) return;
        try
        {
            await ShowPlanAsync(await viewModel.BuildRemovePlanAsync(card.Component));
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not build removal plan", exception.Message);
        }
    }

    private async void ComponentSecondary_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card } || ViewModel is not { } viewModel) return;
        if (card.CanShowFiles)
        {
            if (Host is { } host)
                await MessageDialog.ShowAsync(host, $"{card.Name} files",
                    card.Files.Length == 0 ? "No detected files." : card.Files);
            return;
        }
        if (card.CanCheckAgain) await viewModel.CheckForUpdatesAsync(false);
    }

    private void OpenOfficialPage_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: ComponentCardViewModel card }) ViewModel?.OpenOfficialPage(card);
    }

    private async Task BuildAutomaticComponentPlanAsync(ComponentKind component)
    {
        if (ViewModel is not { } viewModel) return;
        ResetOperationCancellation();
        try
        {
            await ShowPlanAsync(await viewModel.BuildAutomaticComponentPlanAsync(component, operationCancellation!.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (DeploymentConflictException exception)
        {
            await ShowErrorAsync("Installation blocked", exception.Message, exception.TechnicalDetails);
        }
        catch (ArtifactPipelineException exception)
        {
            await ShowErrorAsync("Could not build installation plan", exception.UserSummary,
                exception.TechnicalDetail ?? exception.ToString());
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not build installation plan", exception.Message);
        }
    }

    private async void UseLocalArtifact_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card } ||
            ViewModel is not { } viewModel || Host is not { } host) return;
        var patterns = card.Component == ComponentKind.RenoDx
            ? new[] { "*.addon64", "*.addon32" }
            : new[] { "*.dll" };
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = card.CanUseDownloadedArtifact
                ? "Choose the RenoDX addon downloaded from the official page"
                : $"Advanced troubleshooting: choose local {card.Name} file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType($"{card.Name} artifact") { Patterns = patterns }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            await ShowPlanAsync(await viewModel.BuildInstallPlanAsync(card.Component, path, "install"));
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not build plan", exception.Message);
        }
    }

    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel) await viewModel.CheckForUpdatesAsync();
    }

    private async Task ShowPlanAsync(DeploymentPlan plan)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        if (!viewModel.IsPlanForCurrentSelection(plan))
        {
            await MessageDialog.ShowAsync(host, "Game selection changed",
                "The plan was discarded because a different game or installation folder is now selected. No files were changed.");
            return;
        }
        await DeploymentPlanApproval.SealAsync(plan, operationCancellation?.Token ?? default);
        viewModel.AttachRecommendedPlanSummary(plan);
        await new DeploymentPlanDialog(viewModel, plan).ShowDialog(host);
    }

    private async void CopyLaunch_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(viewModel.LaunchOption);
        if (this.FindControl<Button>("CopyLaunchButton") is not { } button) return;
        button.Content = "Copied";
        if (this.FindControl<TextBlock>("CopyFeedback") is { } feedback) feedback.IsVisible = true;
        await Task.Delay(1800);
        if (!IsVisible) return;
        button.Content = "Copy";
        if (this.FindControl<TextBlock>("CopyFeedback") is { } resetFeedback) resetFeedback.IsVisible = false;
    }

    private void ResetOperationCancellation()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new();
    }

    private async Task<IStorageFolder?> TryFolderAsync(string path)
    {
        if (Host is not { } host) return null;
        try
        {
            return await host.StorageProvider.TryGetFolderFromPathAsync(new Uri(path));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private async Task ShowErrorAsync(string title, string message, string? detail = null)
    {
        if (Host is not { } host) return;
        if (detail is null) await MessageDialog.ShowAsync(host, title, message);
        else await MessageDialog.ShowAsync(host, title, message, detail);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = null;
        base.OnDetachedFromVisualTree(eventArgs);
    }
}
