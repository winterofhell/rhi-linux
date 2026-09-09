namespace RhiLinux.Core;

public sealed class XdgPaths
{
    public XdgPaths(string? home = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        environment ??= new EnvironmentDictionary();
        ConfigDirectory = Resolve(environment, "XDG_CONFIG_HOME", Path.Combine(home, ".config"), home);
        DataDirectory = Resolve(environment, "XDG_DATA_HOME", Path.Combine(home, ".local", "share"), home);
        CacheDirectory = Resolve(environment, "XDG_CACHE_HOME", Path.Combine(home, ".cache"), home);
    }

    public static string ExpandHome(string value, string home)
    {
        if (value.Equals("~", StringComparison.Ordinal)) return home;
        if (value.StartsWith("~/", StringComparison.Ordinal)) return Path.Combine(home, value[2..]);
        return Environment.ExpandEnvironmentVariables(value);
    }

    public static string? ReadBaseDirectory(
        IReadOnlyDictionary<string, string?> environment,
        string key,
        string home)
    {
        if (!environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return null;
        var expanded = ExpandHome(value.Trim(), home);
        return Path.IsPathRooted(expanded) ? expanded : null;
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
    public string GameProfilesFile => Path.Combine(AppDataDirectory, "game-profiles.json");
    public string ActivityHistoryFile => Path.Combine(AppDataDirectory, "activity-history.json");
    public string ManualGamesFile => Path.Combine(AppConfigDirectory, "manual-games.json");
    public string LogsDirectory => Path.Combine(AppDataDirectory, "logs");

    private static string Resolve(
        IReadOnlyDictionary<string, string?> env,
        string key,
        string fallback,
        string home) =>
        Path.GetFullPath(ReadBaseDirectory(env, key, home) ?? fallback);

    private sealed class EnvironmentDictionary : IReadOnlyDictionary<string, string?>
    {
        private static IEnumerable<KeyValuePair<string, string?>> Snapshot()
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key)
                    yield return new(key, entry.Value as string);
        }

        public string? this[string key] => Environment.GetEnvironmentVariable(key);
        public IEnumerable<string> Keys => Snapshot().Select(item => item.Key);
        public IEnumerable<string?> Values => Snapshot().Select(item => item.Value);
        public int Count => Environment.GetEnvironmentVariables().Count;
        public bool ContainsKey(string key) => Environment.GetEnvironmentVariable(key) is not null;
        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => Snapshot().GetEnumerator();
        public bool TryGetValue(string key, out string? value) { value = Environment.GetEnvironmentVariable(key); return value is not null; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
