using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SshAgentProxy.Protocol;

namespace SshAgentProxy.Pipes;

public class NamedPipeAgentServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<SshAgentMessage, ClientContext, CancellationToken, Task<SshAgentMessage>> _handler;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public string PipeName => _pipeName;

    public event Action<string>? OnLog;

    /// <summary>
    /// Called when a client disconnects. Used to detect stale hostKeyMappings
    /// (identity offered but no sign request received).
    /// </summary>
    public event Action<ClientContext>? OnClientDisconnected;

    public NamedPipeAgentServer(string pipeName, Func<SshAgentMessage, ClientContext, CancellationToken, Task<SshAgentMessage>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = ListenAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_listenTask != null)
        {
            try { await _listenTask; } catch { }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        Log($"Server starting on pipe: {_pipeName}");

        // Cache PipeSecurity outside the loop (avoids repeated WindowsIdentity.GetCurrent() calls)
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        if (currentUser != null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    security);

                Log("Waiting for connection...");
                await pipe.WaitForConnectionAsync(ct);
                var clientPid = GetClientProcessId(pipe);
                Log($"Client connected (PID: {clientPid})");

                _ = HandleClientAsync(pipe, clientPid, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Error accepting connection: {ex.Message}");
                await Task.Delay(100, ct);
            }
        }

        Log("Server stopped");
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, int clientPid, CancellationToken ct)
    {
        var context = new ClientContext(clientPid);

        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var request = await SshAgentProtocol.ReadMessageAsync(pipe, ct);
                if (request == null)
                {
                    Log("Client disconnected (null message)");
                    break;
                }

                Log($"Received: {request.Value.Type}");

                var response = await _handler(request.Value, context, ct);
                await SshAgentProtocol.WriteMessageAsync(pipe, response, ct);

                Log($"Sent: {response.Type}");
            }
        }
        catch (Exception ex)
        {
            Log($"Error handling client: {ex.Message}");
        }
        finally
        {
            try { OnClientDisconnected?.Invoke(context); }
            catch (Exception ex) { Log($"Error in disconnect handler: {ex.Message}"); }
            pipe.Dispose();
        }
    }

    private static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        try
        {
            // Get client process ID via Windows API
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId))
                return (int)processId;
        }
        catch { }
        return -1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

    private void Log(string message) => OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

/// <summary>
/// Context information about the connected client
/// </summary>
public class ClientContext
{
    public int ProcessId { get; }
    public SshConnectionInfo? ConnectionInfo { get; private set; }
    private bool _connectionInfoResolved;

    /// <summary>
    /// The hostKeyMapping pattern that was matched during identity request (null if none)
    /// </summary>
    public string? MatchedPattern { get; set; }

    /// <summary>
    /// The fingerprint returned via hostKeyMapping match (null if none)
    /// </summary>
    public string? MatchedFingerprint { get; set; }

    /// <summary>
    /// Whether a sign request was received during this session
    /// </summary>
    public bool SignRequested { get; set; }

    /// <summary>
    /// The fingerprint selected by user dialog (for deferred auto-save)
    /// </summary>
    public string? PendingSaveFingerprint { get; set; }

    /// <summary>
    /// The pattern to use for deferred auto-save
    /// </summary>
    public string? PendingSavePattern { get; set; }

    /// <summary>
    /// The comment for deferred auto-save
    /// </summary>
    public string? PendingSaveComment { get; set; }

    public ClientContext(int processId)
    {
        ProcessId = processId;
    }

    /// <summary>
    /// Get SSH connection info (lazy-loaded and cached)
    /// </summary>
    public SshConnectionInfo? GetConnectionInfo()
    {
        if (!_connectionInfoResolved)
        {
            ConnectionInfo = ProcessHelper.GetSshConnectionInfo(ProcessId);
            _connectionInfoResolved = true;
        }
        return ConnectionInfo;
    }
}
