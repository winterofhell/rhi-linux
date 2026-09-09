using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using RhiLinux.Core;

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
    private TextBox SearchInput => this.FindControl<TextBox>("SearchBox") ?? throw new InvalidOperationException("Search box was not loaded.");
    private Views.GameDetailsView Details => this.FindControl<Views.GameDetailsView>("GameDetails") ?? throw new InvalidOperationException("Game details view was not loaded.");
    private Views.LibraryView LibraryControl => this.FindControl<Views.LibraryView>("Library") ?? throw new InvalidOperationException("Library view was not loaded.");

    public MainWindow() : this(new MainViewModel()) { }

    internal MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Opened += OnOpened;
        Closing += OnClosing;
        UpdateWorkspaceColumns();
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        operationCancellation = new CancellationTokenSource();
        await viewModel.InitializeAsync(operationCancellation.Token);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var maximumWidth = screen is null ? 3840 : screen.WorkingArea.Width / screen.Scaling;
        var maximumHeight = screen is null ? 2160 : screen.WorkingArea.Height / screen.Scaling;
        Width = Math.Clamp(viewModel.Preferences.WindowWidth, MinWidth, Math.Max(MinWidth, maximumWidth));
        Height = Math.Clamp(viewModel.Preferences.WindowHeight, MinHeight, Math.Max(MinHeight, maximumHeight));
        if (viewModel.Preferences.WindowMaximized) WindowState = WindowState.Maximized;
        UpdateWorkspaceColumns();
        ThemeBox.SelectedIndex = viewModel.Preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ApplyTheme(viewModel.Preferences.Theme);
        LibraryControl.SyncSelection(viewModel.SelectedGame);
        initialized = true;
        if (!viewModel.Preferences.OnboardingCompleted)
        {
            var result = await new FirstRunDialog(viewModel).ShowDialog<FirstRunResult>(this);
            result ??= new(false, new Dictionary<string, bool>(StringComparer.Ordinal));
            await viewModel.CompleteOnboardingAsync(result.ProviderPreferences);
            if (result.AddManualGame)
            {
                var saved = await new ManualGameWizardDialog(viewModel.ManualGameWizard).ShowDialog<bool>(this);
                if (saved) await RefreshViewAsync();
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MainViewModel.ShowLibraryPage) or nameof(MainViewModel.SelectedNavigation))
            UpdateWorkspaceColumns();
        if (eventArgs.PropertyName is nameof(MainViewModel.FilteredGames) or nameof(MainViewModel.SelectedGame))
            LibraryControl.SyncSelection(viewModel.SelectedGame);
    }

    private void UpdateWorkspaceColumns()
    {
        if (viewModel.ShowLibraryPage)
        {
            Workspace.ColumnDefinitions[1].Width = new GridLength(Math.Clamp(viewModel.Preferences.SidebarWidth, 240, 520));
            Workspace.ColumnDefinitions[2].Width = new GridLength(6);
        }
        else
        {
            Workspace.ColumnDefinitions[1].Width = new GridLength(0);
            Workspace.ColumnDefinitions[2].Width = new GridLength(0);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (closeRequested) return;

        eventArgs.Cancel = true;
        if (viewModel.HasActiveWriteOperation)
        {
            await MessageDialog.ShowAsync(this, "Operation in progress",
                "An operation is currently modifying game files. Wait for it to reach a safe state before closing.");
            return;
        }
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
        viewModel.Preferences.WindowMaximized = WindowState == WindowState.Maximized;
        if (viewModel.ShowLibraryPage && Workspace.ColumnDefinitions[1].ActualWidth > 0)
            viewModel.Preferences.SidebarWidth = Workspace.ColumnDefinitions[1].ActualWidth;
        viewModel.Preferences.SelectedAppId = viewModel.SelectedGame?.AppId;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;

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
            await viewModel.DisposeAsync();
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
        LibraryControl.SyncSelection(viewModel.SelectedGame);
    }

    private async void GameList_SelectionChanged(object? sender, Views.GameSelectionRequestedEventArgs eventArgs)
    {
        var game = eventArgs.Game;
        if (!initialized || DataContext is not MainViewModel currentViewModel ||
            string.Equals(game.EffectiveInstallId, currentViewModel.SelectedGame?.EffectiveInstallId, StringComparison.Ordinal))
            return;

        selectionChangeCancellation?.Cancel();
        selectionChangeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        selectionChangeCancellation = cancellation;
        await ChangeSelectedGameAsync(currentViewModel, game, cancellation);
    }

    private async Task ChangeSelectedGameAsync(
        MainViewModel currentViewModel,
        InstalledGame game,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            Details.ResetPresentation();
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

    private async void ThemeSelector_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem item || item.Content is not string theme) return;
        viewModel.Preferences.Theme = theme;
        ApplyTheme(theme);
        try { await viewModel.SavePreferencesAsync(); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not save theme", exception.Message); }
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

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => operationCancellation?.Cancel();
    private void DismissError_Click(object? sender, RoutedEventArgs eventArgs) => viewModel.DismissError();

    private async void Library_AddManualGameRequested(object? sender, EventArgs eventArgs)
    {
        var saved = await new ManualGameWizardDialog(viewModel.ManualGameWizard).ShowDialog<bool>(this);
        if (saved) await RefreshViewAsync();
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        var editingText = eventArgs.Source is TextBox;
        if (eventArgs.Key == Key.F5)
        {
            eventArgs.Handled = true;
            await RefreshViewAsync();
        }
        else if (eventArgs.Key == Key.F && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            eventArgs.Handled = true;
            SearchInput.Focus();
            SearchInput.SelectAll();
        }
        else if (!editingText && eventArgs.Key == Key.Enter && viewModel.ShowLibraryPage &&
                 viewModel.SelectedGame is { } selected)
        {
            eventArgs.Handled = true;
            viewModel.OpenReadinessForInstall(selected.EffectiveInstallId);
        }
        else if (!editingText && eventArgs.Key == Key.R && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                 !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            eventArgs.Handled = true;
            await RefreshViewAsync();
        }
    }


}
