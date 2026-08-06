namespace RhiLinux.Core;

public sealed class XdgPaths
{
    public XdgPaths(string? home = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        environment ??= new EnvironmentDictionary();
        ConfigDirectory = Resolve(environment, "XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        DataDirectory = Resolve(environment, "XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
        CacheDirectory = Resolve(environment, "XDG_CACHE_HOME", Path.Combine(home, ".cache"));
    }

    public string ConfigDirectory { get; }
    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string AppConfigDirectory => Path.Combine(ConfigDirectory, "rhi-linux");
    public string AppDataDirectory => Path.Combine(DataDirectory, "rhi-linux");
    public string AppCacheDirectory => Path.Combine(CacheDirectory, "rhi-linux");
    public string StateFile => Path.Combine(AppDataDirectory, "state.json");
    public string LibraryIndexFile => Path.Combine(AppDataDirectory, "library-index.json");
    public string SourceIndexFile => Path.Combine(AppDataDirectory, "source-index.json");
    public string LibraryDatabaseFile => Path.Combine(AppDataDirectory, "library.db");
    public string LogsDirectory => Path.Combine(AppDataDirectory, "logs");

    private static string Resolve(IReadOnlyDictionary<string, string?> env, string key, string fallback) =>
        env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(value))
            : Path.GetFullPath(fallback);

    private sealed class EnvironmentDictionary : IReadOnlyDictionary<string, string?>
    {
        public string? this[string key] => Environment.GetEnvironmentVariable(key);
        public IEnumerable<string> Keys => throw new NotSupportedException();
        public IEnumerable<string?> Values => throw new NotSupportedException();
        public int Count => 0;
        public bool ContainsKey(string key) => Environment.GetEnvironmentVariable(key) is not null;
        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => throw new NotSupportedException();
        public bool TryGetValue(string key, out string? value) { value = Environment.GetEnvironmentVariable(key); return value is not null; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
