using Avalonia;

namespace RhiLinux.Gui;

internal static class Program
{
    public static IReadOnlyList<string>? SteamRoots { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        SteamRoots = ParseSteamRoots(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static IReadOnlyList<string>? ParseSteamRoots(string[] args)
    {
        var roots = new List<string>();
        for (var index = 0; index < args.Length; index++)
            if (args[index] == "--steam-root" && index + 1 < args.Length)
                roots.Add(Path.GetFullPath(args[++index]));
        return roots.Count == 0 ? null : roots;
    }
}
