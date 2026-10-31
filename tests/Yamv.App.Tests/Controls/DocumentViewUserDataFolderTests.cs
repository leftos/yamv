using Yamv.App.Controls;

namespace Yamv.App.Tests.Controls;

/// <summary>Where the document view keeps WebView2's user data, so a normal and an elevated instance never share a browser.</summary>
public sealed class DocumentViewUserDataFolderTests
{
    private const string LocalAppData = "local-app-data";

    [Theory]
    [InlineData(false, "webview2")]
    [InlineData(true, "webview2-admin")]
    public void The_folder_is_webview2_or_for_an_elevated_instance_webview2_admin(bool elevated, string folder) =>
        Assert.Equal(Path.Combine(LocalAppData, "YAMV", folder), DocumentView.WindowsUserDataFolder(overridden: null, LocalAppData, elevated));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_set_WEBVIEW2_USER_DATA_FOLDER_wins(bool elevated)
    {
        string overridden = Path.Combine("hidden", "profile");

        Assert.Equal(overridden, DocumentView.WindowsUserDataFolder(overridden, LocalAppData, elevated));
    }

    [Fact]
    public void An_empty_WEBVIEW2_USER_DATA_FOLDER_is_not_set() =>
        Assert.Equal(Path.Combine(LocalAppData, "YAMV", "webview2"), DocumentView.WindowsUserDataFolder("", LocalAppData, elevated: false));
}
