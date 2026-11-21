namespace RobustoClient.Systems.Abstract;

/// <summary>Eviction for the per-entity caches the overlays read every frame.</summary>
internal static class CacheUtility
{
    /// <summary>Removes every entry whose stamp is older than <paramref name="cutoff"/>.</summary>
    internal static int RemoveOlderThan<TKey, TValue>(
        Dictionary<TKey, TValue> cache,
        Func<TValue, TimeSpan> stamp,
        TimeSpan cutoff)
        where TKey : notnull
    {
        if (cache.Count == 0)
            return 0;

        List<TKey>? expired = null;

        foreach (var (key, value) in cache)
        {
            if (stamp(value) < cutoff)
                (expired ??= new List<TKey>()).Add(key);
        }

        if (expired == null)
            return 0;

        foreach (var key in expired)
            cache.Remove(key);

        return expired.Count;
    }

    /// <summary>Removes the entry with the oldest stamp, for a cache that is still full.</summary>
    internal static bool RemoveOldest<TKey, TValue>(
        Dictionary<TKey, TValue> cache,
        Func<TValue, TimeSpan> stamp)
        where TKey : notnull
    {
        if (cache.Count == 0)
            return false;

        var oldest = default(TKey)!;
        var oldestStamp = TimeSpan.MaxValue;
        var found = false;

        foreach (var (key, value) in cache)
        {
            var entryStamp = stamp(value);
            if (found && entryStamp >= oldestStamp)
                continue;

            oldestStamp = entryStamp;
            oldest = key;
            found = true;
        }

        return found && cache.Remove(oldest);
    }
}
