using System.Text;
using Yamv.Core.Instance;

namespace Yamv.Core.Tests.Instance;

public sealed class OpenMessageTests
{
    /// <summary>A fully qualified root on this platform whose text JSON writes as it is, one byte per character.</summary>
    private static readonly string Root = OperatingSystem.IsWindows() ? "C:/" : "/";

    /// <summary>The bytes <c>open ["</c> and <c>"]</c> add around one plain ASCII path.</summary>
    private static readonly int EnvelopeBytes = Encoding.UTF8.GetByteCount("open [\"\"]");

    [Fact]
    public void TryEncode_BackslashAndNonAsciiPaths_RoundTrip()
    {
        string[] paths =
        [
            Path.Combine(Path.GetTempPath(), "dir\\with\\back slash.md"),
            Path.Combine(Path.GetTempPath(), "Ελληνικά", "日本語 ✓ notes.md"),
        ];

        Assert.True(OpenMessage.TryEncode(paths, out string message, out _));
        Assert.True(OpenMessage.TryDecode(message, out IReadOnlyList<string> decoded));

        Assert.Equal(paths, decoded);
    }

    [Fact]
    public void TryEncode_MessageExactlyAtTheFrameCap_ReturnsTrue()
    {
        string path = PlainPathOfBytes(InstanceFrame.MaxPayloadBytes - EnvelopeBytes);

        Assert.True(OpenMessage.TryEncode([path], out string message, out _));

        Assert.Equal(InstanceFrame.MaxPayloadBytes, Encoding.UTF8.GetByteCount(message));
        Assert.True(OpenMessage.TryDecode(message, out IReadOnlyList<string> decoded));
        Assert.Equal(path, Assert.Single(decoded));
    }

    [Fact]
    public void TryEncode_OneByteOverTheFrameCap_ReturnsFalse()
    {
        string path = PlainPathOfBytes(InstanceFrame.MaxPayloadBytes - EnvelopeBytes + 1);

        Assert.False(OpenMessage.TryEncode([path], out _, out _));
    }

    [Fact]
    public void TryEncode_EscapedPathNearTheFrameCap_IsMeasuredOnTheEscapedJson()
    {
        // Each "é\" is 3 UTF-8 bytes as text but 8 once JSON escapes it as é\\, so only the escaped size meets the cap.
        const int Units = 8000;
        int fill = InstanceFrame.MaxPayloadBytes - EnvelopeBytes - Root.Length - (8 * Units);
        string atCap = Root + string.Concat(Enumerable.Repeat("é\\", Units)) + new string('a', fill);

        Assert.True(OpenMessage.TryEncode([atCap], out string message, out _));
        Assert.Equal(InstanceFrame.MaxPayloadBytes, Encoding.UTF8.GetByteCount(message));
        Assert.False(OpenMessage.TryEncode([atCap + "a"], out _, out OpenMessageProblem problem));
        Assert.Equal(OpenMessageProblem.TooLarge, problem);
    }

    [Fact]
    public void TryEncode_UnpairedSurrogate_RoundTripsOrReturnsFalseWithoutThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), "lone\uD800surrogate.md");

        bool encoded = OpenMessage.TryEncode([path], out string message, out _);

        if (encoded)
        {
            Assert.True(OpenMessage.TryDecode(message, out IReadOnlyList<string> decoded));
            Assert.Equal(path, Assert.Single(decoded));
        }
    }

    [Theory]
    [InlineData("open [\"unterminated")]
    [InlineData("open {\"a\":1}")]
    [InlineData("open [1, 2]")]
    [InlineData("open [null]")]
    [InlineData("open")]
    [InlineData("activate")]
    [InlineData("")]
    public void TryDecode_MalformedMessage_ReturnsFalse(string message) => Assert.False(OpenMessage.TryDecode(message, out _));

    [Fact]
    public void TryDecode_RelativePath_ReturnsFalse()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "fine.md");
        string message = $"open [{System.Text.Json.JsonSerializer.Serialize(absolute)}, \"notes/readme.md\"]";

        Assert.False(OpenMessage.TryDecode(message, out _));
    }

    [Fact]
    public void TryDecode_EmptyList_ReturnsFalse() => Assert.False(OpenMessage.TryDecode("open []", out _));

    [Theory]
    [InlineData("opened", InstanceReply.Opened)]
    [InlineData("refused", InstanceReply.Refused)]
    [InlineData("closing", InstanceReply.Closing)]
    [InlineData("OPENED", InstanceReply.Refused)]
    [InlineData("done", InstanceReply.Refused)]
    [InlineData("", InstanceReply.Refused)]
    public void InstanceReplyParse_UnknownTextIsRefused(string text, InstanceReply expected) => Assert.Equal(expected, InstanceReplies.Parse(text));

    /// <summary>A fully qualified path of plain ASCII letters that is exactly <paramref name="bytes"/> UTF-8 bytes long.</summary>
    private static string PlainPathOfBytes(int bytes) => Root + new string('a', bytes - Root.Length);
}
