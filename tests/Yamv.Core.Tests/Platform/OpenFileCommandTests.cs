using Yamv.Core.Platform;

namespace Yamv.Core.Tests.Platform;

public sealed class OpenFileCommandTests
{
    [Fact]
    public void For_FileOnWindows_HandsTheFileItselfToTheShell()
    {
        var command = OpenFileCommand.For(@"C:\themes\a b.yamv-theme.json", isMac: false);

        Assert.Equal(@"C:\themes\a b.yamv-theme.json", command.Program);
        Assert.True(command.UseShellExecute);
        Assert.Empty(command.ArgumentList);
    }

    [Fact]
    public void For_FileOnMac_OpensItWithOpen()
    {
        var command = OpenFileCommand.For("/Users/me/themes/a b.yamv-theme.json", isMac: true);

        Assert.Equal("/usr/bin/open", command.Program);
        Assert.False(command.UseShellExecute);
        Assert.Equal(["/Users/me/themes/a b.yamv-theme.json"], command.ArgumentList);
    }

    [Theory]
    [InlineData("a b.yamv-theme.json")]
    [InlineData("a\"b.yamv-theme.json")]
    [InlineData("a'b.yamv-theme.json")]
    [InlineData("thème-ü.yamv-theme.json")]
    public void For_APathWithSpacesQuotesOrNonAscii_KeepsItOneWholeArgument(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "yamv open", name);

        var mac = OpenFileCommand.For(path, isMac: true);
        var windows = OpenFileCommand.For(path, isMac: false);

        Assert.Equal([path], mac.ArgumentList);
        Assert.Equal(path, windows.Program);
        Assert.Empty(windows.ArgumentList);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void For_ANullPath_Throws(bool isMac) => Assert.Throws<ArgumentNullException>(() => OpenFileCommand.For(null!, isMac));
}
