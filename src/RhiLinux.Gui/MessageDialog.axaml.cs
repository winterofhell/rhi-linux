using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace RhiLinux.Gui;

public sealed partial class MessageDialog : Window
{
    public MessageDialog() => AvaloniaXamlLoader.Load(this);

    private MessageDialog(string title, string message, string? confirmText, string? technicalDetails = null) : this()
    {
        Title = title;
        (this.FindControl<TextBlock>("TitleText") ?? throw new InvalidOperationException("Dialog title was not loaded.")).Text = title;
        (this.FindControl<TextBlock>("MessageText") ?? throw new InvalidOperationException("Dialog message was not loaded.")).Text = message;
        (this.FindControl<Button>("CancelButton") ?? throw new InvalidOperationException("Dialog cancel button was not loaded.")).IsVisible = confirmText is not null;
        (this.FindControl<Button>("ConfirmButton") ?? throw new InvalidOperationException("Dialog confirm button was not loaded.")).Content = confirmText ?? "Close";
        var details = this.FindControl<Expander>("TechnicalDetails") ?? throw new InvalidOperationException("Dialog details were not loaded.");
        details.IsVisible = !string.IsNullOrWhiteSpace(technicalDetails);
        (this.FindControl<TextBlock>("TechnicalDetailsText") ?? throw new InvalidOperationException("Dialog details text was not loaded.")).Text = technicalDetails;
    }

    public static Task ShowAsync(Window owner, string title, string message) => new MessageDialog(title, message, null).ShowDialog(owner);
    public static Task ShowAsync(Window owner, string title, string message, string technicalDetails) =>
        new MessageDialog(title, message, null, technicalDetails).ShowDialog(owner);
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText) => new MessageDialog(title, message, confirmText).ShowDialog<bool>(owner);
    private void Confirm_Click(object? sender, RoutedEventArgs eventArgs) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs) => Close(false);
}
