namespace DrawAndGuessMod.Scripts.State;

internal static class GalleryVoteResolver
{
    public static ulong Resolve(
        IReadOnlyCollection<ulong> candidateOwnerIds,
        IEnumerable<ulong> votes)
    {
        HashSet<ulong> candidates = candidateOwnerIds.ToHashSet();
        if (candidates.Count == 0)
        {
            return 0ul;
        }

        Dictionary<ulong, int> counts = candidates.ToDictionary(candidate => candidate, _ => 0);
        foreach (ulong vote in votes)
        {
            if (counts.ContainsKey(vote))
            {
                counts[vote]++;
            }
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .First()
            .Key;
    }
}
