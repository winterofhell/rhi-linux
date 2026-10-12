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
    private bool configurationDialogOpen;

    public GameDetailsView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? ViewModel => DataContext as MainViewModel;
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    public void CancelPendingOperation() => operationCancellation?.Cancel();

    private async void ConfigureOptiScaler_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (configurationDialogOpen || ViewModel is not { } viewModel || Host is not { } host) return;
        configurationDialogOpen = true;
        var selectedId = viewModel.SelectedGame?.EffectiveInstallId;
        ResetOperationCancellation();
        try
        {
            var current = await viewModel.ReadOptiScalerSettingsAsync(operationCancellation!.Token);
            var settings = await new OptiScalerSettingsDialog(current).ShowDialog<OptiScalerSettings?>(host);
            if (settings is null || viewModel.SelectedGame?.EffectiveInstallId != selectedId) return;
            await ShowPlanAsync(await viewModel.BuildOptiScalerSettingsPlanAsync(settings, operationCancellation.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await ShowErrorAsync("Could not configure OptiScaler", exception.Message); }
        finally { configurationDialogOpen = false; }
    }

    private async void ImportPreset_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        var selectedId = viewModel.SelectedGame?.EffectiveInstallId;
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a ReShade preset",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ReShade preset") { Patterns = ["*.ini"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } source ||
            viewModel.SelectedGame?.EffectiveInstallId != selectedId) return;
        await BuildPresetPlanAsync(source);
    }

    private async void ResetPreset_Click(object? sender, RoutedEventArgs eventArgs) => await BuildPresetPlanAsync(null);

    private async Task BuildPresetPlanAsync(string? source)
    {
        if (ViewModel is not { } viewModel) return;
        ResetOperationCancellation();
        try
        {
            await ShowPlanAsync(await viewModel.BuildReShadePresetPlanAsync(source, operationCancellation!.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await ShowErrorAsync("Could not prepare ReShade preset", exception.Message); }
    }

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

    private async void EnhancementAction_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if ((sender as Control)?.DataContext is not EnhancementCardViewModel { Kind: { } kind } card ||
            ViewModel is not { } viewModel || Host is not { } host) return;
        if (card.IsShaderCard)
        {
            var selected = await new ShaderPackPickerDialog(viewModel.InstalledShaderPackIds).ShowDialog<IReadOnlyList<string>?>(host);
            if (selected is null) return;
            await ShowEnhancementPlanAsync(selected.Count == 0 ? "Remove shader packs" : "Install shader packs",
                (progress, token) => viewModel.BuildShaderPackPlanAsync(selected, progress, token));
            return;
        }
        await ShowEnhancementPlanAsync($"{card.ActionText} {card.Name}",
            (_, token) => viewModel.BuildEnhancementInstallPlanAsync(kind, token));
    }

    private async void EnhancementRemove_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if ((sender as Control)?.DataContext is not EnhancementCardViewModel { Kind: { } kind } card || ViewModel is not { } viewModel) return;
        if (card.IsShaderCard)
            await ShowEnhancementPlanAsync("Remove shader packs", (progress, token) => viewModel.BuildShaderPackPlanAsync([], progress, token));
        else
            await ShowEnhancementPlanAsync($"Remove {card.Name}", (_, token) => viewModel.BuildEnhancementRemovePlanAsync(kind, token));
    }

    private async void EnhancementRemoveAll_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel) return;
        await ShowEnhancementPlanAsync("Remove enhancements", (_, token) => viewModel.BuildRemoveAllEnhancementsPlanAsync(token));
    }

    private async void EnhancementRecover_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel) return;
        ResetOperationCancellation();
        try
        {
            if (!await viewModel.RecoverEnhancementsAsync(operationCancellation!.Token))
                await ShowErrorAsync("Recovery incomplete", "Some files could not be restored. Check the diagnostics page for details.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            await ShowErrorAsync("Recovery failed", exception.Message);
        }
    }

    private async Task ShowEnhancementPlanAsync(string title, Func<IProgress<string>, CancellationToken, Task<EnhancementPlan>> build)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        await new EnhancementPlanDialog(viewModel, title, build).ShowDialog(host);
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
