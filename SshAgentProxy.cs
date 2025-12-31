using System.Diagnostics;
using System.Linq;
using SshAgentProxy.Pipes;
using SshAgentProxy.Protocol;

namespace SshAgentProxy;

public class SshAgentProxyService : IAsyncDisposable
{
    private readonly Config _config;
    private readonly NamedPipeAgentServer _server;
    private readonly SemaphoreSlim _stateLock = new(1, 1); // Thread safety for shared state
    private string? _currentAgent; // null = unknown/not determined yet
    private readonly Dictionary<string, string> _keyToAgent = new(); // fingerprint -> agent name
    private readonly List<SshIdentity> _allKeys = new(); // merged keys from all agents
    private bool _keysScanned = false;
    private readonly FailureCache _failureCache;

    public event Action<string>? OnLog;
    public string? CurrentAgent => _currentAgent;

    public SshAgentProxyService(Config config)
    {
        _config = config;
        _currentAgent = null; // Will be determined on first connection
        _failureCache = new FailureCache(config.FailureCacheTtlSeconds);
        _server = new NamedPipeAgentServer(config.ProxyPipeName, HandleRequestAsync);
        _server.OnLog += msg => OnLog?.Invoke(msg);

        // Load key mappings from config
        foreach (var mapping in config.KeyMappings)
        {
            if (!string.IsNullOrEmpty(mapping.Fingerprint))
                _keyToAgent[mapping.Fingerprint] = mapping.Agent;
            if (!string.IsNullOrEmpty(mapping.Comment))
                _keyToAgent[$"comment:{mapping.Comment}"] = mapping.Agent;
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        Log($"Starting proxy on pipe: {_config.ProxyPipeName}");
        Log($"Backend pipe: {_config.BackendPipeName}");
        Log($"Configured agents: {string.Join(", ", _config.Agents.Keys)}");
        Log($"Default agent: {_config.DefaultAgent}");

        // Try to detect current agent from existing pipe
        await DetectCurrentAgentAsync(ct);

        _server.Start();
        Log("Proxy server started");
        Log("");
        Log("=== IMPORTANT ===");
        Log($"Set environment variable: SSH_AUTH_SOCK=\\\\.\\pipe\\{_config.ProxyPipeName}");
        Log("=================");
    }

    /// <summary>
    /// Detect which agent currently owns the backend pipe by checking keys against mappings
    /// </summary>
    private async Task DetectCurrentAgentAsync(CancellationToken ct)
    {
        Log("Detecting current agent from backend pipe...");

        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
        {
            Log("  No backend available - agent will be started on first request");
            _currentAgent = null;
            return;
        }

        try
        {
            var keys = await client.RequestIdentitiesAsync(ct);
            Log($"  Found {keys.Count} keys from backend");

            if (keys.Count == 0)
            {
                Log("  No keys - cannot determine agent");
                _currentAgent = null;
                return;
            }

            // Check keys against known mappings to identify current agent
            var agentVotes = new Dictionary<string, int>();
            foreach (var key in keys)
            {
                if (_keyToAgent.TryGetValue(key.Fingerprint, out var mappedAgent))
                {
                    agentVotes.TryGetValue(mappedAgent, out var count);
                    agentVotes[mappedAgent] = count + 1;
                    Log($"    [{mappedAgent}] {key.Comment} ({key.Fingerprint}) - mapped");
                }
                else
                {
                    Log($"    [?] {key.Comment} ({key.Fingerprint}) - unmapped");
                }

                // Add to allKeys if not already present
                if (!_allKeys.Any(k => k.Fingerprint == key.Fingerprint))
                {
                    _allKeys.Add(key);
                }
            }

            if (agentVotes.Count > 0)
            {
                // Use the agent with most mapped keys
                var detected = agentVotes.OrderByDescending(v => v.Value).First().Key;
                _currentAgent = detected;
                _keysScanned = true;
                Log($"  Detected current agent: {detected} (by key mapping)");

                // Map unmapped keys to detected agent
                foreach (var key in keys)
                {
                    if (!_keyToAgent.ContainsKey(key.Fingerprint))
                    {
                        _keyToAgent[key.Fingerprint] = detected;
                    }
                }
            }
            else
            {
                // No mappings - cannot determine, leave as null
                Log("  No key mappings found - agent unknown");
                _currentAgent = null;
            }
        }
        catch (Exception ex)
        {
            Log($"  Error detecting agent: {ex.Message}");
            _currentAgent = null;
        }
    }

    /// <summary>
    /// Save a key mapping to config and persist to disk
    /// </summary>
    private void SaveKeyMapping(string fingerprint, string agent)
    {
        // Update in-memory mapping
        _keyToAgent[fingerprint] = agent;

        // Check if already in config
        var existing = _config.KeyMappings.FirstOrDefault(m => m.Fingerprint == fingerprint);
        if (existing != null)
        {
            if (existing.Agent == agent)
                return; // No change needed
            existing.Agent = agent;
        }
        else
        {
            _config.KeyMappings.Add(new KeyMapping { Fingerprint = fingerprint, Agent = agent });
        }

        // Persist to disk
        try
        {
            _config.Save();
            Log($"    Mapping saved: {fingerprint} -> {agent}");
        }
        catch (Exception ex)
        {
            Log($"    Warning: Failed to save mapping: {ex.Message}");
        }
    }

    /// <summary>
    /// Get agent config by name, returns null if not found
    /// </summary>
    private AgentAppConfig? GetAgentConfig(string agentName)
    {
        return _config.Agents.TryGetValue(agentName, out var config) ? config : null;
    }

    /// <summary>
    /// Rescan all agents for keys (public wrapper for ScanAllAgentsAsync)
    /// </summary>
    public async Task ScanKeysAsync(CancellationToken ct = default)
    {
        Log("Rescanning all agents...");
        _allKeys.Clear();
        _keysScanned = false;
        await ScanAllAgentsAsync(ct);
    }

    private async Task<NamedPipeAgentClient?> ConnectToBackendAsync(CancellationToken ct = default)
    {
        var client = new NamedPipeAgentClient(_config.BackendPipeName);

        if (await client.TryConnectAsync(2000, ct))
        {
            Log($"Connected to backend: {_config.BackendPipeName}");
            return client;
        }

        Log($"Failed to connect to backend: {_config.BackendPipeName}");
        client.Dispose();
        return null;
    }

    public Task ForceSwitchToAsync(string agentName, CancellationToken ct = default)
    {
        return ForceSwitchToAsync(agentName, startOthers: true, ct);
    }

    public Task ForceSwitchToAsync(string agentName, bool startOthers, CancellationToken ct = default)
    {
        return SwitchToAsync(agentName, startOthers, force: true, ct);
    }

    /// <summary>
    /// Start the specified agent if not running (does not stop other agents)
    /// </summary>
    public async Task EnsureAgentRunningAsync(string agentName, CancellationToken ct = default)
    {
        var agent = GetAgentConfig(agentName);
        if (agent == null)
        {
            Log($"Warning: Agent '{agentName}' not configured");
            return;
        }

        var processes = Process.GetProcessesByName(agent.ProcessName);
        if (processes.Length > 0)
        {
            Log($"{agentName} is already running");
            _currentAgent = agentName;
            return;
        }

        Log($"Starting {agentName}...");
        StartProcessIfNeeded(agent.ProcessName, agent.ExePath);
        await Task.Delay(3000, ct); // Wait for startup
        _currentAgent = agentName;
        Log($"{agentName} started");
    }

    /// <summary>
    /// Start other agents in the background (fire and forget).
    /// Exceptions are caught and logged since this is called without await.
    /// </summary>
    private async Task EnsureOtherAgentsRunningAsync(string primaryAgent, CancellationToken ct)
    {
        try
        {
            foreach (var otherAgentName in _config.GetOtherAgents(primaryAgent))
            {
                var otherAgent = GetAgentConfig(otherAgentName);
                if (otherAgent == null) continue;

                var processes = Process.GetProcessesByName(otherAgent.ProcessName);
                if (processes.Length > 0)
                    continue; // Already running

                Log($"  Starting {otherAgentName} in background...");
                await Task.Delay(1000, ct); // Brief delay before starting
                StartProcessIfNeeded(otherAgent.ProcessName, otherAgent.ExePath);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            Log($"  Warning: Failed to start other agents: {ex.Message}");
        }
    }

    public async Task SwitchToAsync(string agentName, CancellationToken ct = default)
    {
        await SwitchToAsync(agentName, startOthers: true, force: false, ct);
    }

    public async Task SwitchToAsync(string agentName, bool startOthers, bool force = false, CancellationToken ct = default)
    {
        if (!force && _currentAgent == agentName)
        {
            Log($"Already using {agentName}");
            return;
        }

        var primary = GetAgentConfig(agentName);
        if (primary == null)
        {
            Log($"Warning: Agent '{agentName}' not configured");
            return;
        }

        Log($"Switching from {_currentAgent ?? "(none)"} to {agentName}...");

        // 1. Kill all agent processes to release the pipe
        foreach (var (name, config) in _config.GetAgentsByPriority())
        {
            await KillProcessAsync(config.ProcessName);
        }
        await Task.Delay(1000, ct);

        // 2. Start primary first (to acquire the pipe)
        StartProcessIfNeeded(primary.ProcessName, primary.ExePath);
        await Task.Delay(3000, ct); // Wait for startup

        // 3. Start others (optional)
        if (startOthers)
        {
            foreach (var otherAgentName in _config.GetOtherAgents(agentName))
            {
                var otherAgent = GetAgentConfig(otherAgentName);
                if (otherAgent != null)
                {
                    StartProcessIfNeeded(otherAgent.ProcessName, otherAgent.ExePath);
                    await Task.Delay(1000, ct);
                }
            }
        }

        _currentAgent = agentName;
        Log($"Switched to {agentName}");
    }

    private async Task<SshAgentMessage> HandleRequestAsync(SshAgentMessage request, CancellationToken ct)
    {
        // Acquire lock for thread safety (multiple clients can connect concurrently)
        await _stateLock.WaitAsync(ct);
        try
        {
            return request.Type switch
            {
                SshAgentMessageType.SSH_AGENTC_REQUEST_IDENTITIES => await HandleRequestIdentitiesAsync(ct),
                SshAgentMessageType.SSH_AGENTC_SIGN_REQUEST => await HandleSignRequestAsync(request.Payload, ct),
                _ => await ForwardRequestAsync(request, ct),
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private Task<SshAgentMessage> HandleRequestIdentitiesAsync(CancellationToken ct)
    {
        Log("Request: List identities");

        if (_keysScanned && _allKeys.Count > 0)
        {
            // Return merged keys if already scanned
            Log($"  Returning {_allKeys.Count} merged keys");
            foreach (var id in _allKeys)
            {
                var agent = _keyToAgent.TryGetValue(id.Fingerprint, out var a) ? a : "?";
                Log($"    [{agent}] {id.Comment} ({id.Fingerprint})");
            }
            return Task.FromResult(SshAgentMessage.IdentitiesAnswer(_allKeys));
        }

        // If not scanned yet, get keys from backend
        return HandleRequestIdentitiesFromBackendAsync(ct);
    }

    private async Task<SshAgentMessage> HandleRequestIdentitiesFromBackendAsync(CancellationToken ct)
    {
        // Get keys from both agents
        await ScanAllAgentsAsync(ct);

        if (_allKeys.Count == 0)
        {
            Log("  No keys found from any agent");
            return SshAgentMessage.Failure();
        }

        Log($"  Returning {_allKeys.Count} keys from all agents");
        return SshAgentMessage.IdentitiesAnswer(_allKeys);
    }

    private async Task ScanAllAgentsAsync(CancellationToken ct)
    {
        Log("  Scanning all agents for keys...");

        // Scan all configured agents by priority
        foreach (var (agentName, _) in _config.GetAgentsByPriority())
        {
            await ScanAgentAsync(agentName, ct);
        }

        _keysScanned = _allKeys.Count > 0;
        Log($"  Total: {_allKeys.Count} keys");
    }

    private async Task ScanAgentAsync(string agentName, CancellationToken ct)
    {
        if (GetAgentConfig(agentName) == null)
        {
            Log($"    {agentName}: not configured");
            return;
        }

        Log($"    Scanning {agentName}...");

        // Switch to this agent if different from current
        if (_currentAgent != agentName)
        {
            await ForceSwitchToAsync(agentName, startOthers: false, ct);
        }
        else
        {
            // Start agent if not running
            await EnsureAgentRunningAsync(agentName, ct);
        }

        await Task.Delay(500, ct); // Wait for pipe to stabilize

        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
        {
            Log($"    {agentName}: not available");
            return;
        }

        try
        {
            var keys = await client.RequestIdentitiesAsync(ct);
            Log($"    {agentName}: {keys.Count} keys");

            foreach (var key in keys)
            {
                if (!_keyToAgent.ContainsKey(key.Fingerprint))
                {
                    _keyToAgent[key.Fingerprint] = agentName;
                }
                if (!_allKeys.Any(k => k.Fingerprint == key.Fingerprint))
                {
                    _allKeys.Add(key);
                    Log($"      [{agentName}] {key.Comment} ({key.Fingerprint})");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"    {agentName}: error - {ex.Message}");
        }
    }

    private async Task<SshAgentMessage> HandleSignRequestAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var (keyBlob, data, flags) = SshAgentProtocol.ParseSignRequest(payload);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(keyBlob))[..16];

        Log($"Request: Sign with key {fingerprint}");

        // Step 1: Determine target agent from mapping
        string? targetAgent = null;
        if (_keyToAgent.TryGetValue(fingerprint, out var mappedAgent))
        {
            targetAgent = mappedAgent;
            Log($"  Key mapped to {mappedAgent}");
        }
        else if (_currentAgent != null)
        {
            targetAgent = _currentAgent;
            Log($"  No mapping, using current agent: {_currentAgent}");
        }
        else
        {
            targetAgent = _config.DefaultAgent;
            Log($"  No mapping, no current agent, using default: {_config.DefaultAgent}");
        }

        // Step 2: If target matches current agent, try signing directly
        if (targetAgent == _currentAgent && _currentAgent != null)
        {
            if (!_failureCache.IsFailureCached(fingerprint, _currentAgent))
            {
                var sig = await TrySignAsync(keyBlob, data, flags, ct);
                if (sig != null)
                {
                    Log($"  Signed by {_currentAgent}");
                    SaveKeyMapping(fingerprint, _currentAgent);
                    _failureCache.ClearFailure(fingerprint, _currentAgent);
                    _ = EnsureOtherAgentsRunningAsync(_currentAgent, ct);
                    return SshAgentMessage.SignResponse(sig);
                }
                Log($"  Sign failed on current agent {_currentAgent}");
                _failureCache.CacheFailure(fingerprint, _currentAgent);
            }
            else
            {
                Log($"  Skipping {_currentAgent} (cached failure)");
            }
        }

        // Step 3: Try target agent (if different from current)
        if (targetAgent != null && targetAgent != _currentAgent)
        {
            if (!_failureCache.IsFailureCached(fingerprint, targetAgent))
            {
                Log($"  Switching to target agent: {targetAgent}...");
                await ForceSwitchToAsync(targetAgent, startOthers: false, ct);
                await Task.Delay(500, ct);

                var sig = await TrySignAsync(keyBlob, data, flags, ct);
                if (sig != null)
                {
                    Log($"  Signed by {targetAgent}");
                    SaveKeyMapping(fingerprint, targetAgent);
                    _failureCache.ClearFailure(fingerprint, targetAgent);
                    _ = EnsureOtherAgentsRunningAsync(targetAgent, ct);
                    return SshAgentMessage.SignResponse(sig);
                }
                Log($"  Sign failed on {targetAgent}");
                _failureCache.CacheFailure(fingerprint, targetAgent);
            }
            else
            {
                Log($"  Skipping {targetAgent} (cached failure)");
            }
        }

        // Step 4: Try other agents in priority order
        Log($"  Trying other agents...");
        foreach (var (agentName, _) in _config.GetAgentsByPriority())
        {
            // Skip already tried agents
            if (agentName == _currentAgent || agentName == targetAgent)
                continue;

            if (_failureCache.IsFailureCached(fingerprint, agentName))
            {
                Log($"    Skipping {agentName} (cached failure)");
                continue;
            }

            Log($"    Trying {agentName}...");
            await ForceSwitchToAsync(agentName, startOthers: false, ct);
            await Task.Delay(500, ct);

            var sig = await TrySignAsync(keyBlob, data, flags, ct);
            if (sig != null)
            {
                Log($"  Signed by {agentName}");
                SaveKeyMapping(fingerprint, agentName);
                _failureCache.ClearFailure(fingerprint, agentName);
                _ = EnsureOtherAgentsRunningAsync(agentName, ct);
                return SshAgentMessage.SignResponse(sig);
            }
            Log($"    Sign failed on {agentName}");
            _failureCache.CacheFailure(fingerprint, agentName);
        }

        Log("  Sign failed on all agents");
        return SshAgentMessage.Failure();
    }

    private async Task<byte[]?> TrySignAsync(byte[] keyBlob, byte[] data, uint flags, CancellationToken ct)
    {
        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
        {
            Log("    Sign: backend not connected");
            return null;
        }

        try
        {
            return await client.SignAsync(keyBlob, data, flags, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            Log($"    Sign error: {ex.Message}");
            return null;
        }
    }

    private async Task<SshAgentMessage> ForwardRequestAsync(SshAgentMessage request, CancellationToken ct)
    {
        Log($"Request: Forward {request.Type}");

        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
            return SshAgentMessage.Failure();

        try
        {
            var response = await client.SendAsync(request, ct);
            return response ?? SshAgentMessage.Failure();
        }
        catch (Exception ex)
        {
            Log($"  Error: {ex.Message}");
            return SshAgentMessage.Failure();
        }
    }

    private async Task KillProcessAsync(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
        {
            Log($"  {processName} is not running");
            return;
        }

        Log($"  Stopping {processName} ({processes.Length} processes)...");
        try
        {
            // Use PowerShell CIM (WMIC replacement, works across sessions)
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='{processName}.exe'\\\" | Invoke-CimMethod -MethodName Terminate\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                var error = await proc.StandardError.ReadToEndAsync();
                if (!string.IsNullOrEmpty(error))
                    Log($"    CIM: {error.Trim()}");
            }

            // Wait for process to fully terminate
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                var remaining = Process.GetProcessesByName(processName);
                if (remaining.Length == 0)
                {
                    Log($"    Stopped");
                    return;
                }
            }
            Log($"    Warning: Some processes may still be running");
        }
        catch (Exception ex)
        {
            Log($"    Warning: {ex.Message}");
        }
    }

    private void StartProcessIfNeeded(string processName, string exePath)
    {
        // Skip if already running
        var existing = Process.GetProcessesByName(processName);
        if (existing.Length > 0)
        {
            Log($"  {processName} is already running");
            return;
        }

        if (!File.Exists(exePath))
        {
            Log($"  Warning: {exePath} not found");
            return;
        }

        try
        {
            Log($"  Starting {processName}...");
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            });
        }
        catch (Exception ex)
        {
            Log($"  Error: {ex.Message}");
        }
    }

    private void Log(string message) => OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
    }
}
