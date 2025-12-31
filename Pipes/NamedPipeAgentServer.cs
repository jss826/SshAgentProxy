using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SshAgentProxy.Protocol;

namespace SshAgentProxy.Pipes;

public class NamedPipeAgentServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<SshAgentMessage, CancellationToken, Task<SshAgentMessage>> _handler;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public string PipeName => _pipeName;

    public event Action<string>? OnLog;

    public NamedPipeAgentServer(string pipeName, Func<SshAgentMessage, CancellationToken, Task<SshAgentMessage>> handler)
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Security: Only allow current user (ssh.exe runs as current user)
                var security = new PipeSecurity();
                var currentUser = WindowsIdentity.GetCurrent().User;
                if (currentUser != null)
                {
                    security.AddAccessRule(new PipeAccessRule(
                        currentUser,
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));
                }

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
                Log($"Client connected (PID: {GetClientProcessId(pipe)})");

                _ = HandleClientAsync(pipe, ct);
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

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
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

                var response = await _handler(request.Value, ct);
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
