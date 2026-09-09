using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace RhiLinux.Gui;

public sealed partial class ManualGameWizardDialog : Window
{
    private readonly ManualGameWizardViewModel viewModel;

    public ManualGameWizardDialog() : this(new ManualGameWizardViewModel(
        new RhiLinux.Sources.ManualGameWizardService(new RhiLinux.Core.XdgPaths().ManualGamesFile,
            detectComponents: (target, token) => new RhiLinux.Mods.ComponentDetector().DetectAsync(target, token))))
    { }

    public ManualGameWizardDialog(ManualGameWizardViewModel viewModel)
    {
        this.viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
    }

    private async void ChooseDirectory_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose game install directory",
            AllowMultiple = false
        });
        if (selected.Count == 0) return;
        try { await viewModel.ScanAsync(selected[0].Path.LocalPath); }
        catch (Exception exception) { await MessageDialog.ShowAsync(this, "Could not scan directory", exception.Message); }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(false);

    private async void Save_Click(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await viewModel.SaveAsync();
            Close(true);
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowAsync(this, "Could not save game", exception.Message);
        }
    }
}
