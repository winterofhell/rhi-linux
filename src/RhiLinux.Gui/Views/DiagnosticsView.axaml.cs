using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace RhiLinux.Gui.Views;

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? ViewModel => DataContext as MainViewModel;
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    private async void CopyReport_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host?.Clipboard is not { } clipboard) return;
        var report = await viewModel.CreateTroubleshootingReportAsync();
        await clipboard.SetTextAsync(report);
        if (this.FindControl<TextBlock>("CopyFeedback") is not { } feedback) return;
        feedback.IsVisible = true;
        await Task.Delay(1800);
        if (IsVisible) feedback.IsVisible = false;
    }

    private async void SaveReport_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        var file = await host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save troubleshooting report",
            SuggestedFileName = "rhi-linux-troubleshooting.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text report") { Patterns = ["*.txt"] }]
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var report = await viewModel.CreateTroubleshootingReportAsync();
            await viewModel.SaveTroubleshootingReportAsync(path, report);
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowAsync(host, "Could not save troubleshooting report", exception.Message);
        }
    }

    private async void OpenLogs_Click(object? sender, RoutedEventArgs eventArgs) =>
        await OpenDirectoryAsync(new RhiLinux.Core.XdgPaths().LogsDirectory, "Could not open logs");

    private async void OpenState_Click(object? sender, RoutedEventArgs eventArgs) =>
        await OpenDirectoryAsync(new RhiLinux.Core.XdgPaths().AppDataDirectory, "Could not open state directory");

    private async Task OpenDirectoryAsync(string path, string title)
    {
        if (Host is not { } host) return;
        try
        {
            Directory.CreateDirectory(path);
            await host.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowAsync(host, title, exception.Message);
        }
    }
}
