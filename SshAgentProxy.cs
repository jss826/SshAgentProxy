using System.Diagnostics;
using System.Linq;
using SshAgentProxy.Pipes;
using SshAgentProxy.Protocol;

namespace SshAgentProxy;

public class SshAgentProxyService : IAsyncDisposable
{
    private readonly Config _config;
    private readonly NamedPipeAgentServer _server;
    private string _currentAgent;
    private readonly Dictionary<string, string> _keyToAgent = new(); // fingerprint -> agent name
    private readonly List<SshIdentity> _allKeys = new(); // 全agentの鍵（マージ用）
    private bool _keysScanned = false;

    public event Action<string>? OnLog;
    public string CurrentAgent => _currentAgent;

    public SshAgentProxyService(Config config)
    {
        _config = config;
        _currentAgent = config.DefaultAgent;
        _server = new NamedPipeAgentServer(config.ProxyPipeName, HandleRequestAsync);
        _server.OnLog += msg => OnLog?.Invoke(msg);

        // 設定からキーマッピングを読み込み
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

        // 現在のbackendから鍵を取得（起動/切り替えはしない）
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

        // 現在のbackendから鍵を取得（起動/切り替えはしない）
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
                // 既知のマッピングがなければ追加（現在のagentとして）
                if (!_keyToAgent.ContainsKey(key.Fingerprint))
                {
                    _keyToAgent[key.Fingerprint] = _currentAgent;
                }

                // 重複チェック
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
        // 強制切り替え（現在のagent状態を無視）
        _currentAgent = agentName == "1Password" ? "Bitwarden" : "1Password";
        return SwitchToAsync(agentName, startSecondary, ct);
    }

