using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace RhiLinux.Gui;

public sealed record FirstRunResult(bool AddManualGame, IReadOnlyDictionary<string, bool> ProviderPreferences);

public sealed partial class FirstRunDialog : Window
{
    public FirstRunDialog() => AvaloniaXamlLoader.Load(this);

    public FirstRunDialog(MainViewModel viewModel) : this() => DataContext = new FirstRunViewModel(viewModel);

    private void Continue_Click(object? sender, RoutedEventArgs eventArgs) => Close(Result(false));
    private void AddManual_Click(object? sender, RoutedEventArgs eventArgs) => Close(Result(true));

    private FirstRunResult Result(bool manual)
    {
        var model = (FirstRunViewModel)DataContext!;
        return new(manual, model.Sources.ToDictionary(item => item.Id, item => item.Enabled, StringComparer.Ordinal));
    }
}

public sealed class FirstRunSourceRow : INotifyPropertyChanged
{
    private bool enabled;

    public FirstRunSourceRow(string id, string name, bool enabled, bool detected, int gameCount)
    {
        Id = id;
        Name = name;
        this.enabled = enabled;
        Detected = detected;
        GameCount = gameCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Name { get; }
    public bool Detected { get; }
    public int GameCount { get; }
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        }
    }
    public string Status => Detected ? "Detected" : "Not detected";
    public string GameCountText => Detected ? $"{GameCount} games" : string.Empty;
    public string EnableAutomationName => $"Enable {Name} source";
}

public sealed class FirstRunViewModel
{
    public FirstRunViewModel(MainViewModel viewModel)
    {
        var providers = viewModel.ProviderStatuses.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        Sources = new[]
        {
            Row("steam", "Steam", true),
            Row("heroic", "Heroic", viewModel.Preferences.EnableHeroic),
            Row("legendary", "Legendary", viewModel.Preferences.EnableLegendary),
            Row("lutris", "Lutris", viewModel.Preferences.EnableLutris),
            Row("bottles", "Bottles", viewModel.Preferences.EnableBottles),
            Row("minigalaxy", "Minigalaxy", viewModel.Preferences.EnableMinigalaxy),
            Row("manual", "Manual", true)
        };
        FirstRunSourceRow Row(string id, string name, bool enabled)
        {
            providers.TryGetValue(id, out var status);
            var count = viewModel.Games.Count(game => game.Sources.Any(source => source.ProviderId == id) ||
                game.PrimaryLauncher.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
            return new(id, name, enabled, status?.IsDetected == true || count > 0, count);
        }
    }

    public IReadOnlyList<FirstRunSourceRow> Sources { get; }
    public bool NothingDetected => Sources.All(item => !item.Detected);
}
