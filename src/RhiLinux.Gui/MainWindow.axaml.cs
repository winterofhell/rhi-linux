using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using RhiLinux.Core;
using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? selectionChangeCancellation;
    private bool initialized;
    private bool closeRequested;
    private Grid Workspace => this.FindControl<Grid>("WorkspaceGrid") ?? throw new InvalidOperationException("Workspace grid was not loaded.");
    private ComboBox ThemeBox => this.FindControl<ComboBox>("ThemeSelector") ?? throw new InvalidOperationException("Theme selector was not loaded.");
    private ListBox GamesList => this.FindControl<ListBox>("GameList") ?? throw new InvalidOperationException("Game list was not loaded.");
    private Button CopyButton => this.FindControl<Button>("CopyLaunchButton") ?? throw new InvalidOperationException("Copy button was not loaded.");
    private TextBlock CopyStatus => this.FindControl<TextBlock>("CopyFeedback") ?? throw new InvalidOperationException("Copy feedback was not loaded.");
    private TextBox SearchInput => this.FindControl<TextBox>("SearchBox") ?? throw new InvalidOperationException("Search box was not loaded.");

    public MainWindow() : this(new MainViewModel()) { }

    internal MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        operationCancellation = new CancellationTokenSource();
        await viewModel.InitializeAsync(operationCancellation.Token);
        Width = Math.Clamp(viewModel.Preferences.WindowWidth, MinWidth, 3840);
        Height = Math.Clamp(viewModel.Preferences.WindowHeight, MinHeight, 2160);
        Workspace.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(viewModel.Preferences.SidebarWidth, 240, 520));
        ThemeBox.SelectedIndex = viewModel.Preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ApplyTheme(viewModel.Preferences.Theme);
        GamesList.SelectedItem = viewModel.SelectedGame;
        initialized = true;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (closeRequested) return;

        eventArgs.Cancel = true;
        closeRequested = true;

        selectionChangeCancellation?.Cancel();
        selectionChangeCancellation?.Dispose();
        selectionChangeCancellation = null;
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = null;
        viewModel.CancelBackgroundWork();

        viewModel.Preferences.WindowWidth = Width;
        viewModel.Preferences.WindowHeight = Height;
        viewModel.Preferences.SidebarWidth = Workspace.ColumnDefinitions[0].ActualWidth;
        viewModel.Preferences.SelectedAppId = viewModel.SelectedGame?.AppId;

        try
        {
            using var saveCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await viewModel.SavePreferencesAsync(saveCancellation.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
        finally
        {
            Closing -= OnClosing;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                ReferenceEquals(desktop.MainWindow, this))
            {
                desktop.Shutdown();
            }
            else
            {
                Close();
            }
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs eventArgs)
    {
        await RefreshViewAsync();
    }

    private async Task RefreshViewAsync()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        await viewModel.RefreshAsync(operationCancellation.Token);
        GamesList.SelectedItem = viewModel.SelectedGame;
    }

    private void GameList_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!initialized || sender is not ListBox { SelectedItem: SteamGame game } ||
            DataContext is not MainViewModel currentViewModel || game.AppId == currentViewModel.SelectedGame?.AppId) return;

        selectionChangeCancellation?.Cancel();
        selectionChangeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        selectionChangeCancellation = cancellation;
        _ = ChangeSelectedGameAsync(currentViewModel, game, cancellation);
    }

    private async Task ChangeSelectedGameAsync(
        MainViewModel currentViewModel,
        SteamGame game,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            if (this.FindControl<ScrollViewer>("GameContentScroll") is { } contentScroll)
                contentScroll.Offset = default;
            ResetWorkspaceExpanders();
            currentViewModel.CollapseComponentDetails();
            await currentViewModel.SelectAsync(game, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            try
            {
                if (IsVisible)
                    await MessageDialog.ShowAsync(this, "Could not select game", exception.Message);
            }
            catch (Exception dialogException)
            {
                System.Diagnostics.Debug.WriteLine(dialogException);
            }
        }
        finally
        {
            if (ReferenceEquals(selectionChangeCancellation, cancellation))
            {
                selectionChangeCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void ThemeSelector_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem item || item.Content is not string theme) return;
        viewModel.Preferences.Theme = theme;
        ApplyTheme(theme);
        _ = viewModel.SavePreferencesAsync();
    }

    private void ResetWorkspaceExpanders()
    {
        foreach (var name in new[] { "CompatibilityDetails", "AdvancedGameDetails", "AdvancedExecutableDetails", "ApplicationSettings" })
            if (this.FindControl<Expander>(name) is { } expander)
                expander.IsExpanded = false;
    }

    private static void ApplyTheme(string theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private async void ChangeExecutable_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedGame is not { } game) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the primary Windows game executable",
            AllowMultiple = false,
            SuggestedStartLocation = await TryFolderAsync(game.DeploymentDirectory),
            FileTypeFilter = [new FilePickerFileType("Windows executable") { Patterns = ["*.exe"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await viewModel.SaveOverridesAsync(path, game.DeploymentDirectory);
        GamesList.SelectedItem = viewModel.SelectedGame;
    }

    private async void ChangeDeploymentFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedGame is not { } game) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the game folder for compatibility files",
            AllowMultiple = false,
            SuggestedStartLocation = await TryFolderAsync(game.DeploymentDirectory)
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await viewModel.SaveOverridesAsync(game.Executable, path);
        GamesList.SelectedItem = viewModel.SelectedGame;
    }

    private async void OpenExecutableFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (viewModel.SelectedGame?.Executable is not { } executable) return;
        var directory = Path.GetDirectoryName(executable);
        if (directory is not null) await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory));
    }

    private async void ComponentAction_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card }) return;
        if (card.ActionText == "Remove" || card.ActionText.StartsWith("Remove ", StringComparison.Ordinal))
        {
            try { await ShowPlanAsync(await viewModel.BuildRemovePlanAsync(card.Component)); }
            catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build removal plan", exception.Message); }
            return;
        }
        await BuildAutomaticComponentPlanAsync(card.Component);
    }

    private async void ComponentRemove_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card }) return;
        try { await ShowPlanAsync(await viewModel.BuildRemovePlanAsync(card.Component)); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build removal plan", exception.Message); }
    }

    private async void ComponentSecondary_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card }) return;
        if (card.CanShowFiles)
        {
            await MessageDialog.ShowAsync(this, $"{card.Name} files",
                card.Files.Length == 0 ? "No detected files." : card.Files);
            return;
        }
        if (card.CanCheckAgain)
            await viewModel.CheckForUpdatesAsync(false);
    }

    private void OpenOfficialPage_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card }) return;
        viewModel.OpenOfficialPage(card);
    }

    private async Task BuildAutomaticComponentPlanAsync(ComponentKind component)
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        try
        {
            await ShowPlanAsync(await viewModel.BuildAutomaticComponentPlanAsync(
                component, operationCancellation.Token));
        }
        catch (OperationCanceledException) { }
        catch (DeploymentConflictException exception)
        {
            await MessageDialog.ShowAsync(this, "Installation blocked", exception.Message, exception.TechnicalDetails);
        }
        catch (ArtifactPipelineException exception)
        {
            await MessageDialog.ShowAsync(this, "Could not build installation plan", exception.UserSummary,
                exception.TechnicalDetail ?? exception.ToString());
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowAsync(this, "Could not build installation plan", exception.Message);
        }
    }

    private async Task BuildAutomaticPlanAsync()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        try { await ShowPlanAsync(await viewModel.BuildRecommendedStackPlanAsync(operationCancellation.Token)); }
        catch (OperationCanceledException) { }
        catch (DeploymentConflictException exception)
        {
            await MessageDialog.ShowAsync(this, "Installation blocked", exception.Message, exception.TechnicalDetails);
        }
        catch (ArtifactPipelineException exception)
        {
            await MessageDialog.ShowAsync(this, "Could not build installation plan", exception.UserSummary,
                exception.TechnicalDetail ?? exception.ToString());
        }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build installation plan", exception.Message); }
    }

    private async void UseLocalArtifact_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ComponentCardViewModel card }) return;
        var patterns = card.Component switch
        {
            ComponentKind.RenoDx => new[] { "*.addon64", "*.addon32" },
            _ => new[] { "*.dll" }
        };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Advanced troubleshooting: choose local {card.Name} file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType($"{card.Name} artifact") { Patterns = patterns }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var plan = await viewModel.BuildInstallPlanAsync(card.Component, path, "install");
            await ShowPlanAsync(plan);
        }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build plan", exception.Message); }
    }

    private async void Restore_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try { await ShowPlanAsync(await viewModel.BuildRestorePlanAsync()); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build restore plan", exception.Message); }
    }

    private async void InstallRecommended_Click(object? sender, RoutedEventArgs eventArgs)
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        try { await ShowPlanAsync(await viewModel.BuildPrimaryActionPlanAsync(operationCancellation.Token)); }
        catch (OperationCanceledException) { }
        catch (DeploymentConflictException exception)
        {
            await MessageDialog.ShowAsync(this, "Installation blocked", exception.Message, exception.TechnicalDetails);
        }
        catch (ArtifactPipelineException exception)
        {
            await MessageDialog.ShowAsync(this, "Could not build recommended setup", exception.UserSummary,
                exception.TechnicalDetail ?? exception.ToString());
        }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build recommended setup", exception.Message); }
    }

    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs eventArgs) => await viewModel.CheckForUpdatesAsync();
    private void OpenCacheFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try { viewModel.OpenCacheFolder(); }
        catch (Exception exception) { _ = MessageDialog.ShowAsync(this, "Could not open cache folder", exception.Message); }
    }
    private async void VerifyCache_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try { await viewModel.VerifyCacheAsync(); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Cache verification failed", exception.Message); }
    }
    private async void RefreshRenoDxCatalog_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try { await viewModel.RefreshRenoDxCatalogAsync(); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "RenoDX catalog refresh failed", exception.Message); }
    }
    private async void ClearCache_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try { await viewModel.ClearUnusedCacheAsync(); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Cache cleanup failed", exception.Message); }
    }

    private async void ResetSettings_Click(object? sender, RoutedEventArgs eventArgs)
    {
        viewModel.ResetSettingsToDefaults();
        ThemeBox.SelectedIndex = 0;
        ApplyTheme("System");
        await MessageDialog.ShowAsync(this, "Settings reset",
            "Defaults were restored for cache limit, motion, automatic update checks, and the additional Steam library path.");
    }

    private async Task ShowPlanAsync(DeploymentPlan plan)
    {
        if (!viewModel.IsPlanForCurrentSelection(plan))
        {
            await MessageDialog.ShowAsync(this, "Game selection changed",
                "The plan was discarded because a different game or installation folder is now selected. No files were changed.");
            return;
        }
        var dialog = new DeploymentPlanDialog(viewModel, plan);
        await dialog.ShowDialog(this);
    }

    private async void CopyLaunch_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(viewModel.LaunchOption);
        CopyButton.Content = "Copied";
        CopyStatus.IsVisible = true;
        await Task.Delay(1800);
        if (!IsVisible) return;
        CopyButton.Content = "Copy";
        CopyStatus.IsVisible = false;
    }

    private async void CopyHdrLaunch_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(viewModel.HdrLaunchOption);
        if (this.FindControl<Button>("CopyHdrLaunchButton") is { } button)
        {
            button.Content = "Copied";
            await Task.Delay(1800);
            if (!IsVisible) return;
            button.Content = "Copy";
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => operationCancellation?.Cancel();
    private void DismissError_Click(object? sender, RoutedEventArgs eventArgs) => viewModel.DismissError();

    private async void Window_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.F5)
        {
            eventArgs.Handled = true;
            await RefreshViewAsync();
        }
        else if (eventArgs.Key == Key.L && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            eventArgs.Handled = true;
            SearchInput.Focus();
            SearchInput.SelectAll();
        }
        else if (eventArgs.Key == Key.R && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) && viewModel.SelectedGame is not null)
        {
            eventArgs.Handled = true;
            await ShowPlanAsync(await viewModel.BuildRestorePlanAsync());
        }
    }

    private async Task<IStorageFolder?> TryFolderAsync(string path)
    {
        try { return await StorageProvider.TryGetFolderFromPathAsync(new Uri(path)); }
        catch (UriFormatException) { return null; }
    }
}
