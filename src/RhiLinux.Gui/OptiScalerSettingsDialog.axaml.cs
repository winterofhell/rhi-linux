using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RhiLinux.Mods;

namespace RhiLinux.Gui;

public sealed partial class OptiScalerSettingsDialog : Window
{
    public OptiScalerSettingsDialog() => AvaloniaXamlLoader.Load(this);

    public OptiScalerSettingsDialog(OptiScalerSettings settings) : this() =>
        DataContext = new OptiScalerSettingsViewModel(settings);

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close();

    private void Review_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is OptiScalerSettingsViewModel viewModel) Close(viewModel.ToSettings());
    }
}
