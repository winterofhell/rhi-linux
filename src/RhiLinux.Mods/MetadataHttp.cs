namespace RhiLinux.Mods;

public static class MetadataHttp
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan UpdateCheckBudget = TimeSpan.FromSeconds(12);

    public static HttpClient CreateClient() => new()
    {
        Timeout = RequestTimeout
    };
}
