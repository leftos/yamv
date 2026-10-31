using Yamv.Core.Platform;

namespace Yamv.Core.Tests.Platform;

public sealed class ShellExtensionTests
{
    [Theory]
    [InlineData("plain", true)]
    [InlineData("a b", true)]
    [InlineData("a.b c", true)]
    [InlineData("release.1 notes", true)]
    [InlineData("a. b", true)]
    [InlineData("a b.c d", true)]
    [InlineData("ab.c d", true)]
    [InlineData("a.vbs x", true)]
    [InlineData("a.zzz", false)]
    [InlineData("a.ext", false)]
    [InlineData("a.123", false)]
    [InlineData("a.verylongext", false)]
    [InlineData("a.vbs", false)]
    [InlineData("a.py", false)]
    [InlineData("a b.c d.zzz", false)]
    [InlineData("a b.cd", false)]
    [InlineData("x.y.qqq", false)]
    [InlineData("a.b c.zzz", false)]
    [InlineData("release.1", false)]
    [InlineData("a .zzz", false)]
    [InlineData(".zzz", false)]
    [InlineData("a.b\u00A0c", false)]
    public void IsMissing_ReadsTheFinalNameAsTheShellDoes(string name, bool missing)
    {
        Assert.Equal(missing, ShellExtension.IsMissing(name));
        Assert.Equal(missing, ShellExtension.IsMissing(@"C:\x y\" + name));
        Assert.Equal(missing, ShellExtension.IsMissing("/tmp/x.y z/" + name));
    }
}
