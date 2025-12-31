using System.Text.Json;
using System.Text.Json.Serialization;

namespace SshAgentProxy;

public class Config
{
    private static readonly object _saveLock = new();
    private string? _configPath;

    [JsonPropertyName("proxyPipeName")]
    public string ProxyPipeName { get; set; } = "ssh-agent-proxy";

    [JsonPropertyName("backendPipeName")]
    public string BackendPipeName { get; set; } = "openssh-ssh-agent";

    [JsonPropertyName("agents")]
    public Dictionary<string, AgentAppConfig> Agents { get; set; } = new()
    {
        ["1Password"] = new()
        {
            ProcessName = "1Password",
            ExePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"1Password\app\8\1Password.exe"),
            Priority = 1
        },
        ["Bitwarden"] = new()
        {
            ProcessName = "Bitwarden",
            ExePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Bitwarden\Bitwarden.exe"),
            Priority = 2
        }
    };

    [JsonPropertyName("keyMappings")]
    public List<KeyMapping> KeyMappings { get; set; } = [];

    [JsonPropertyName("defaultAgent")]
    public string DefaultAgent { get; set; } = "1Password";

    [JsonPropertyName("failureCacheTtlSeconds")]
    public int FailureCacheTtlSeconds { get; set; } = 60;

    [JsonIgnore]
    public string? ConfigPath => _configPath;

    public static Config LoadOrCreate(string path)
    {
        Config config;
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
        }
        else
        {
            config = new Config();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            config.SaveInternal(path);
        }
        config._configPath = path;
        return config;
    }

    public void Save()
    {
        if (_configPath == null)
            throw new InvalidOperationException("Config path not set. Use Save(path) instead.");
        Save(_configPath);
    }

    public void Save(string path)
    {
        lock (_saveLock)
        {
            SaveInternal(path);
        }
    }

    private void SaveInternal(string path)
    {
        // Atomic write: write to temp file, then rename
        var tempPath = path + ".tmp";
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, options));
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Get agents ordered by priority (lower number = higher priority)
    /// </summary>
    public IEnumerable<(string Name, AgentAppConfig Config)> GetAgentsByPriority()
    {
        return Agents
            .OrderBy(a => a.Value.Priority)
            .Select(a => (a.Key, a.Value));
    }

    /// <summary>
    /// Get all agent names except the specified one, ordered by priority
    /// </summary>
    public IEnumerable<string> GetOtherAgents(string exceptAgent)
    {
        return Agents
            .Where(a => a.Key != exceptAgent)
            .OrderBy(a => a.Value.Priority)
            .Select(a => a.Key);
    }
}

public class AgentAppConfig
{
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "";

    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;
}

public class KeyMapping
{
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("agent")]
    public string Agent { get; set; } = "1Password";
}
