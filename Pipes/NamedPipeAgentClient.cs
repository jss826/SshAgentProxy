using System.IO.Pipes;
using SshAgentProxy.Protocol;

namespace SshAgentProxy.Pipes;

public class NamedPipeAgentClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;

    public string PipeName => _pipeName;

    public NamedPipeAgentClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    public async Task<bool> TryConnectAsync(int timeoutMs = 1000, CancellationToken ct = default)
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(timeoutMs, ct);
            return true;
        }
        catch
        {
            _pipe?.Dispose();
            _pipe = null;
            return false;
        }
    }

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public async Task<SshAgentMessage?> SendAsync(SshAgentMessage request, CancellationToken ct = default)
    {
        if (_pipe == null || !_pipe.IsConnected)
            throw new InvalidOperationException("Not connected");

        await SshAgentProtocol.WriteMessageAsync(_pipe, request, ct);
        return await SshAgentProtocol.ReadMessageAsync(_pipe, ct);
    }

    public async Task<List<SshIdentity>> RequestIdentitiesAsync(CancellationToken ct = default)
    {
        var request = new SshAgentMessage(SshAgentMessageType.SSH_AGENTC_REQUEST_IDENTITIES, ReadOnlyMemory<byte>.Empty);
        var response = await SendAsync(request, ct);

        if (response == null || response.Value.Type != SshAgentMessageType.SSH_AGENT_IDENTITIES_ANSWER)
            return [];

        return SshAgentProtocol.ParseIdentitiesAnswer(response.Value.Payload);
    }

    public async Task<byte[]?> SignAsync(byte[] keyBlob, byte[] data, uint flags = 0, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteUInt32BigEndian(writer, (uint)keyBlob.Length);
        writer.Write(keyBlob);
        WriteUInt32BigEndian(writer, (uint)data.Length);
        writer.Write(data);
        WriteUInt32BigEndian(writer, flags);

        var request = new SshAgentMessage(SshAgentMessageType.SSH_AGENTC_SIGN_REQUEST, ms.ToArray());
        var response = await SendAsync(request, ct);

        if (response == null || response.Value.Type != SshAgentMessageType.SSH_AGENT_SIGN_RESPONSE)
            return null;

        // Parse signature from response with bounds checking
        var payload = response.Value.Payload.Span;
        if (payload.Length < 4)
            throw new InvalidDataException("Sign response too short: missing signature length");
        var sigLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload);
        if (sigLen < 0 || 4 + sigLen > payload.Length)
            throw new InvalidDataException($"Sign response invalid: signature length {sigLen} exceeds payload");
        return payload.Slice(4, sigLen).ToArray();
    }

    private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        writer.Write(buffer);
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _pipe = null;
    }
}
