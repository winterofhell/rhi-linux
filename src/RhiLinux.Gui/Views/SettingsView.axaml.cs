using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using RhiLinux.Core;

namespace RhiLinux.Gui.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? ViewModel => DataContext as MainViewModel;
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    private async void Refresh_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel) await viewModel.RefreshAsync();
    }

    private async void OpenCacheFolder_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel) return;
        try
        {
            viewModel.OpenCacheFolder();
        }
        catch (Exception exception)
        {
            if (Host is { } host) await MessageDialog.ShowAsync(host, "Could not open cache folder", exception.Message);
        }
    }

    private async void VerifyCache_Click(object? sender, RoutedEventArgs eventArgs) =>
        await RunAsync(viewModel => viewModel.VerifyCacheAsync(), "Cache verification failed");

    private async void RefreshRenoDxCatalog_Click(object? sender, RoutedEventArgs eventArgs) =>
        await RunAsync(viewModel => viewModel.RefreshRenoDxCatalogAsync(), "RenoDX catalog refresh failed");

    private async void ClearCache_Click(object? sender, RoutedEventArgs eventArgs) =>
        await RunAsync(viewModel => viewModel.ClearUnusedCacheAsync(), "Cache cleanup failed");

    private async void ResetSettings_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel) return;
        viewModel.ResetSettingsToDefaults();
        if (Application.Current is { } application) application.RequestedThemeVariant = ThemeVariant.Default;
        if (Host is { } host)
            await MessageDialog.ShowAsync(host, "Settings reset",
                "Defaults were restored for cache limit, motion, automatic update checks, and the additional Steam library path.");
    }

    private async void ExportProfiles_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        var file = await host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export portable game profiles",
            SuggestedFileName = "rhi-linux-profiles.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON profile file") { Patterns = ["*.json"] }]
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        await RunAsync(model => model.ExportProfilesAsync(path), "Profile export failed");
    }

    private async void ImportProfiles_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import portable game profiles",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON profile file") { Patterns = ["*.json"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var preview = await viewModel.PreviewProfileImportAsync(path);
            if (!preview.IsValid)
            {
                await MessageDialog.ShowAsync(host, "Profile file could not be imported", string.Join('\n', preview.Errors));
                return;
            }
            var summary = string.Join('\n', preview.Entries.Select(entry =>
                $"{entry.Profile.DisplayName}: {entry.State} — {entry.Explanation}"));
            if (!await MessageDialog.ConfirmAsync(host, "Review profile import", summary, "Import matched profiles")) return;
            var approvals = preview.Entries
                .Where(entry => entry.State == ProfileImportMatchState.LikelyMatch && entry.MatchedGame is not null)
                .Select(entry => new ProfileImportApproval(entry.Profile.Id, entry.MatchedGame!.InstallId))
                .ToArray();
            await viewModel.ApplyProfileImportAsync(preview, approvals);
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowAsync(host, "Profile import failed", exception.Message);
        }
    }

    private async void RunSetupAgain_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        var result = await new FirstRunDialog(viewModel).ShowDialog<FirstRunResult>(host);
        if (result is not null) await viewModel.CompleteOnboardingAsync(result.ProviderPreferences);
    }

    private async void RebuildLibraryCache_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel || Host is not { } host) return;
        if (!await MessageDialog.ConfirmAsync(host, "Rebuild library cache",
                "Rebuild discovery and analysis caches? Profiles, backups, operation history, ownership state, installed mods, and game files will not be deleted.",
                "Rebuild cache")) return;
        await RunAsync(model => model.RebuildLibraryCacheAsync(true), "Library cache rebuild failed");
    }

    private async Task RunAsync(Func<MainViewModel, Task> operation, string title)
    {
        if (ViewModel is not { } viewModel) return;
        try
        {
            await operation(viewModel);
        }
        catch (Exception exception)
        {
            if (Host is { } host) await MessageDialog.ShowAsync(host, title, exception.Message);
        }
    }
}
