using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Yamv.Core.Instance;

/// <summary>
/// The codec for the <see cref="InstanceMessages.Open"/> message: <c>open</c>, a space, then a JSON array of full paths. JSON
/// keeps every character a path can hold (a macOS file name may hold a newline); the default encoder writes non-ASCII and some
/// ASCII characters as six-byte <c>\uXXXX</c> escapes, so the size is measured on the encoded text, never estimated.
/// </summary>
public static class OpenMessage
{
    private const string Prefix = InstanceMessages.Open + " ";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Builds the message for <paramref name="fullPaths"/>. False, with <paramref name="message"/> empty and
    /// <paramref name="problem"/> saying why, when the list is empty, a path is not fully qualified, a path is not well-formed
    /// text, or the escaped message is over the 64 KiB frame cap: a launcher refuses such a request before connecting, and never
    /// splits it. <paramref name="problem"/> is <see cref="OpenMessageProblem.None"/> on success.
    /// </summary>
    public static bool TryEncode(IReadOnlyList<string> fullPaths, out string message, out OpenMessageProblem problem)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        message = string.Empty;
        problem = Check(fullPaths);
        if (problem != OpenMessageProblem.None)
        {
            return false;
        }

        string? json = TryWriteJson(fullPaths);
        if (json is null)
        {
            problem = OpenMessageProblem.NotWellFormed;
            return false;
        }

        string candidate = Prefix + json;
        if (!FitsInFrame(candidate))
        {
            problem = OpenMessageProblem.TooLarge;
            return false;
        }

        message = candidate;
        return true;
    }

    /// <summary>
    /// Reads the paths from an <c>open</c> message. Never throws: false, with <paramref name="paths"/> empty, when the message
    /// does not start with <c>open </c>, its JSON is not an array of strings, the array is empty, or a path is not fully qualified.
    /// </summary>
    public static bool TryDecode(string message, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (message is null || !message.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        List<string>? decoded = TryReadJson(message[Prefix.Length..]);
        if (decoded is null || Check(decoded) != OpenMessageProblem.None)
        {
            return false;
        }

        paths = decoded;
        return true;
    }

    /// <summary>
    /// What is wrong with the list, if anything: it must hold at least one path and each must be fully qualified, well-formed
    /// UTF-16. A lone surrogate is refused here: the JSON writer would silently replace it with U+FFFD and name another file.
    /// </summary>
    private static OpenMessageProblem Check(IReadOnlyList<string?> paths)
    {
        if (paths.Count == 0)
        {
            return OpenMessageProblem.Empty;
        }

        foreach (string? path in paths)
        {
            if (path is null || !Path.IsPathFullyQualified(path))
            {
                return OpenMessageProblem.NotFullPath;
            }

            if (!IsWellFormedUtf16(path))
            {
                return OpenMessageProblem.NotWellFormed;
            }
        }

        return OpenMessageProblem.None;
    }

    private static bool IsWellFormedUtf16(string text)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The JSON array of <paramref name="paths"/>, or null when the writer cannot encode one of them.</summary>
    private static string? TryWriteJson(IReadOnlyList<string> paths)
    {
        ArrayBufferWriter<byte> buffer = new();
        try
        {
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartArray();
                foreach (string path in paths)
                {
                    writer.WriteStringValue(path);
                }

                writer.WriteEndArray();
            }

            return StrictUtf8.GetString(buffer.WrittenSpan);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The strings of a JSON array that holds only strings, or null when <paramref name="json"/> is anything else.</summary>
    private static List<string>? TryReadJson(string json)
    {
        List<string> paths = [];
        try
        {
            Utf8JsonReader reader = new(StrictUtf8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.String)
            {
                paths.Add(reader.GetString()!);
            }

            bool closed = reader.TokenType == JsonTokenType.EndArray;
            return closed && !reader.Read() ? paths : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="message"/>, made of well-formed paths only, is at most the frame cap in UTF-8 bytes.</summary>
    private static bool FitsInFrame(string message) => StrictUtf8.GetByteCount(message) <= InstanceFrame.MaxPayloadBytes;
}

/// <summary>Why <see cref="OpenMessage.TryEncode"/> could not build a message.</summary>
public enum OpenMessageProblem
{
    /// <summary>Nothing: the message was built.</summary>
    None,

    /// <summary>The list holds no path.</summary>
    Empty,

    /// <summary>A path is not fully qualified.</summary>
    NotFullPath,

    /// <summary>A path is not well-formed text (it holds a lone surrogate).</summary>
    NotWellFormed,

    /// <summary>The escaped message is over the 64 KiB frame cap.</summary>
    TooLarge,
}
