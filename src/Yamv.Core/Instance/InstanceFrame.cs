using System.Buffers.Binary;
using System.Text;

namespace Yamv.Core.Instance;

/// <summary>
/// The wire format of one instance message: an int32 little-endian byte count, then that many UTF-8 bytes.
/// </summary>
internal static class InstanceFrame
{
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>The largest reply a client reads: a reply is one short word, so a longer frame is not a reply.</summary>
    public const int MaxReplyPayloadBytes = 256;

    private const int HeaderBytes = sizeof(int);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Builds the frame for <paramref name="message"/>.</summary>
    /// <exception cref="ArgumentException">The message encodes to more than <see cref="MaxPayloadBytes"/> bytes.</exception>
    public static byte[] Encode(string message)
    {
        int payloadBytes = StrictUtf8.GetByteCount(message);
        if (payloadBytes > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"An instance message is at most {MaxPayloadBytes} UTF-8 bytes; this one is {payloadBytes}.",
                nameof(message)
            );
        }

        byte[] frame = new byte[HeaderBytes + payloadBytes];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payloadBytes);
        StrictUtf8.GetBytes(message, frame.AsSpan(HeaderBytes));
        return frame;
    }

    /// <summary>Reads one request frame of at most <see cref="MaxPayloadBytes"/> from <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">The length is negative or over <see cref="MaxPayloadBytes"/>.</exception>
    /// <exception cref="EndOfStreamException">The stream ended before the frame did.</exception>
    /// <exception cref="DecoderFallbackException">The payload is not valid UTF-8.</exception>
    public static Task<string> ReadAsync(Stream stream, CancellationToken cancellationToken) => ReadAsync(stream, MaxPayloadBytes, cancellationToken);

    /// <summary>Reads one frame of at most <paramref name="maxPayloadBytes"/> from <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">The length is negative or over <paramref name="maxPayloadBytes"/>.</exception>
    /// <exception cref="EndOfStreamException">The stream ended before the frame did.</exception>
    /// <exception cref="DecoderFallbackException">The payload is not valid UTF-8.</exception>
    public static async Task<string> ReadAsync(Stream stream, int maxPayloadBytes, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxPayloadBytes)
        {
            throw new InvalidDataException($"An instance message frame declared {length} bytes; the limit is 0 to {maxPayloadBytes}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return StrictUtf8.GetString(payload);
    }
}
