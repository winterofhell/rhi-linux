using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using RhiLinux.Core;

namespace RhiLinux.Gui.Views;

public sealed class GameSelectionRequestedEventArgs(InstalledGame game) : EventArgs
{
    public InstalledGame Game { get; } = game;
}

public sealed partial class LibraryView : UserControl
{
    public LibraryView() => AvaloniaXamlLoader.Load(this);

    public event EventHandler<GameSelectionRequestedEventArgs>? GameSelectionRequested;
    public event EventHandler? AddManualGameRequested;
    private bool syncing;

    public void SyncSelection(InstalledGame? game)
    {
        if (this.FindControl<ListBox>("GameList") is not { } games) return;
        syncing = true;
        try
        {
            games.SelectedItem = game;
        }
        finally
        {
            syncing = false;
        }
    }

    private void GameList_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (syncing || sender is not ListBox games) return;
        var game = eventArgs.AddedItems.OfType<InstalledGame>().LastOrDefault() ?? games.SelectedItem as InstalledGame;
        if (game is not null)
            GameSelectionRequested?.Invoke(this, new(game));
    }

    private void AddGame_Click(object? sender, RoutedEventArgs eventArgs) =>
        AddManualGameRequested?.Invoke(this, EventArgs.Empty);

}
