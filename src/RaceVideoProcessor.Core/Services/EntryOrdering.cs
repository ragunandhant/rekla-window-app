namespace RaceVideoProcessor.Core.Services;

/// <summary>Newest-first ordering of entries and choice of the next entry to work on.</summary>
public static class EntryOrdering
{
    /// <summary>
    /// Newest API date first. Entries without a date go last; ties fall back to
    /// the card number so the order is stable between refreshes.
    /// </summary>
    public static int CompareNewestFirst(DateTimeOffset? aDate, string aCard, DateTimeOffset? bDate, string bCard)
    {
        if (aDate.HasValue && bDate.HasValue)
        {
            var byDate = bDate.Value.CompareTo(aDate.Value);
            if (byDate != 0)
                return byDate;
        }
        else if (aDate.HasValue != bDate.HasValue)
        {
            return aDate.HasValue ? -1 : 1;
        }

        return string.Compare(aCard, bCard, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first eligible entry after <paramref name="current"/> in list order,
    /// wrapping to the top. Never returns <paramref name="current"/> itself.
    /// </summary>
    public static T? FindNext<T>(IReadOnlyList<T> ordered, T? current, Func<T, bool> isEligible) where T : class
    {
        if (ordered.Count == 0)
            return null;

        var start = current is null ? -1 : IndexOf(ordered, current);
        for (var step = 1; step <= ordered.Count; step++)
        {
            var index = (start + step) % ordered.Count;
            if (index < 0)
                index += ordered.Count;
            var candidate = ordered[index];
            if (!ReferenceEquals(candidate, current) && isEligible(candidate))
                return candidate;
        }

        return null;
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T item) where T : class
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item))
                return i;
        return -1;
    }
}
