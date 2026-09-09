using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RhiLinux.Core;

namespace RhiLinux.Gui.Views;

public sealed partial class OverviewView : UserControl
{
    public OverviewView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OpenReadyGames_Click(object? sender, RoutedEventArgs eventArgs) =>
        ViewModel?.FilterLibraryForReadiness("Ready");

    private void OpenUpdates_Click(object? sender, RoutedEventArgs eventArgs) =>
        ViewModel?.Navigate(MainSection.Updates);

    private void OpenDiagnostics_Click(object? sender, RoutedEventArgs eventArgs) =>
        ViewModel?.Navigate(MainSection.Diagnostics);

    private async void ViewActivityResult_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ActivityHistoryEntry entry } ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await MessageDialog.ShowAsync(owner, entry.Action,
            entry.Summary ?? $"{entry.Outcome} · {entry.FilesChanged} files changed");
    }

    private async void ViewActivityFiles_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: ActivityHistoryEntry entry } ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        var files = entry.Files.Count == 0 ? "No file paths were recorded." : string.Join('\n', entry.Files);
        await MessageDialog.ShowAsync(owner, $"{entry.GameName} files", files);
    }

    private async void CopyActivityLaunch_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: ActivityHistoryEntry { LaunchConfiguration: { Length: > 0 } launch } } &&
            TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(launch);
    }
}
