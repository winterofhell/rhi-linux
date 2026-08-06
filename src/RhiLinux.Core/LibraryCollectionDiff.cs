namespace RhiLinux.Core;

public static class LibraryCollectionDiff
{
    public sealed record Result(
        int Added,
        int Updated,
        int Removed,
        IReadOnlyList<InstalledGame> Games);

    public static Result Apply(
        IList<InstalledGame> current,
        IReadOnlyList<InstalledGame> incoming)
    {
        var incomingById = new Dictionary<string, InstalledGame>(StringComparer.Ordinal);
        foreach (var game in incoming)
            incomingById[game.InstallId.Value] = game;

        var added = 0;
        var updated = 0;
        var removed = 0;

        for (var index = current.Count - 1; index >= 0; index--)
        {
            var existing = current[index];
            if (!incomingById.TryGetValue(existing.InstallId.Value, out var next))
            {
                current.RemoveAt(index);
                removed++;
                continue;
            }

            if (!ReferenceEquals(existing, next) && !Equals(existing, next))
            {
                current[index] = next;
                updated++;
            }

            incomingById.Remove(existing.InstallId.Value);
        }

        foreach (var game in incoming)
        {
            if (!incomingById.ContainsKey(game.InstallId.Value))
                continue;
            current.Add(game);
            added++;
        }

        return new Result(added, updated, removed, current.ToArray());
    }
}
