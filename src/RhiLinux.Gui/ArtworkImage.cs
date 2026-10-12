using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RhiLinux.Core;
using RhiLinux.Steam;

namespace RhiLinux.Gui;

public static class ArtworkImage
{
    private const int CacheLimit = 384;
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> CacheOrder = new();
    private static readonly SemaphoreSlim DecodeGate = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    public static readonly AttachedProperty<InstalledGame?> GameProperty =
        AvaloniaProperty.RegisterAttached<Image, InstalledGame?>("Game", typeof(ArtworkImage));

    public static readonly AttachedProperty<SteamArtworkKind> KindProperty =
        AvaloniaProperty.RegisterAttached<Image, SteamArtworkKind>("Kind", typeof(ArtworkImage), SteamArtworkKind.Header);

    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("DecodeWidth", typeof(ArtworkImage), 320);

    public static readonly AttachedProperty<bool> HasArtworkProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("HasArtwork", typeof(ArtworkImage));

    static ArtworkImage()
    {
        GameProperty.Changed.AddClassHandler<Image>((image, _) => Load(image));
        KindProperty.Changed.AddClassHandler<Image>((image, _) => Load(image));
    }

    public static InstalledGame? GetGame(Image image) => image.GetValue(GameProperty);
    public static void SetGame(Image image, InstalledGame? value) => image.SetValue(GameProperty, value);
    public static SteamArtworkKind GetKind(Image image) => image.GetValue(KindProperty);
    public static void SetKind(Image image, SteamArtworkKind value) => image.SetValue(KindProperty, value);
    public static int GetDecodeWidth(Image image) => image.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);
    public static bool GetHasArtwork(Image image) => image.GetValue(HasArtworkProperty);
    public static void SetHasArtwork(Image image, bool value) => image.SetValue(HasArtworkProperty, value);

    private static void Load(Image image)
    {
        var game = GetGame(image);
        var kind = GetKind(image);
        var width = Math.Clamp(GetDecodeWidth(image), 32, 2560);
        if (game is not { SteamAppId: { } appId and > 0 })
        {
            Apply(image, null);
            return;
        }
        var steamRoot = game.SteamRoot;
        var key = $"{steamRoot}|{appId}|{kind}|{width}";
        if (Cache.TryGetValue(key, out var cached))
        {
            Apply(image, cached);
            return;
        }
        image.Source = null;
        image.Opacity = 0;
        SetHasArtwork(image, false);
        _ = Task.Run(async () =>
        {
            var bitmap = await DecodeAsync(steamRoot, appId, kind, width);
            Remember(key, bitmap);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (GetGame(image) is { } current && current.SteamAppId == appId && GetKind(image) == kind)
                    Apply(image, bitmap);
            });
        });
    }

    private static async Task<Bitmap?> DecodeAsync(string? steamRoot, uint appId, SteamArtworkKind kind, int width)
    {
        await DecodeGate.WaitAsync();
        try
        {
            if (SteamArtworkLocator.Find(steamRoot, appId, kind) is not { } path) return null;
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new MemoryStream(bytes, false);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static void Remember(string key, Bitmap? bitmap)
    {
        if (!Cache.TryAdd(key, bitmap)) return;
        CacheOrder.Enqueue(key);
        while (Cache.Count > CacheLimit && CacheOrder.TryDequeue(out var oldest))
            Cache.TryRemove(oldest, out _);
    }

    private static void Apply(Image image, Bitmap? bitmap)
    {
        image.Source = bitmap;
        SetHasArtwork(image, bitmap is not null);
        image.Opacity = bitmap is null ? 0 : 1;
    }
}
