using System.Text.Json;

namespace SshAgentProxy.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SshAgentProxyTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    #region Default Values Tests

    [Fact]
    public void Config_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new Config();

        // Assert
        Assert.Equal("ssh-agent-proxy", config.ProxyPipeName);
        Assert.Equal("openssh-ssh-agent", config.BackendPipeName);
        Assert.Equal("1Password", config.DefaultAgent);
        Assert.Equal(60, config.FailureCacheTtlSeconds);
        Assert.Empty(config.KeyMappings);
    }

    [Fact]
    public void Config_DefaultAgents_ContainsTwoAgents()
    {
        // Arrange & Act
        var config = new Config();

        // Assert
        Assert.Equal(2, config.Agents.Count);
        Assert.Contains("1Password", config.Agents.Keys);
        Assert.Contains("Bitwarden", config.Agents.Keys);
    }

    [Fact]
    public void Config_DefaultAgents_HaveCorrectPriorities()
    {
        // Arrange & Act
        var config = new Config();

        // Assert
        Assert.Equal(1, config.Agents["1Password"].Priority);
        Assert.Equal(2, config.Agents["Bitwarden"].Priority);
    }

    #endregion

    #region GetAgentsByPriority Tests

    [Fact]
    public void GetAgentsByPriority_ReturnsAgentsInPriorityOrder()
    {
        // Arrange
        var config = new Config();
        config.Agents["1Password"].Priority = 2;
        config.Agents["Bitwarden"].Priority = 1;

        // Act
        var result = config.GetAgentsByPriority().ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Bitwarden", result[0].Name);
        Assert.Equal("1Password", result[1].Name);
    }

    [Fact]
    public void GetAgentsByPriority_WithThreeAgents_ReturnsCorrectOrder()
    {
        // Arrange
        var config = new Config();
        config.Agents["KeePassXC"] = new AgentAppConfig { ProcessName = "KeePassXC", Priority = 2 };
        config.Agents["1Password"].Priority = 3;
        config.Agents["Bitwarden"].Priority = 1;

        // Act
        var result = config.GetAgentsByPriority().Select(a => a.Name).ToList();

        // Assert
        Assert.Equal(new[] { "Bitwarden", "KeePassXC", "1Password" }, result);
    }

    #endregion

    #region GetOtherAgents Tests

    [Fact]
    public void GetOtherAgents_ExcludesSpecifiedAgent()
    {
        // Arrange
        var config = new Config();

        // Act
        var result = config.GetOtherAgents("1Password").ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("Bitwarden", result[0]);
    }

    [Fact]
    public void GetOtherAgents_ReturnsInPriorityOrder()
    {
        // Arrange
        var config = new Config();
        config.Agents["KeePassXC"] = new AgentAppConfig { ProcessName = "KeePassXC", Priority = 1 };
        config.Agents["1Password"].Priority = 2;
        config.Agents["Bitwarden"].Priority = 3;

        // Act
        var result = config.GetOtherAgents("Bitwarden").ToList();

        // Assert
        Assert.Equal(new[] { "KeePassXC", "1Password" }, result);
    }

    [Fact]
    public void GetOtherAgents_NonExistentAgent_ReturnsAll()
    {
        // Arrange
        var config = new Config();

        // Act
        var result = config.GetOtherAgents("NonExistent").ToList();

        // Assert
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region LoadOrCreate Tests

    [Fact]
    public void LoadOrCreate_CreatesNewFileWhenNotExists()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");

        // Act
        var config = Config.LoadOrCreate(configPath);

        // Assert
        Assert.True(File.Exists(configPath));
        Assert.NotNull(config);
        Assert.Equal(configPath, config.ConfigPath);
    }

    [Fact]
    public void LoadOrCreate_LoadsExistingFile()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var originalConfig = new Config { DefaultAgent = "Bitwarden" };
        File.WriteAllText(configPath, JsonSerializer.Serialize(originalConfig));

        // Act
        var config = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Equal("Bitwarden", config.DefaultAgent);
    }

    [Fact]
    public void LoadOrCreate_CreatesDirectoryIfNeeded()
    {
        // Arrange
        var subDir = Path.Combine(_tempDir, "subdir", "nested");
        var configPath = Path.Combine(subDir, "config.json");

        // Act
        var config = Config.LoadOrCreate(configPath);

        // Assert
        Assert.True(Directory.Exists(subDir));
        Assert.True(File.Exists(configPath));
    }

    #endregion

    #region Save Tests

    [Fact]
    public void Save_WithPath_SavesCorrectly()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = new Config { DefaultAgent = "TestAgent" };

        // Act
        config.Save(configPath);

        // Assert
        Assert.True(File.Exists(configPath));
        var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath));
        Assert.Equal("TestAgent", loaded?.DefaultAgent);
    }

    [Fact]
    public void Save_WithoutPath_ThrowsWhenConfigPathNotSet()
    {
        // Arrange
        var config = new Config();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => config.Save());
    }

    [Fact]
    public void Save_WithoutPath_WorksAfterLoadOrCreate()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = Config.LoadOrCreate(configPath);
        config.DefaultAgent = "Modified";

        // Act
        config.Save();

        // Assert
        var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath));
        Assert.Equal("Modified", loaded?.DefaultAgent);
    }

    [Fact]
    public void Save_AtomicWrite_DoesNotCorruptOnInterruption()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = Config.LoadOrCreate(configPath);

        // Act - Save multiple times rapidly
        for (int i = 0; i < 10; i++)
        {
            config.DefaultAgent = $"Agent{i}";
            config.Save();
        }

        // Assert - File should be valid JSON
        var content = File.ReadAllText(configPath);
        var loaded = JsonSerializer.Deserialize<Config>(content);
        Assert.NotNull(loaded);
        Assert.Equal("Agent9", loaded.DefaultAgent);
    }

    [Fact]
    public void Save_RemovesTempFile()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = Config.LoadOrCreate(configPath);

        // Act
        config.Save();

        // Assert
        var tempPath = configPath + ".tmp";
        Assert.False(File.Exists(tempPath));
    }

    #endregion

    #region KeyMapping Tests

    [Fact]
    public void KeyMappings_CanBeAddedAndSaved()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = Config.LoadOrCreate(configPath);
        config.KeyMappings.Add(new KeyMapping { Fingerprint = "ABC123", Agent = "1Password" });

        // Act
        config.Save();
        var loaded = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Single(loaded.KeyMappings);
        Assert.Equal("ABC123", loaded.KeyMappings[0].Fingerprint);
        Assert.Equal("1Password", loaded.KeyMappings[0].Agent);
    }

    [Fact]
    public void KeyMappings_SupportsCommentBasedMapping()
    {
        // Arrange
        var config = new Config();
        config.KeyMappings.Add(new KeyMapping { Comment = "work@company.com", Agent = "Bitwarden" });

        // Assert
        Assert.Null(config.KeyMappings[0].Fingerprint);
        Assert.Equal("work@company.com", config.KeyMappings[0].Comment);
    }

    [Fact]
    public void KeyMappings_SupportsKeyBlobCaching()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = Config.LoadOrCreate(configPath);
        var keyBlob = Convert.ToBase64String(new byte[] { 0x00, 0x00, 0x00, 0x07, 0x73, 0x73, 0x68, 0x2d, 0x72, 0x73, 0x61 });
        config.KeyMappings.Add(new KeyMapping
        {
            Fingerprint = "ABC123",
            KeyBlob = keyBlob,
            Comment = "test@example.com",
            Agent = "1Password"
        });

        // Act
        config.Save();
        var loaded = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Single(loaded.KeyMappings);
        Assert.Equal("ABC123", loaded.KeyMappings[0].Fingerprint);
        Assert.Equal(keyBlob, loaded.KeyMappings[0].KeyBlob);
        Assert.Equal("test@example.com", loaded.KeyMappings[0].Comment);
        Assert.Equal("1Password", loaded.KeyMappings[0].Agent);
    }

    [Fact]
    public void KeyMappings_KeyBlobCanBeNull()
    {
        // Arrange - old config without keyBlob should still load
        var configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, """
        {
            "keyMappings": [
                { "fingerprint": "ABC123", "agent": "1Password" }
            ]
        }
        """);

        // Act
        var config = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Single(config.KeyMappings);
        Assert.Equal("ABC123", config.KeyMappings[0].Fingerprint);
        Assert.Null(config.KeyMappings[0].KeyBlob);
        Assert.Equal("1Password", config.KeyMappings[0].Agent);
    }

    #endregion

    #region AgentAppConfig Tests

    [Fact]
    public void AgentAppConfig_DefaultPriority_Is100()
    {
        // Arrange & Act
        var agent = new AgentAppConfig();

        // Assert
        Assert.Equal(100, agent.Priority);
    }

    [Fact]
    public void AgentAppConfig_CanSetAllProperties()
    {
        // Arrange & Act
        var agent = new AgentAppConfig
        {
            ProcessName = "TestProcess",
            ExePath = @"C:\Test\test.exe",
            Priority = 5
        };

        // Assert
        Assert.Equal("TestProcess", agent.ProcessName);
        Assert.Equal(@"C:\Test\test.exe", agent.ExePath);
        Assert.Equal(5, agent.Priority);
    }

    #endregion

    #region HostKeyMapping Tests

    [Fact]
    public void HostKeyMappings_DefaultIsEmpty()
    {
        var config = new Config();
        Assert.Empty(config.HostKeyMappings);
    }

    [Fact]
    public void HostKeyMappings_CanBeAddedAndSaved()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = Config.LoadOrCreate(configPath);
        config.HostKeyMappings.Add(new HostKeyMapping
        {
            Pattern = "github.com:myorg/*",
            Fingerprint = "ABC123",
            Description = "Work account"
        });

        // Act
        config.Save();
        var loaded = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Single(loaded.HostKeyMappings);
        Assert.Equal("github.com:myorg/*", loaded.HostKeyMappings[0].Pattern);
        Assert.Equal("ABC123", loaded.HostKeyMappings[0].Fingerprint);
        Assert.Equal("Work account", loaded.HostKeyMappings[0].Description);
    }

    [Fact]
    public void HostKeyMappings_DescriptionCanBeNull()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, """
        {
            "hostKeyMappings": [
                { "pattern": "github.com:*", "fingerprint": "ABC123" }
            ]
        }
        """);

        // Act
        var config = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Single(config.HostKeyMappings);
        Assert.Equal("github.com:*", config.HostKeyMappings[0].Pattern);
        Assert.Null(config.HostKeyMappings[0].Description);
    }

    [Fact]
    public void HostKeyMappings_MultiplePatterns_PreserveOrder()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = Config.LoadOrCreate(configPath);
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:org1/*", Fingerprint = "KEY1" });
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:org2/*", Fingerprint = "KEY2" });
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:*", Fingerprint = "KEY3" });

        // Act
        config.Save();
        var loaded = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Equal(3, loaded.HostKeyMappings.Count);
        Assert.Equal("github.com:org1/*", loaded.HostKeyMappings[0].Pattern);
        Assert.Equal("github.com:org2/*", loaded.HostKeyMappings[1].Pattern);
        Assert.Equal("github.com:*", loaded.HostKeyMappings[2].Pattern);
    }

    [Fact]
    public void KeySelectionTimeoutSeconds_DefaultIs30()
    {
        var config = new Config();
        Assert.Equal(30, config.KeySelectionTimeoutSeconds);
    }

    [Fact]
    public void KeySelectionTimeoutSeconds_CanBeCustomized()
    {
        // Arrange
        var configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, """
        {
            "keySelectionTimeoutSeconds": 60
        }
        """);

        // Act
        var config = Config.LoadOrCreate(configPath);

        // Assert
        Assert.Equal(60, config.KeySelectionTimeoutSeconds);
    }

    #endregion

    #region HostKeyMapping Specificity Tests

    [Theory]
    [InlineData("github.com", 1)]
    [InlineData("github.com:*", 1)]
    [InlineData("gitlab.com:*", 1)]
    [InlineData("github.com:owner/*", 2)]
    [InlineData("github.com:myorg/*", 2)]
    [InlineData("github.com:owner/repo", 3)]
    [InlineData("github.com:owner/repo.git", 3)]
    public void GetSpecificity_ReturnsCorrectScore(string pattern, int expectedScore)
    {
        Assert.Equal(expectedScore, HostKeyMapping.GetSpecificity(pattern));
    }

    [Fact]
    public void HostKeyMappings_SpecificPatternMatchesFirst_RegardlessOfOrder()
    {
        // Arrange - catch-all pattern listed BEFORE specific pattern
        var config = new Config();
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:*", Fingerprint = "CATCH_ALL" });
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:myorg/*", Fingerprint = "ORG_KEY" });
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:myorg/specific-repo", Fingerprint = "REPO_KEY" });

        var connectionInfo = new SshConnectionInfo
        {
            Host = "github.com",
            Repository = "myorg/specific-repo"
        };

        // Act - iterate in specificity order (same as SshAgentProxy does)
        string? matched = null;
        foreach (var mapping in config.HostKeyMappings
            .OrderByDescending(m => HostKeyMapping.GetSpecificity(m.Pattern)))
        {
            if (connectionInfo.MatchesPattern(mapping.Pattern))
            {
                matched = mapping.Fingerprint;
                break;
            }
        }

        // Assert - exact repo match wins despite being last in the list
        Assert.Equal("REPO_KEY", matched);
    }

    [Fact]
    public void HostKeyMappings_OwnerWildcardBeatsHostWildcard()
    {
        // Arrange - catch-all first, owner-specific second
        var config = new Config();
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:*", Fingerprint = "CATCH_ALL" });
        config.HostKeyMappings.Add(new HostKeyMapping { Pattern = "github.com:sooooooooooon/*", Fingerprint = "OWNER_KEY" });

        var connectionInfo = new SshConnectionInfo
        {
            Host = "github.com",
            Repository = "sooooooooooon/some-repo"
        };

        // Act
        string? matched = null;
        foreach (var mapping in config.HostKeyMappings
            .OrderByDescending(m => HostKeyMapping.GetSpecificity(m.Pattern)))
        {
            if (connectionInfo.MatchesPattern(mapping.Pattern))
            {
                matched = mapping.Fingerprint;
                break;
            }
        }

        // Assert - owner wildcard wins over host wildcard
        Assert.Equal("OWNER_KEY", matched);
    }

    #endregion

    #region JSON Serialization Tests

    [Fact]
    public void Config_SerializesWithCorrectPropertyNames()
    {
        // Arrange
        var config = new Config
        {
            ProxyPipeName = "test-proxy",
            FailureCacheTtlSeconds = 120
        };

        // Act
        var json = JsonSerializer.Serialize(config);

        // Assert
        Assert.Contains("\"proxyPipeName\"", json);
        Assert.Contains("\"failureCacheTtlSeconds\"", json);
        Assert.Contains("\"keyMappings\"", json);
    }

    [Fact]
    public void Config_DeserializesFromJson()
    {
        // Arrange
        var json = """
        {
            "proxyPipeName": "custom-proxy",
            "defaultAgent": "Bitwarden",
            "failureCacheTtlSeconds": 30,
            "agents": {
                "CustomAgent": {
                    "processName": "CustomProcess",
                    "exePath": "C:\\custom.exe",
                    "priority": 1
                }
            }
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<Config>(json);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("custom-proxy", config.ProxyPipeName);
        Assert.Equal("Bitwarden", config.DefaultAgent);
        Assert.Equal(30, config.FailureCacheTtlSeconds);
        Assert.Contains("CustomAgent", config.Agents.Keys);
    }

    #endregion
}
