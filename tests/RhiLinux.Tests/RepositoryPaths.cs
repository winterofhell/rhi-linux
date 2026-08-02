namespace RhiLinux.Tests;

internal static class RepositoryPaths
{
    private const string SolutionFileName = "RhiLinux.sln";

    public static string FindRepositoryRoot()
    {
        foreach (var start in StartingDirectories())
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                    return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate {SolutionFileName} by walking parents of " +
            $"AppContext.BaseDirectory ({AppContext.BaseDirectory}) and " +
            $"Directory.GetCurrentDirectory() ({Directory.GetCurrentDirectory()}).");
    }

    public static string GetRepositoryFile(params string[] relativeParts)
    {
        var path = Path.GetFullPath(Path.Combine([FindRepositoryRoot(), .. relativeParts]));
        Assert.True(File.Exists(path),
            $"Expected repository file at '{path}' relative to {SolutionFileName} root.");
        return path;
    }

    private static IEnumerable<string> StartingDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }
}
