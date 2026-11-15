using System.Buffers.Binary;

namespace AppleDeviceHub.Core.Engine;

/// <summary>Why a frame or message was refused (windows/protocol/clasp-engine-v1 section 2).</summary>
public enum EngineFrameError
{
    /// <summary>Length prefix above <see cref="EngineFraming.MaxFrameLength"/>.</summary>
    Oversized = 1,

    /// <summary>Length prefix of zero.</summary>
    ZeroLength = 2,

    /// <summary>End of stream inside a header or body.</summary>
    Truncated = 3,

    /// <summary>Invalid UTF-8 or JSON, not an object, missing type, or a field of the wrong type.</summary>
    Malformed = 4,

    /// <summary>A <c>type</c> this protocol version does not define.</summary>
    UnknownType = 5,
}

/// <summary>A frame or message broke the clasp-engine protocol. The connection must be closed.</summary>
public sealed class EngineProtocolException : Exception
{
    public EngineProtocolException(EngineFrameError error, string message)
        : base(message) => Error = error;

    public EngineProtocolException()
        : this(EngineFrameError.Malformed, "Protocol violation")
    {
    }

    public EngineProtocolException(string message)
        : this(EngineFrameError.Malformed, message)
    {
    }

    public EngineProtocolException(string message, Exception innerException)
        : base(message, innerException) => Error = EngineFrameError.Malformed;

    public EngineFrameError Error { get; }
}

/// <summary>
/// Length-prefixed frames: a 4-byte little-endian length N (1..65536), then N bytes of UTF-8
/// JSON. The length is checked before anything is allocated; partial reads and writes are
/// looped; a stream that ends inside a frame is a protocol error, never a partial message.
/// </summary>
public static class EngineFraming
{
    public const int MaxFrameLength = 65536;

    /// <summary>
    /// Read one frame body. Returns null when the stream ends cleanly between frames.
    /// </summary>
    /// <exception cref="EngineProtocolException">Zero, oversized or truncated frame.</exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        int got = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < header.Length)
            throw new EngineProtocolException(EngineFrameError.Truncated, "The stream ended inside a frame header.");

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
            throw new EngineProtocolException(EngineFrameError.ZeroLength, "Zero-length frame.");
        if (length > MaxFrameLength)
            throw new EngineProtocolException(EngineFrameError.Oversized, $"Frame of {length} bytes exceeds the {MaxFrameLength}-byte limit.");

        var body = new byte[length];
        got = await ReadFullyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        if (got < body.Length)
            throw new EngineProtocolException(EngineFrameError.Truncated, "The stream ended inside a frame body.");
        return body;
    }

    /// <summary>Write one frame (header and body in a single buffer).</summary>
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (body.Length == 0 || body.Length > MaxFrameLength)
            throw new EngineProtocolException(body.Length == 0 ? EngineFrameError.ZeroLength : EngineFrameError.Oversized,
                $"Refusing to send a frame of {body.Length} bytes.");
        var buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)body.Length);
        body.CopyTo(buffer.AsMemory(4));
        // Stream.WriteAsync transfers the whole buffer (it loops over partial pipe writes).
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encode a frame into a byte array (tests and fixtures).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> body)
    {
        var buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)body.Length);
        body.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
