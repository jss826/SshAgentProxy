namespace SshAgentProxy;

/// <summary>
/// Cache for tracking failed signing attempts to avoid repeated failures.
/// Thread-safe for concurrent access.
/// </summary>
public class FailureCache
{
    private readonly Dictionary<(string Fingerprint, string Agent), DateTime> _cache = new();
    private readonly object _lock = new();
    private readonly int _ttlSeconds;

    public FailureCache(int ttlSeconds = 60)
    {
        _ttlSeconds = ttlSeconds;
    }

    /// <summary>
    /// Check if a failure is cached for the given fingerprint and agent.
    /// Expired entries are automatically removed.
    /// </summary>
    public bool IsFailureCached(string fingerprint, string agent)
    {
        var key = (fingerprint, agent);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var expiry))
            {
                if (DateTime.UtcNow < expiry)
                    return true;
                _cache.Remove(key);
            }
            return false;
        }
    }

    /// <summary>
    /// Cache a failure for the given fingerprint and agent.
    /// </summary>
    public void CacheFailure(string fingerprint, string agent)
    {
        var key = (fingerprint, agent);
        lock (_lock)
        {
            _cache[key] = DateTime.UtcNow.AddSeconds(_ttlSeconds);
        }
    }

    /// <summary>
    /// Clear a cached failure (e.g., after a successful operation).
    /// </summary>
    public void ClearFailure(string fingerprint, string agent)
    {
        var key = (fingerprint, agent);
        lock (_lock)
        {
            _cache.Remove(key);
        }
    }

    /// <summary>
    /// Clear all cached failures.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// Get the number of cached failures (for testing).
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }
}
