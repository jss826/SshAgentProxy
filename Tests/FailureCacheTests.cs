namespace SshAgentProxy.Tests;

public class FailureCacheTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_DefaultTtl_Is60Seconds()
    {
        // Arrange & Act
        var cache = new FailureCache();

        // Assert - Cache a failure and check it's cached
        cache.CacheFailure("fp1", "agent1");
        Assert.True(cache.IsFailureCached("fp1", "agent1"));
    }

    [Fact]
    public void Constructor_CustomTtl_IsRespected()
    {
        // Arrange
        var cache = new FailureCache(ttlSeconds: 1);

        // Act
        cache.CacheFailure("fp1", "agent1");

        // Assert - Initially cached
        Assert.True(cache.IsFailureCached("fp1", "agent1"));

        // Wait for expiry
        Thread.Sleep(1100);
        Assert.False(cache.IsFailureCached("fp1", "agent1"));
    }

    #endregion

    #region IsFailureCached Tests

    [Fact]
    public void IsFailureCached_NotCached_ReturnsFalse()
    {
        // Arrange
        var cache = new FailureCache();

        // Act & Assert
        Assert.False(cache.IsFailureCached("unknown", "unknown"));
    }

    [Fact]
    public void IsFailureCached_Cached_ReturnsTrue()
    {
        // Arrange
        var cache = new FailureCache();
        cache.CacheFailure("fp1", "agent1");

        // Act & Assert
        Assert.True(cache.IsFailureCached("fp1", "agent1"));
    }

    [Fact]
    public void IsFailureCached_DifferentFingerprint_ReturnsFalse()
    {
        // Arrange
        var cache = new FailureCache();
        cache.CacheFailure("fp1", "agent1");

        // Act & Assert
        Assert.False(cache.IsFailureCached("fp2", "agent1"));
    }

    [Fact]
    public void IsFailureCached_DifferentAgent_ReturnsFalse()
    {
        // Arrange
        var cache = new FailureCache();
        cache.CacheFailure("fp1", "agent1");

        // Act & Assert
        Assert.False(cache.IsFailureCached("fp1", "agent2"));
    }

    [Fact]
    public void IsFailureCached_Expired_ReturnsFalseAndRemovesEntry()
    {
        // Arrange
        var cache = new FailureCache(ttlSeconds: 1);
        cache.CacheFailure("fp1", "agent1");

        // Wait for expiry
        Thread.Sleep(1100);

        // Act
        var result = cache.IsFailureCached("fp1", "agent1");

        // Assert
        Assert.False(result);
        Assert.Equal(0, cache.Count);
    }

    #endregion

    #region CacheFailure Tests

    [Fact]
    public void CacheFailure_AddsNewEntry()
    {
        // Arrange
        var cache = new FailureCache();

        // Act
        cache.CacheFailure("fp1", "agent1");

        // Assert
        Assert.Equal(1, cache.Count);
        Assert.True(cache.IsFailureCached("fp1", "agent1"));
    }

    [Fact]
    public void CacheFailure_UpdatesExistingEntry()
    {
        // Arrange
        var cache = new FailureCache(ttlSeconds: 1);
        cache.CacheFailure("fp1", "agent1");
        Thread.Sleep(500); // Wait half the TTL

        // Act - Refresh the cache
        cache.CacheFailure("fp1", "agent1");
        Thread.Sleep(600); // Wait past original TTL but within refreshed TTL

        // Assert - Should still be cached due to refresh
        Assert.True(cache.IsFailureCached("fp1", "agent1"));
    }

    [Fact]
    public void CacheFailure_MultipleEntries()
    {
        // Arrange
        var cache = new FailureCache();

        // Act
        cache.CacheFailure("fp1", "agent1");
        cache.CacheFailure("fp2", "agent1");
        cache.CacheFailure("fp1", "agent2");

        // Assert
        Assert.Equal(3, cache.Count);
        Assert.True(cache.IsFailureCached("fp1", "agent1"));
        Assert.True(cache.IsFailureCached("fp2", "agent1"));
        Assert.True(cache.IsFailureCached("fp1", "agent2"));
    }

    #endregion

    #region ClearFailure Tests

    [Fact]
    public void ClearFailure_RemovesEntry()
    {
        // Arrange
        var cache = new FailureCache();
        cache.CacheFailure("fp1", "agent1");

        // Act
        cache.ClearFailure("fp1", "agent1");

        // Assert
        Assert.Equal(0, cache.Count);
        Assert.False(cache.IsFailureCached("fp1", "agent1"));
    }

    [Fact]
    public void ClearFailure_NonExistent_DoesNotThrow()
    {
        // Arrange
        var cache = new FailureCache();

        // Act & Assert - Should not throw
        var exception = Record.Exception(() => cache.ClearFailure("fp1", "agent1"));
        Assert.Null(exception);
    }

    [Fact]
    public void ClearFailure_OnlyRemovesSpecificEntry()
    {
        // Arrange
        var cache = new FailureCache();
        cache.CacheFailure("fp1", "agent1");
        cache.CacheFailure("fp2", "agent1");

        // Act
        cache.ClearFailure("fp1", "agent1");

        // Assert
        Assert.Equal(1, cache.Count);
        Assert.False(cache.IsFailureCached("fp1", "agent1"));
        Assert.True(cache.IsFailureCached("fp2", "agent1"));
    }

    #endregion

    #region Clear Tests

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        var cache = new FailureCache();
        cache.CacheFailure("fp1", "agent1");
        cache.CacheFailure("fp2", "agent2");
        cache.CacheFailure("fp3", "agent3");

        // Act
        cache.Clear();

        // Assert
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Clear_EmptyCache_DoesNotThrow()
    {
        // Arrange
        var cache = new FailureCache();

        // Act & Assert
        var exception = Record.Exception(() => cache.Clear());
        Assert.Null(exception);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentAccess_DoesNotCorrupt()
    {
        // Arrange
        var cache = new FailureCache(ttlSeconds: 60);
        var tasks = new List<Task>();

        // Act - Run many concurrent operations
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                cache.CacheFailure($"fp{index % 10}", $"agent{index % 5}");
                cache.IsFailureCached($"fp{index % 10}", $"agent{index % 5}");
                if (index % 3 == 0)
                    cache.ClearFailure($"fp{index % 10}", $"agent{index % 5}");
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - Cache should be in valid state (no exception)
        var exception = Record.Exception(() =>
        {
            _ = cache.Count;
            cache.Clear();
        });
        Assert.Null(exception);
    }

    #endregion

    #region Count Property Tests

    [Fact]
    public void Count_EmptyCache_ReturnsZero()
    {
        // Arrange
        var cache = new FailureCache();

        // Assert
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Count_ReflectsActualCount()
    {
        // Arrange
        var cache = new FailureCache();

        // Act & Assert
        cache.CacheFailure("fp1", "agent1");
        Assert.Equal(1, cache.Count);

        cache.CacheFailure("fp2", "agent2");
        Assert.Equal(2, cache.Count);

        cache.ClearFailure("fp1", "agent1");
        Assert.Equal(1, cache.Count);
    }

    #endregion
}