    /// <summary>
    /// 指定したagentが起動していなければ起動する（他のagentは終了しない）
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
        await Task.Delay(3000, ct); // 起動を待つ
        _currentAgent = agentName;
        Log($"{agentName} started");
    }

    public async Task SwitchToAsync(string agentName, CancellationToken ct = default)
    {
        await SwitchToAsync(agentName, startSecondary: true, ct);
    }

    public async Task SwitchToAsync(string agentName, bool startSecondary, CancellationToken ct = default)
    {
        if (_currentAgent == agentName)
        {
            Log($"Already using {agentName}");
            return;
        }

        Log($"Switching from {_currentAgent} to {agentName}...");

        var (primary, secondary) = agentName == "1Password"
            ? (_config.Agents.OnePassword, _config.Agents.Bitwarden)
            : (_config.Agents.Bitwarden, _config.Agents.OnePassword);

        // 1. ヘルパープロセスを終了（パイプを解放させる）
        await KillProcessAsync(primary.ProcessName);
        await KillProcessAsync(secondary.ProcessName);
        await Task.Delay(1000, ct);

        // 2. プライマリを先に起動（pipeを取得）
        StartProcessIfNeeded(primary.ProcessName, primary.ExePath);
        await Task.Delay(3000, ct); // 起動を待つ

        // 3. セカンダリを起動（オプション）
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
        return request.Type switch
        {
            SshAgentMessageType.SSH_AGENTC_REQUEST_IDENTITIES => await HandleRequestIdentitiesAsync(ct),
            SshAgentMessageType.SSH_AGENTC_SIGN_REQUEST => await HandleSignRequestAsync(request.Payload, ct),
            _ => await ForwardRequestAsync(request, ct),
        };
    }

    private Task<SshAgentMessage> HandleRequestIdentitiesAsync(CancellationToken ct)
    {
        Log("Request: List identities");

        if (_keysScanned && _allKeys.Count > 0)
        {
            // スキャン済みならマージされた全鍵を返す
            Log($"  Returning {_allKeys.Count} merged keys");
            foreach (var id in _allKeys)
            {
                var agent = _keyToAgent.TryGetValue(id.Fingerprint, out var a) ? a : "?";
                Log($"    [{agent}] {id.Comment} ({id.Fingerprint})");
            }
            return Task.FromResult(SshAgentMessage.IdentitiesAnswer(_allKeys));
        }

        // スキャン未完了の場合は現在のbackendから取得
        return HandleRequestIdentitiesFromBackendAsync(ct);
    }

    private async Task<SshAgentMessage> HandleRequestIdentitiesFromBackendAsync(CancellationToken ct)
    {
        // まず現在のbackendに接続を試みる
        var client = await ConnectToBackendAsync(ct);

        // 接続できなければ、デフォルトagentを起動
        if (client == null)
        {
            Log($"  No backend, starting {_config.DefaultAgent}...");
            await EnsureAgentRunningAsync(_config.DefaultAgent, ct);
            await Task.Delay(500, ct); // パイプ安定化待ち
            client = await ConnectToBackendAsync(ct);
        }

        if (client == null)
        {
            Log("  Failed to connect to backend");
            return SshAgentMessage.Failure();
        }

        using (client)
        {
            try
            {
                var identities = await client.RequestIdentitiesAsync(ct);
                Log($"  Found {identities.Count} keys from {_currentAgent}");

                foreach (var id in identities)
                {
                    // マッピングに追加
                    if (!_keyToAgent.ContainsKey(id.Fingerprint))
                    {
                        _keyToAgent[id.Fingerprint] = _currentAgent;
                    }
                    if (!_allKeys.Any(k => k.Fingerprint == id.Fingerprint))
                    {
                        _allKeys.Add(id);
                    }
                    Log($"    - {id.Comment} ({id.Fingerprint})");
                }

                _keysScanned = identities.Count > 0;
                return SshAgentMessage.IdentitiesAnswer(identities);
            }
            catch (Exception ex)
            {
                Log($"  Error: {ex.Message}");
                return SshAgentMessage.Failure();
            }
        }
    }

    private async Task<SshAgentMessage> HandleSignRequestAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var (keyBlob, data, flags) = SshAgentProtocol.ParseSignRequest(payload);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(keyBlob))[..16];

        Log($"Request: Sign with key {fingerprint}");

        // キーマッピングがあれば、そのagentを使う
        if (_keyToAgent.TryGetValue(fingerprint, out var mappedAgent))
        {
            Log($"  Key mapped to {mappedAgent}");

            // 現在のagentが違う場合のみ切り替え
            if (_currentAgent != mappedAgent)
            {
                Log($"  Switching to {mappedAgent}...");
                await ForceSwitchToAsync(mappedAgent, startSecondary: false, ct);
            }

            var signature = await TrySignAsync(keyBlob, data, flags, ct);
            if (signature != null)
            {
                Log($"  Signed by {mappedAgent}");
                return SshAgentMessage.SignResponse(signature);
            }
            // マッピングが古い場合、クリアしてフォールバック
            Log($"  Mapped agent failed, trying fallback...");
            _keyToAgent.Remove(fingerprint);
        }

        // 現在のバックエンドで試す（起動中の場合）
        Log($"  Trying current backend...");
        var sig = await TrySignAsync(keyBlob, data, flags, ct);
        if (sig != null)
        {
            _keyToAgent[fingerprint] = _currentAgent;
            Log($"  Signed by {_currentAgent} (mapping saved)");
            return SshAgentMessage.SignResponse(sig);
        }

        // バックエンド未接続 or 署名失敗 → 1Passwordに切り替えて試す
        if (_currentAgent != "1Password")
        {
            Log($"  Switching to 1Password...");
            await ForceSwitchToAsync("1Password", startSecondary: false, ct);
        }
        else
        {
            // 1Passwordが_currentAgentなのに接続できなかった → 起動する
            Log($"  Starting 1Password...");
            await EnsureAgentRunningAsync("1Password", ct);
        }
        await Task.Delay(500, ct);

        sig = await TrySignAsync(keyBlob, data, flags, ct);
        if (sig != null)
        {
            _keyToAgent[fingerprint] = "1Password";
            Log($"  Signed by 1Password (mapping saved)");
            return SshAgentMessage.SignResponse(sig);
        }

        // 1Password失敗 → Bitwardenに切り替えて試す
        Log($"  Switching to Bitwarden...");
        await ForceSwitchToAsync("Bitwarden", startSecondary: false, ct);

        sig = await TrySignAsync(keyBlob, data, flags, ct);
        if (sig != null)
        {
            _keyToAgent[fingerprint] = "Bitwarden";
            Log($"  Signed by Bitwarden (mapping saved)");
            return SshAgentMessage.SignResponse(sig);
        }

        Log("  Sign failed on both agents");
        return SshAgentMessage.Failure();
    }

    private async Task<byte[]?> TrySignAsync(byte[] keyBlob, byte[] data, uint flags, CancellationToken ct)
    {
        using var client = await ConnectToBackendAsync(ct);
        if (client == null)
            return null;

        try
        {
            return await client.SignAsync(keyBlob, data, flags, ct);
        }
        catch
        {
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
            // WMICを使用（セッション跨ぎに強い）
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = $"process where name='{processName}.exe' delete",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                var output = await proc.StandardOutput.ReadToEndAsync();
                if (!string.IsNullOrEmpty(output) && output.Contains("error", StringComparison.OrdinalIgnoreCase))
                    Log($"    wmic: {output.Trim()}");
            }

            // プロセスが完全に終了するまで待機
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
        // 既に起動していればスキップ
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
