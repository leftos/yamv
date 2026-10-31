using Yamv.Core.Platform;

namespace Yamv.Core.Tests.Platform;

public sealed class AlertCommandTests
{
    [Fact]
    public void For_TheNotRespondingText_ShowsItInACautionDialogTitledYAMV()
    {
        var command = AlertCommand.For("YAMV is not responding; the file was not opened.");

        Assert.Equal("/usr/bin/osascript", command.Program);
        Assert.Equal(
            [
                "-e",
                "display dialog \"YAMV is not responding; the file was not opened.\" with title \"YAMV\" buttons {\"OK\"} default button \"OK\" with icon caution",
            ],
            command.ArgumentList
        );
    }

    [Fact]
    public void For_TextWithQuotesAndBackslashes_EscapesBothForAppleScript()
    {
        var command = AlertCommand.For("say \"hi\" C:\\temp");

        Assert.Equal(2, command.ArgumentList.Count);
        Assert.Equal("-e", command.ArgumentList[0]);
        Assert.Equal(
            "display dialog \"say \\\"hi\\\" C:\\\\temp\" with title \"YAMV\" buttons {\"OK\"} default button \"OK\" with icon caution",
            command.ArgumentList[1]
        );
    }

    [Fact]
    public void For_NullText_Throws() => Assert.Throws<ArgumentNullException>(() => AlertCommand.For(null!));
}
