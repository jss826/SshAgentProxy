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
    private string _currentAgent;
    private readonly Dictionary<string, string> _keyToAgent = new(); // fingerprint -> agent name
    private readonly List<SshIdentity> _allKeys = new(); // merged keys from all agents
    private bool _keysScanned = false;

    public event Action<string>? OnLog;
    public string CurrentAgent => _currentAgent;

    public SshAgentProxyService(Config config)
    {
        _config = config;
        _currentAgent = config.DefaultAgent;
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

        // Get keys from current backend (without starting/switching agents)
        await ScanKeysAsync(ct);

        _server.Start();
        Log("Proxy server started");
        Log("");
        Log("=== IMPORTANT ===");
        Log($"Set environment variable: SSH_AUTH_SOCK=\\\\.\\pipe\\{_config.ProxyPipeName}");
        Log("=================");
    }

    public async Task ScanKeysAsync(CancellationToken ct = default)
    {
        Log("Scanning keys from current backend...");

        // Get keys from current backend (without starting/switching agents)
        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
        {
            Log("  No backend available");
            return;
        }

        try
        {
            var keys = await client.RequestIdentitiesAsync(ct);
            Log($"  Found {keys.Count} keys");

            foreach (var key in keys)
            {
                // Add mapping if not already known (map to current agent)
                if (!_keyToAgent.ContainsKey(key.Fingerprint))
                {
                    _keyToAgent[key.Fingerprint] = _currentAgent;
                }

                // Check for duplicates
                if (!_allKeys.Any(k => k.Fingerprint == key.Fingerprint))
                {
                    _allKeys.Add(key);
                }

                var agent = _keyToAgent[key.Fingerprint];
                Log($"    [{agent}] {key.Comment} ({key.Fingerprint})");
            }
        }
        catch (Exception ex)
        {
            Log($"  Error: {ex.Message}");
        }

        _keysScanned = _allKeys.Count > 0;
        Log($"  Total: {_allKeys.Count} keys, {_keyToAgent.Count} mappings");
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
        return ForceSwitchToAsync(agentName, startSecondary: true, ct);
    }

    public Task ForceSwitchToAsync(string agentName, bool startSecondary, CancellationToken ct = default)
    {
        return SwitchToAsync(agentName, startSecondary, force: true, ct);
    }

    /// <summary>
    /// Start the specified agent if not running (does not stop other agents)
    /// </summary>
    public async Task EnsureAgentRunningAsync(string agentName, CancellationToken ct = default)
    {
        var agent = agentName == "1Password"
            ? _config.Agents.OnePassword
            : _config.Agents.Bitwarden;

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
    /// Start the secondary agent in the background (fire and forget).
    /// Exceptions are caught and logged since this is called without await.
    /// </summary>
    private async Task EnsureSecondaryAgentRunningAsync(string primaryAgent, CancellationToken ct)
    {
        try
        {
            var secondaryName = primaryAgent == "1Password" ? "Bitwarden" : "1Password";
            var secondary = primaryAgent == "1Password"
                ? _config.Agents.Bitwarden
                : _config.Agents.OnePassword;

            var processes = Process.GetProcessesByName(secondary.ProcessName);
            if (processes.Length > 0)
                return; // Already running

            Log($"  Starting {secondaryName} in background...");
            await Task.Delay(1000, ct); // Brief delay before starting secondary
            StartProcessIfNeeded(secondary.ProcessName, secondary.ExePath);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            Log($"  Warning: Failed to start secondary agent: {ex.Message}");
        }
    }

    public async Task SwitchToAsync(string agentName, CancellationToken ct = default)
    {
        await SwitchToAsync(agentName, startSecondary: true, force: false, ct);
    }

    public async Task SwitchToAsync(string agentName, bool startSecondary, bool force = false, CancellationToken ct = default)
    {
        if (!force && _currentAgent == agentName)
        {
            Log($"Already using {agentName}");
            return;
        }

        Log($"Switching from {_currentAgent} to {agentName}...");

        var (primary, secondary) = agentName == "1Password"
            ? (_config.Agents.OnePassword, _config.Agents.Bitwarden)
            : (_config.Agents.Bitwarden, _config.Agents.OnePassword);

        // 1. Kill processes to release the pipe
        await KillProcessAsync(primary.ProcessName);
        await KillProcessAsync(secondary.ProcessName);
        await Task.Delay(1000, ct);

        // 2. Start primary first (to acquire the pipe)
        StartProcessIfNeeded(primary.ProcessName, primary.ExePath);
        await Task.Delay(3000, ct); // Wait for startup

        // 3. Start secondary (optional)
        if (startSecondary)
        {
            StartProcessIfNeeded(secondary.ProcessName, secondary.ExePath);
            await Task.Delay(1000, ct);
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

        // Scan 1Password
        await ScanAgentAsync("1Password", ct);

        // Scan Bitwarden
        await ScanAgentAsync("Bitwarden", ct);

        _keysScanned = _allKeys.Count > 0;
        Log($"  Total: {_allKeys.Count} keys");
    }

    private async Task ScanAgentAsync(string agentName, CancellationToken ct)
    {
        Log($"    Scanning {agentName}...");

        // Switch to this agent if different from current
        if (_currentAgent != agentName)
        {
            await ForceSwitchToAsync(agentName, startSecondary: false, ct);
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

        // Use mapped agent if key mapping exists
        if (_keyToAgent.TryGetValue(fingerprint, out var mappedAgent))
        {
            Log($"  Key mapped to {mappedAgent}");

            // Switch only if current agent is different
            if (_currentAgent != mappedAgent)
            {
                Log($"  Switching to {mappedAgent}...");
                await ForceSwitchToAsync(mappedAgent, startSecondary: false, ct);
            }

            var signature = await TrySignAsync(keyBlob, data, flags, ct);
            if (signature != null)
            {
                Log($"  Signed by {mappedAgent}");
                _ = EnsureSecondaryAgentRunningAsync(mappedAgent, ct);
                return SshAgentMessage.SignResponse(signature);
            }
            // Clear stale mapping and try fallback
            Log($"  Mapped agent failed, trying fallback...");
            _keyToAgent.Remove(fingerprint);
        }

        // Try current backend (if running)
        Log($"  Trying current backend...");
        var sig = await TrySignAsync(keyBlob, data, flags, ct);
        if (sig != null)
        {
            _keyToAgent[fingerprint] = _currentAgent;
            Log($"  Signed by {_currentAgent} (mapping saved)");
            _ = EnsureSecondaryAgentRunningAsync(_currentAgent, ct);
            return SshAgentMessage.SignResponse(sig);
        }

        // Backend not connected or sign failed - try 1Password
        if (_currentAgent != "1Password")
        {
            Log($"  Switching to 1Password...");
            await ForceSwitchToAsync("1Password", startSecondary: false, ct);
        }
        else
        {
            // 1Password is current but not connected - start it
            Log($"  Starting 1Password...");
            await EnsureAgentRunningAsync("1Password", ct);
        }
        await Task.Delay(500, ct);

        sig = await TrySignAsync(keyBlob, data, flags, ct);
        if (sig != null)
        {
            _keyToAgent[fingerprint] = "1Password";
            Log($"  Signed by 1Password (mapping saved)");
            _ = EnsureSecondaryAgentRunningAsync("1Password", ct);
            return SshAgentMessage.SignResponse(sig);
        }

        // 1Password failed - try Bitwarden
        Log($"  Switching to Bitwarden...");
        await ForceSwitchToAsync("Bitwarden", startSecondary: false, ct);

        sig = await TrySignAsync(keyBlob, data, flags, ct);
        if (sig != null)
        {
            _keyToAgent[fingerprint] = "Bitwarden";
            Log($"  Signed by Bitwarden (mapping saved)");
            _ = EnsureSecondaryAgentRunningAsync("Bitwarden", ct);
            return SshAgentMessage.SignResponse(sig);
        }

        Log("  Sign failed on both agents");
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
