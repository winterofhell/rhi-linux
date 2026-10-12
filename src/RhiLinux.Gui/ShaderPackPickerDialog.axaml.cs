using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace RhiLinux.Gui;

public sealed partial class ShaderPackPickerDialog : Window
{
    private readonly ShaderPackPickerViewModel model;

    public ShaderPackPickerDialog() : this([]) { }

    public ShaderPackPickerDialog(IReadOnlyCollection<string> installedIds)
    {
        model = new ShaderPackPickerViewModel(installedIds);
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
    }

    private void Recommended_Click(object? sender, RoutedEventArgs eventArgs) => model.ApplyRecommended();

    private void Clear_Click(object? sender, RoutedEventArgs eventArgs) => model.Clear();

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void Apply_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (model.CanApply) Close(model.SelectedIds);
    }
}
