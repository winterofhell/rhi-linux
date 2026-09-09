using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace RhiLinux.Gui.Views;

public sealed partial class UpdatesView : UserControl
{
    public UpdatesView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel) await viewModel.CheckForUpdatesAsync();
    }

    private void OpenLibrary_Click(object? sender, RoutedEventArgs eventArgs) =>
        ViewModel?.Navigate(MainSection.Library);

    private void ReviewComponentUpdate_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string installId }) ViewModel?.OpenReadinessForInstall(installId);
    }

}
