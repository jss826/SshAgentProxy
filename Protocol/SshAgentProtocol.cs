using System.Buffers.Binary;

namespace SshAgentProxy.Protocol;

public static class SshAgentProtocol
{
    public static async Task<SshAgentMessage?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        // Read 4-byte length header with proper loop (partial reads are valid on pipes)
        var lengthBuffer = new byte[4];
        var headerRead = 0;
        while (headerRead < 4)
        {
            var bytesRead = await stream.ReadAsync(lengthBuffer.AsMemory(headerRead, 4 - headerRead), ct);
            if (bytesRead == 0)
            {
                if (headerRead == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Protocol] Read 0 bytes - client disconnected");
                    return null;
                }
                throw new InvalidDataException("Unexpected end of stream while reading header");
            }
            headerRead += bytesRead;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (length == 0 || length > 256 * 1024)
            throw new InvalidDataException($"Invalid message length: {length}");

        // Read payload with loop
        var payload = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var bytesRead = await stream.ReadAsync(payload.AsMemory(totalRead, (int)length - totalRead), ct);
            if (bytesRead == 0)
                throw new InvalidDataException("Unexpected end of stream");
            totalRead += bytesRead;
        }

        return new SshAgentMessage((SshAgentMessageType)payload[0], payload.AsMemory(1));
    }

    public static async Task WriteMessageAsync(Stream stream, SshAgentMessage message, CancellationToken ct = default)
    {
        var length = 1 + message.Payload.Length;
        var buffer = new byte[4 + length];

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)length);
        buffer[4] = (byte)message.Type;
        message.Payload.Span.CopyTo(buffer.AsSpan(5));

        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    public static byte[] CreateIdentitiesAnswer(IReadOnlyList<SshIdentity> identities)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteUInt32BigEndian(writer, (uint)identities.Count);

        foreach (var identity in identities)
        {
            WriteString(writer, identity.PublicKeyBlob);
            WriteString(writer, identity.Comment);
        }

        return ms.ToArray();
    }

    public static (byte[] keyBlob, byte[] data, uint flags) ParseSignRequest(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        var offset = 0;

        // Validate keyBlob length
        if (offset + 4 > span.Length)
            throw new InvalidDataException("Sign request too short: missing keyBlob length");
        var keyBlobLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
        offset += 4;
        if (keyBlobLen < 0 || offset + keyBlobLen > span.Length)
            throw new InvalidDataException($"Sign request invalid: keyBlob length {keyBlobLen} exceeds payload");
        var keyBlob = span.Slice(offset, keyBlobLen).ToArray();
        offset += keyBlobLen;

        // Validate data length
        if (offset + 4 > span.Length)
            throw new InvalidDataException("Sign request too short: missing data length");
        var dataLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
        offset += 4;
        if (dataLen < 0 || offset + dataLen > span.Length)
            throw new InvalidDataException($"Sign request invalid: data length {dataLen} exceeds payload");
        var data = span.Slice(offset, dataLen).ToArray();
        offset += dataLen;

        uint flags = 0;
        if (offset + 4 <= span.Length)
        {
            flags = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
        }

        return (keyBlob, data, flags);
    }

    public static List<SshIdentity> ParseIdentitiesAnswer(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        var offset = 0;
        var identities = new List<SshIdentity>();

        if (span.Length < 4)
            throw new InvalidDataException("Identities answer too short: missing count");
        var count = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
        offset += 4;

        // Sanity check: limit to reasonable number of identities
        if (count > 1000)
            throw new InvalidDataException($"Identities answer invalid: count {count} exceeds limit");

        for (var i = 0; i < count; i++)
        {
            // Validate keyBlob length
            if (offset + 4 > span.Length)
                throw new InvalidDataException($"Identities answer truncated at identity {i}: missing keyBlob length");
            var keyBlobLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            offset += 4;
            if (keyBlobLen < 0 || offset + keyBlobLen > span.Length)
                throw new InvalidDataException($"Identities answer invalid: keyBlob length {keyBlobLen} exceeds payload");
            var keyBlob = span.Slice(offset, keyBlobLen).ToArray();
            offset += keyBlobLen;

            // Validate comment length
            if (offset + 4 > span.Length)
                throw new InvalidDataException($"Identities answer truncated at identity {i}: missing comment length");
            var commentLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            offset += 4;
            if (commentLen < 0 || offset + commentLen > span.Length)
                throw new InvalidDataException($"Identities answer invalid: comment length {commentLen} exceeds payload");
            var comment = System.Text.Encoding.UTF8.GetString(span.Slice(offset, commentLen));
            offset += commentLen;

            identities.Add(new SshIdentity(keyBlob, comment));
        }

        return identities;
    }

    private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        writer.Write(buffer);
    }

    private static void WriteString(BinaryWriter writer, byte[] data)
    {
        WriteUInt32BigEndian(writer, (uint)data.Length);
        writer.Write(data);
    }

    private static void WriteString(BinaryWriter writer, string data)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        WriteUInt32BigEndian(writer, (uint)bytes.Length);
        writer.Write(bytes);
    }
}

public readonly record struct SshAgentMessage(SshAgentMessageType Type, ReadOnlyMemory<byte> Payload)
{
    public static SshAgentMessage Failure() => new(SshAgentMessageType.SSH_AGENT_FAILURE, ReadOnlyMemory<byte>.Empty);
    public static SshAgentMessage Success() => new(SshAgentMessageType.SSH_AGENT_SUCCESS, ReadOnlyMemory<byte>.Empty);

    public static SshAgentMessage IdentitiesAnswer(IReadOnlyList<SshIdentity> identities)
        => new(SshAgentMessageType.SSH_AGENT_IDENTITIES_ANSWER, SshAgentProtocol.CreateIdentitiesAnswer(identities));

    public static SshAgentMessage SignResponse(byte[] signature)
    {
        var payload = new byte[4 + signature.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), (uint)signature.Length);
        signature.CopyTo(payload.AsSpan(4));
        return new(SshAgentMessageType.SSH_AGENT_SIGN_RESPONSE, payload);
    }
}

public record SshIdentity(byte[] PublicKeyBlob, string Comment)
{
    public string Fingerprint => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PublicKeyBlob))[..16];
}
