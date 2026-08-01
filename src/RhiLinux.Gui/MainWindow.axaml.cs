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

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        selectionChangeCancellation?.Cancel();
        selectionChangeCancellation?.Dispose();
        selectionChangeCancellation = null;
        operationCancellation?.Cancel();
        viewModel.Preferences.WindowWidth = Width;
        viewModel.Preferences.WindowHeight = Height;
        viewModel.Preferences.SidebarWidth = Workspace.ColumnDefinitions[0].ActualWidth;
        viewModel.Preferences.SelectedAppId = viewModel.SelectedGame?.AppId;
        viewModel.SavePreferencesAsync().GetAwaiter().GetResult();
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
            if (this.FindControl<Expander>("CompatibilityDetails") is { } compatibilityDetails)
                compatibilityDetails.IsExpanded = false;
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
        if (card.ActionText == "Remove")
        {
            try { await ShowPlanAsync(await viewModel.BuildRemovePlanAsync(card.Component)); }
            catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build removal plan", exception.Message); }
            return;
        }
        await BuildAutomaticComponentPlanAsync(card.Component);
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
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not build recommended setup", exception.Message); }
    }

    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs eventArgs) => await viewModel.CheckForUpdatesAsync();
    private async void VerifyCache_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try { await viewModel.VerifyCacheAsync(); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Cache verification failed", exception.Message); }
    }
    private async void ClearCache_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try { await viewModel.ClearUnusedCacheAsync(); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Cache cleanup failed", exception.Message); }
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
