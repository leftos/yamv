using Yamv.Core.Launch;

namespace Yamv.Core.Tests.Launch;

public sealed class LaunchFailureTextTests
{
    [Fact]
    public void For_OneFile_NamesTheFileNotOpened() => Assert.Equal("YAMV is not responding; the file was not opened.", LaunchFailureText.For(1));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(12)]
    public void For_SeveralFiles_CountsThem(int fileCount) =>
        Assert.Equal($"YAMV is not responding; {fileCount} files were not opened.", LaunchFailureText.For(fileCount));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void For_NoFile_Throws(int fileCount) => Assert.Throws<ArgumentOutOfRangeException>(() => LaunchFailureText.For(fileCount));
}
