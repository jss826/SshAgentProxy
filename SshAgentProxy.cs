using System.Diagnostics;
using SshAgentProxy.Pipes;
using SshAgentProxy.Protocol;
using SshAgentProxy.UI;

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

        // Load key mappings from config (including cached key data)
        var agentsInMappings = new HashSet<string>();
        foreach (var mapping in config.KeyMappings)
        {
            if (!string.IsNullOrEmpty(mapping.Fingerprint))
            {
                _keyToAgent[mapping.Fingerprint] = mapping.Agent;
                agentsInMappings.Add(mapping.Agent);

                // If we have full key data, add to cached keys
                if (!string.IsNullOrEmpty(mapping.KeyBlob))
                {
                    try
                    {
                        var keyBlob = Convert.FromBase64String(mapping.KeyBlob);
                        _allKeys.Add(new SshIdentity(keyBlob, mapping.Comment ?? ""));
                    }
                    catch
                    {
                        // Invalid base64, ignore
                    }
                }
            }
        }

        // If keyMappings reference 2+ different agents, we have sufficient data
        // Skip initial scan to avoid unnecessary Bitwarden unlock prompts
        if (agentsInMappings.Count >= 2)
            _keysScanned = true;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        Log($"Starting proxy on pipe: {_config.ProxyPipeName}");
        Log($"Backend pipe: {_config.BackendPipeName}");
        Log($"Configured agents: {string.Join(", ", _config.Agents.Keys)}");
        Log($"Default agent: {_config.DefaultAgent}");

        if (_allKeys.Count > 0)
        {
            Log($"Loaded {_allKeys.Count} cached keys from config:");
            foreach (var key in _allKeys)
            {
                var agent = _keyToAgent.TryGetValue(key.Fingerprint, out var a) ? a : "?";
                Log($"  [{agent}] {key.Comment} ({key.Fingerprint})");
            }
        }

        if (_keysScanned)
        {
            Log("Skipping initial scan (keyMappings have 2+ agents)");
        }

        // Detect current agent from running processes (avoids Bitwarden unlock prompt)
        DetectCurrentAgentFromProcesses();

        _server.Start();
        Log("Proxy server started");
        Log("");
        Log("=== IMPORTANT ===");
        Log($"Set environment variable: SSH_AUTH_SOCK=\\\\.\\pipe\\{_config.ProxyPipeName}");
        Log("=================");
    }

    /// <summary>
    /// Detect current agent from running processes (no pipe query, avoids Bitwarden unlock).
    /// Uses the heuristic: Bitwarden running = Bitwarden owns pipe (it steals on start).
    /// </summary>
    private void DetectCurrentAgentFromProcesses()
    {
        Log("Detecting current agent from processes...");

        // Check which agents are running
        var bitwardenConfig = GetAgentConfig("Bitwarden");
        var onePasswordConfig = GetAgentConfig("1Password");

        bool bitwardenRunning = bitwardenConfig != null &&
            Process.GetProcessesByName(bitwardenConfig.ProcessName).Length > 0;
        bool onePasswordRunning = onePasswordConfig != null &&
            Process.GetProcessesByName(onePasswordConfig.ProcessName).Length > 0;

        if (bitwardenRunning)
        {
            // Bitwarden steals the pipe on start, so it likely owns it
            _currentAgent = "Bitwarden";
            Log($"  Bitwarden is running - assuming it owns the pipe");
        }
        else if (onePasswordRunning)
        {
            // Only 1Password is running - it might have the pipe, or pipe might be orphaned
            // We'll verify with a lightweight scan (1Password doesn't require unlock)
            _currentAgent = "1Password";
            Log($"  1Password is running - assuming it owns the pipe (will verify on first request)");
        }
        else
        {
            // Neither running - no one owns the pipe
            _currentAgent = null;
            Log($"  No agents running - pipe is available");
        }
    }

    /// <summary>
    /// Quick detection of which agent owns the pipe based on key mappings.
    /// Used when _currentAgent becomes null (e.g., after background agent start).
    /// Only used for 1Password (doesn't trigger unlock prompt).
    /// </summary>
    private async Task DetectCurrentAgentFromPipeAsync(CancellationToken ct)
    {
        Log("  Re-detecting current agent from pipe...");

        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
        {
            Log("    No backend available");
            return;
        }

        try
        {
            var keys = await client.RequestIdentitiesAsync(ct);
            if (keys.Count == 0)
            {
                Log("    No keys in pipe");
                return;
            }

            // Check first key's mapping to determine current agent
            foreach (var key in keys)
            {
                if (_keyToAgent.TryGetValue(key.Fingerprint, out var agent))
                {
                    _currentAgent = agent;
                    Log($"    Detected: {agent} (from key {key.Fingerprint})");
                    return;
                }
            }
            Log("    Could not determine agent from keys");
        }
        catch (Exception ex)
        {
            Log($"    Detection error: {ex.Message}");
        }
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

                // Map unmapped keys to detected agent and persist
                foreach (var key in keys)
                {
                    if (!_keyToAgent.ContainsKey(key.Fingerprint))
                    {
                        SaveKeyMapping(key, detected);
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
    private void SaveKeyMapping(string fingerprint, string agent, byte[]? keyBlob = null, string? comment = null)
    {
        // Update in-memory mapping
        _keyToAgent[fingerprint] = agent;

        // Check if already in config
        var existing = _config.KeyMappings.FirstOrDefault(m => m.Fingerprint == fingerprint);
        if (existing != null)
        {
            if (existing.Agent == agent && existing.KeyBlob != null)
                return; // No change needed (already has full data)
            existing.Agent = agent;
            // Update key data if provided
            if (keyBlob != null)
                existing.KeyBlob = Convert.ToBase64String(keyBlob);
            if (comment != null)
                existing.Comment = comment;
        }
        else
        {
            var mapping = new KeyMapping { Fingerprint = fingerprint, Agent = agent };
            if (keyBlob != null)
                mapping.KeyBlob = Convert.ToBase64String(keyBlob);
            if (comment != null)
                mapping.Comment = comment;
            _config.KeyMappings.Add(mapping);
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
    /// Save a key mapping with full key data from SshIdentity
    /// </summary>
    private void SaveKeyMapping(SshIdentity key, string agent)
    {
        SaveKeyMapping(key.Fingerprint, agent, key.PublicKeyBlob, key.Comment);
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
        return ForceSwitchToAsync(agentName, startOthers: false, ct);
    }

    public Task ForceSwitchToAsync(string agentName, bool startOthers, CancellationToken ct = default)
    {
        return SwitchToAsync(agentName, startOthers, force: true, ct);
    }

    /// <summary>
    /// Switch to agent for signing - kills current agent so target can take the pipe
    /// </summary>
    private async Task SwitchToAgentForSigningAsync(string agentName, CancellationToken ct)
    {
        var agent = GetAgentConfig(agentName);
        if (agent == null) return;

        // Kill current agent only (not all agents) so target can take the pipe
        if (_currentAgent != null && _currentAgent != agentName)
        {
            var currentConfig = GetAgentConfig(_currentAgent);
            if (currentConfig != null)
            {
                await KillProcessAsync(currentConfig.ProcessName);
                await Task.Delay(500, ct);
            }
        }

        // Start target agent
        StartProcessIfNeeded(agent.ProcessName, agent.ExePath);
        await Task.Delay(3000, ct);
        _currentAgent = agentName;

        // Trigger unlock prompt by requesting identities
        // Bitwarden shows unlock dialog on RequestIdentities, not on SignRequest
        await TriggerAgentUnlockAsync(agentName, ct);
    }

    /// <summary>
    /// Trigger agent unlock by sending RequestIdentities.
    /// Bitwarden shows unlock dialog on RequestIdentities, not on SignRequest.
    /// </summary>
    private async Task TriggerAgentUnlockAsync(string agentName, CancellationToken ct)
    {
        Log($"  Triggering {agentName} unlock prompt...");

        for (int retry = 0; retry < 10; retry++)
        {
            using var client = await ConnectToBackendAsync(ct);
            if (client == null)
            {
                await Task.Delay(1000, ct);
                continue;
            }

            try
            {
                var keys = await client.RequestIdentitiesAsync(ct);
                if (keys.Count > 0)
                {
                    Log($"    {agentName} unlocked, {keys.Count} keys available");
                    return;
                }
                Log($"    Waiting for unlock... ({retry + 1}/10)");
            }
            catch (Exception ex)
            {
                Log($"    Error: {ex.Message}");
            }
            await Task.Delay(2000, ct);
        }
        Log($"    {agentName} unlock timeout");
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
            Log($"  → Add it to 'agents' in config: {_config.ConfigPath}");
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

    public async Task SwitchToAsync(string agentName, CancellationToken ct = default)
    {
        await SwitchToAsync(agentName, startOthers: false, force: false, ct);
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
            Log($"  → Add it to 'agents' in config: {_config.ConfigPath}");
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

    private async Task<SshAgentMessage> HandleRequestAsync(SshAgentMessage request, ClientContext context, CancellationToken ct)
    {
        // Acquire lock for thread safety (multiple clients can connect concurrently)
        await _stateLock.WaitAsync(ct);
        try
        {
            return request.Type switch
            {
                SshAgentMessageType.SSH_AGENTC_REQUEST_IDENTITIES => await HandleRequestIdentitiesAsync(context, ct),
                SshAgentMessageType.SSH_AGENTC_SIGN_REQUEST => await HandleSignRequestAsync(request.Payload, ct),
                _ => await ForwardRequestAsync(request, ct),
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<SshAgentMessage> HandleRequestIdentitiesAsync(ClientContext context, CancellationToken ct)
    {
        Log("Request: List identities");

        List<SshIdentity> keysToReturn;

        if (_keysScanned && _allKeys.Count > 0)
        {
            keysToReturn = new List<SshIdentity>(_allKeys);
        }
        else
        {
            // Scan all agents to build complete key list (needed for first run)
            await ScanAllAgentsAsync(ct);
            keysToReturn = new List<SshIdentity>(_allKeys);
        }

        if (keysToReturn.Count == 0)
        {
            Log("  No keys found from any agent");
            return SshAgentMessage.Failure();
        }

        // Try to get SSH connection info for smart key selection
        var connectionInfo = context.GetConnectionInfo();
        string? matchedFingerprint = null;

        if (connectionInfo != null)
        {
            Log($"  Detected connection: {connectionInfo}");

            // Check hostKeyMappings for a matching pattern
            foreach (var mapping in _config.HostKeyMappings)
            {
                if (connectionInfo.MatchesPattern(mapping.Pattern))
                {
                    Log($"  Matched pattern: {mapping.Pattern} -> {mapping.Fingerprint}");
                    matchedFingerprint = mapping.Fingerprint;
                    break;
                }
            }

            // If we found a matching key, prioritize it (move to front)
            if (matchedFingerprint != null)
            {
                var matchedKey = keysToReturn.FirstOrDefault(k => k.Fingerprint == matchedFingerprint);
                if (matchedKey != null)
                {
                    keysToReturn.Remove(matchedKey);
                    keysToReturn.Insert(0, matchedKey);
                    Log($"  Prioritized key: {matchedKey.Comment} ({matchedKey.Fingerprint})");
                }
            }
        }
        else
        {
            Log("  Could not detect connection info from client process");
        }

        // Show key selection dialog if no host pattern matched and multiple keys available
        if (matchedFingerprint == null && keysToReturn.Count > 1)
        {
            Log($"  Showing key selection dialog ({keysToReturn.Count} keys available)...");

            var selectedKeys = KeySelectionDialog.ShowDialog(
                keysToReturn,
                _keyToAgent,
                _config.KeySelectionTimeoutSeconds);

            if (selectedKeys != null && selectedKeys.Count > 0)
            {
                keysToReturn = selectedKeys;
                Log($"  User selected {keysToReturn.Count} key(s)");

                // Auto-save host key mapping if we have connection info
                if (connectionInfo != null && selectedKeys.Count == 1)
                {
                    var selectedKey = selectedKeys[0];
                    var owner = connectionInfo.GetOwner();
                    var pattern = !string.IsNullOrEmpty(owner)
                        ? $"{connectionInfo.Host}:{owner}/*"
                        : $"{connectionInfo.Host}:*";

                    // Check if this pattern already exists
                    var existing = _config.HostKeyMappings.FirstOrDefault(m => m.Pattern == pattern);
                    if (existing == null)
                    {
                        _config.HostKeyMappings.Add(new HostKeyMapping
                        {
                            Pattern = pattern,
                            Fingerprint = selectedKey.Fingerprint,
                            Description = $"Auto-saved: {selectedKey.Comment}"
                        });
                        _config.Save();
                        Log($"  Saved host mapping: {pattern} -> {selectedKey.Fingerprint}");
                    }
                }
            }
            else
            {
                Log("  Dialog cancelled, returning all keys");
            }
        }

        // Log what we're returning
        Log($"  Returning {keysToReturn.Count} keys");
        foreach (var id in keysToReturn)
        {
            var agent = _keyToAgent.TryGetValue(id.Fingerprint, out var a) ? a : "?";
            Log($"    [{agent}] {id.Comment} ({id.Fingerprint})");
        }

        return SshAgentMessage.IdentitiesAnswer(keysToReturn);
    }

    /// <summary>
    /// Scan a single agent without killing others. Used for initial identity request.
    /// </summary>
    private async Task ScanSingleAgentAsync(string agentName, CancellationToken ct)
    {
        Log($"  Scanning {agentName}...");

        var agent = GetAgentConfig(agentName);
        if (agent == null)
        {
            Log($"    {agentName}: not configured");
            Log($"    → Add it to 'agents' in config: {_config.ConfigPath}");
            return;
        }

        // Start agent if not running (don't kill others)
        await EnsureAgentRunningAsync(agentName, ct);
        await Task.Delay(500, ct);

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
                    SaveKeyMapping(key, agentName);
                }
                if (!_allKeys.Any(k => k.Fingerprint == key.Fingerprint))
                {
                    _allKeys.Add(key);
                    Log($"      [{agentName}] {key.Comment} ({key.Fingerprint})");
                }
            }
            _keysScanned = _allKeys.Count > 0;
        }
        catch (Exception ex)
        {
            Log($"    {agentName}: error - {ex.Message}");
        }
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
        var agent = GetAgentConfig(agentName);
        if (agent == null)
        {
            Log($"    {agentName}: not configured");
            Log($"    → Add it to 'agents' in config: {_config.ConfigPath}");
            return;
        }

        Log($"    Scanning {agentName}...");

        // Start agent if not running (don't kill other agents)
        var processes = Process.GetProcessesByName(agent.ProcessName);
        if (processes.Length == 0)
        {
            Log($"      Starting {agentName}...");
            StartProcessIfNeeded(agent.ProcessName, agent.ExePath);
            await Task.Delay(3000, ct); // Wait for startup
        }

        // Track keys we had before scanning this agent
        var existingFingerprints = new HashSet<string>(_allKeys.Select(k => k.Fingerprint));

        // Wait for keys with retry (user may need to authenticate)
        for (int retry = 0; retry < 5; retry++)
        {
            await Task.Delay(2000, ct);

            using var client = await ConnectToBackendAsync(ct);
            if (client == null)
            {
                Log($"      {agentName}: waiting for pipe... ({retry + 1}/5)");
                continue;
            }

            try
            {
                var keys = await client.RequestIdentitiesAsync(ct);

                // Find NEW keys that weren't in previous agents
                var newKeys = keys.Where(k => !existingFingerprints.Contains(k.Fingerprint)).ToList();

                if (newKeys.Count > 0)
                {
                    Log($"    {agentName}: {newKeys.Count} new keys");
                    foreach (var key in newKeys)
                    {
                        SaveKeyMapping(key, agentName);
                        _allKeys.Add(key);
                        Log($"      [{agentName}] {key.Comment} ({key.Fingerprint})");
                    }
                    _currentAgent = agentName;
                    return; // Got new keys, done with this agent
                }

                // If we see keys but all are from previous agents, the new agent may not have the pipe yet
                if (keys.Count > 0 && newKeys.Count == 0)
                {
                    Log($"      {agentName}: only existing keys, waiting for agent to take pipe... ({retry + 1}/5)");
                    continue;
                }

                Log($"      {agentName}: 0 keys, waiting... ({retry + 1}/5)");
            }
            catch (Exception ex)
            {
                Log($"      {agentName}: error - {ex.Message}");
            }
        }
        Log($"    {agentName}: no new keys after retries");
    }

    private async Task<SshAgentMessage> HandleSignRequestAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var (keyBlob, data, flags) = SshAgentProtocol.ParseSignRequest(payload);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(keyBlob))[..16];

        Log($"Request: Sign with key {fingerprint}");

        // Step 0: Re-detect current agent from processes (avoids Bitwarden unlock)
        DetectCurrentAgentFromProcesses();

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
                var (sig, result) = await TrySignAsync(keyBlob, data, flags, ct);
                if (sig != null)
                {
                    Log($"  Signed by {_currentAgent}");
                    var comment = _allKeys.FirstOrDefault(k => k.Fingerprint == fingerprint)?.Comment;
                    SaveKeyMapping(fingerprint, _currentAgent, keyBlob, comment);
                    _failureCache.ClearFailure(fingerprint, _currentAgent);
                    return SshAgentMessage.SignResponse(sig);
                }

                // Handle orphaned pipe scenario: 1Password running but doesn't have the pipe
                // (happens when Bitwarden was running and exited)
                if (result == SignResult.ConnectionFailed && _currentAgent == "1Password")
                {
                    Log($"  Pipe may be orphaned, restarting 1Password...");
                    var agent = GetAgentConfig("1Password");
                    if (agent != null)
                    {
                        await KillProcessAsync(agent.ProcessName);
                        await Task.Delay(500, ct);
                        StartProcessIfNeeded(agent.ProcessName, agent.ExePath);
                        await Task.Delay(3000, ct);

                        // Retry after restart
                        (sig, result) = await TrySignAsync(keyBlob, data, flags, ct);
                        if (sig != null)
                        {
                            Log($"  Signed by 1Password (after restart)");
                            var comment = _allKeys.FirstOrDefault(k => k.Fingerprint == fingerprint)?.Comment;
                            SaveKeyMapping(fingerprint, "1Password", keyBlob, comment);
                            return SshAgentMessage.SignResponse(sig);
                        }
                    }
                }

                Log($"  Sign failed on current agent {_currentAgent}");
                // Only cache connection failures, not sign refusals (user may authenticate on retry)
                if (result == SignResult.ConnectionFailed)
                    _failureCache.CacheFailure(fingerprint, _currentAgent);
            }
            else
            {
                Log($"  Skipping {_currentAgent} (cached connection failure)");
            }
        }

        // Step 3: Try target agent (if different from current) with retries
        if (targetAgent != null && targetAgent != _currentAgent)
        {
            if (!_failureCache.IsFailureCached(fingerprint, targetAgent))
            {
                Log($"  Switching to target agent: {targetAgent}...");
                await SwitchToAgentForSigningAsync(targetAgent, ct);

                // Retry signing (user may need time to authenticate - allow ~15 seconds)
                for (int retry = 0; retry < 5; retry++)
                {
                    await Task.Delay(2000, ct);
                    var (sig, result) = await TrySignAsync(keyBlob, data, flags, ct);
                    if (sig != null)
                    {
                        Log($"  Signed by {targetAgent}");
                        var comment = _allKeys.FirstOrDefault(k => k.Fingerprint == fingerprint)?.Comment;
                        SaveKeyMapping(fingerprint, targetAgent, keyBlob, comment);
                        _failureCache.ClearFailure(fingerprint, targetAgent);
                        return SshAgentMessage.SignResponse(sig);
                    }
                    if (result == SignResult.ConnectionFailed)
                    {
                        _failureCache.CacheFailure(fingerprint, targetAgent);
                        break; // Connection failed, don't retry
                    }
                    Log($"    Sign pending on {targetAgent}, waiting... ({retry + 1}/5)");
                }
                Log($"  Sign failed on {targetAgent} after retries");
            }
            else
            {
                Log($"  Skipping {targetAgent} (cached connection failure)");
            }
        }

        // Step 4: Try other agents in priority order (only if no explicit mapping)
        // If key has a mapping, don't try other agents - user needs to authenticate with mapped agent
        if (mappedAgent == null)
        {
            Log($"  Trying other agents...");
            foreach (var (agentName, _) in _config.GetAgentsByPriority())
            {
                // Skip already tried agents
                if (agentName == _currentAgent || agentName == targetAgent)
                    continue;

                if (_failureCache.IsFailureCached(fingerprint, agentName))
                {
                    Log($"    Skipping {agentName} (cached connection failure)");
                    continue;
                }

                Log($"    Trying {agentName}...");
                await ForceSwitchToAsync(agentName, startOthers: false, ct);
                await Task.Delay(500, ct);

                var (sig, result) = await TrySignAsync(keyBlob, data, flags, ct);
                if (sig != null)
                {
                    Log($"  Signed by {agentName}");
                    var comment = _allKeys.FirstOrDefault(k => k.Fingerprint == fingerprint)?.Comment;
                    SaveKeyMapping(fingerprint, agentName, keyBlob, comment);
                    _failureCache.ClearFailure(fingerprint, agentName);
                    return SshAgentMessage.SignResponse(sig);
                }
                Log($"    Sign failed on {agentName}");
                if (result == SignResult.ConnectionFailed)
                    _failureCache.CacheFailure(fingerprint, agentName);
            }
        }
        else
        {
            Log($"  Key is mapped to {mappedAgent} - not trying other agents");
        }

        Log("  Sign failed on target agent");
        return SshAgentMessage.Failure();
    }

    private enum SignResult { Success, ConnectionFailed, SignFailed }

    private async Task<(byte[]? Signature, SignResult Result)> TrySignAsync(byte[] keyBlob, byte[] data, uint flags, CancellationToken ct)
    {
        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
        {
            Log("    Sign: backend not connected");
            return (null, SignResult.ConnectionFailed);
        }

        try
        {
            var sig = await client.SignAsync(keyBlob, data, flags, ct);
            return sig != null ? (sig, SignResult.Success) : (null, SignResult.SignFailed);
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            Log($"    Sign error: {ex.Message}");
            return (null, SignResult.SignFailed);
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

        // Only check File.Exists if it's a full path (contains directory separator)
        // If it's just a filename, assume it's in PATH
        var isFullPath = exePath.Contains(Path.DirectorySeparatorChar) || exePath.Contains(Path.AltDirectorySeparatorChar);
        if (isFullPath && !File.Exists(exePath))
        {
            Log($"  Warning: {exePath} not found");
            Log($"  → Check 'exePath' in config: {_config.ConfigPath}");
            return;
        }

        try
        {
            Log($"  Starting {processName}...");
            // Use 'cmd /c start' to launch the process independently
            // This ensures the child process is not terminated when the proxy exits
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{exePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
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
