using Yamv.Core.Links;

namespace Yamv.Core.Tests.Links;

public sealed class ConfirmTextTests
{
    [Fact]
    public void A_listed_type_is_named_in_words()
    {
        var text = ConfirmText.For("/repo/tools/setup.ps1", "setup.ps1", isFolder: false, isMac: false, elevated: false);

        Assert.Equal("Open setup.ps1?", text.Title);
        Assert.Equal("It's a PowerShell script (.ps1). YAMV doesn't show this type; it will open with the app Windows uses for it.", text.Body);
    }

    [Theory]
    [InlineData(
        true,
        "/repo/tools/setup.ps1",
        false,
        "It's a PowerShell script (.ps1). YAMV doesn't show this type; it will open with the app Windows uses for it. It will run as Administrator."
    )]
    [InlineData(
        false,
        "/repo/tools/setup.ps1",
        false,
        "It's a PowerShell script (.ps1). YAMV doesn't show this type; it will open with the app Windows uses for it."
    )]
    [InlineData(true, "/repo/assets/", true, "It's a folder. YAMV doesn't show folders; it will open in Explorer.")]
    [InlineData(true, "/repo/go.lnk", false, "It's a shortcut (.lnk). It can start any program or open any address. It will run as Administrator.")]
    public void An_elevated_instance_says_what_opens_will_run_as_Administrator(bool elevated, string path, bool isFolder, string body)
    {
        var text = ConfirmText.For(path, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), isFolder, isMac: false, elevated);

        Assert.Equal(body, text.Body);
    }

    [Fact]
    public void An_unlisted_type_names_its_extension()
    {
        var text = ConfirmText.For("/repo/data.xyz", "data.xyz", isFolder: false, isMac: false, elevated: false);

        Assert.Equal("Open data.xyz?", text.Title);
        Assert.Equal("It's a .xyz file. YAMV doesn't show this type; it will open with the app Windows uses for it.", text.Body);
    }

    [Theory]
    [InlineData("SETUP.PS1", "It's a PowerShell script (.ps1). YAMV doesn't show this type; it will open with the app Windows uses for it.")]
    [InlineData("DATA.XYZ", "It's a .xyz file. YAMV doesn't show this type; it will open with the app Windows uses for it.")]
    public void An_upper_case_extension_shows_lower_case(string name, string body)
    {
        var text = ConfirmText.For(name, name, isFolder: false, isMac: false, elevated: false);

        Assert.Equal($"Open {name}?", text.Title);
        Assert.Equal(body, text.Body);
    }

    [Fact]
    public void A_file_with_no_extension_says_so()
    {
        var text = ConfirmText.For("/repo/Makefile", "Makefile", isFolder: false, isMac: false, elevated: false);

        Assert.Equal("Open Makefile?", text.Title);
        Assert.Equal("It's a file with no type. YAMV doesn't show this type; it will open with the app Windows uses for it.", text.Body);
    }

    [Theory]
    [InlineData(false, "It's a folder. YAMV doesn't show folders; it will open in Explorer.")]
    [InlineData(true, "It's a folder. YAMV doesn't show folders; it will open in Finder.")]
    public void A_folder_says_where_it_opens(bool isMac, string body)
    {
        var text = ConfirmText.For("/repo/assets/", "assets", isFolder: true, isMac, elevated: false);

        Assert.Equal("Open assets?", text.Title);
        Assert.Equal(body, text.Body);
    }

    [Fact]
    public void On_macOS_an_app_bundle_is_named_as_an_app()
    {
        var text = ConfirmText.For("/repo/Tool.app/", "Tool.app", isFolder: true, isMac: true, elevated: false);

        Assert.Equal("Open Tool.app?", text.Title);
        Assert.Equal("It's an app (.app). YAMV doesn't show this type; it will open with the app macOS uses for it.", text.Body);
    }

    [Fact]
    public void On_macOS_a_folder_with_an_extension_is_named_as_that_type()
    {
        var text = ConfirmText.For("/repo/Thing.xyz", "Thing.xyz", isFolder: true, isMac: true, elevated: false);

        Assert.Equal("Open Thing.xyz?", text.Title);
        Assert.Equal("It's a .xyz file. YAMV doesn't show this type; it will open with the app macOS uses for it.", text.Body);
    }

    [Fact]
    public void On_Windows_a_folder_with_a_dot_stays_a_folder()
    {
        var text = ConfirmText.For("/repo/assets.old", "assets.old", isFolder: true, isMac: false, elevated: false);

        Assert.Equal("Open assets.old?", text.Title);
        Assert.Equal("It's a folder. YAMV doesn't show folders; it will open in Explorer.", text.Body);
    }

    [Fact]
    public void MacOS_names_itself()
    {
        var text = ConfirmText.For("/repo/run.command", "run.command", isFolder: false, isMac: true, elevated: false);

        Assert.Equal("It's a shell script (.command). YAMV doesn't show this type; it will open with the app macOS uses for it.", text.Body);
    }

    [Theory]
    [InlineData(false, "It's a file with no type. YAMV doesn't show this type; it will open with the app Windows uses for it.")]
    [InlineData(true, "It's a .1 notes file. YAMV doesn't show this type; it will open with the app macOS uses for it.")]
    public void A_name_whose_extension_holds_a_space_follows_the_shell_on_Windows(bool isMac, string body)
    {
        var text = ConfirmText.For(@"C:\yamv links\release.1 notes", "release.1 notes", isFolder: false, isMac);

        Assert.Equal("Open release.1 notes?", text.Title);
        Assert.Equal(body, text.Body);
    }

    [Theory]
    [InlineData("/repo/run.lnk", ".lnk")]
    [InlineData("/repo/site.URL", ".url")]
    [InlineData("/repo/site.webloc", ".webloc")]
    [InlineData("/repo/note.fileloc", ".fileloc")]
    public void A_shortcut_says_it_can_start_any_program(string path, string shown)
    {
        var text = ConfirmText.For(path, "shortcut", isFolder: false, isMac: false, elevated: false);

        Assert.Equal($"It's a shortcut ({shown}). It can start any program or open any address.", text.Body);
    }

    [Fact]
    public void A_screen_saver_reads_as_a_program()
    {
        var text = ConfirmText.For("/repo/saver.scr", "saver.scr", isFolder: false, isMac: false, elevated: false);

        Assert.Equal("It's a program (.scr). YAMV doesn't show this type; it will open with the app Windows uses for it.", text.Body);
    }

    [Fact]
    public void The_extension_comes_from_the_real_path()
    {
        var text = ConfirmText.For("/repo/report.exe", "report.txt", isFolder: false, isMac: false, elevated: false);

        Assert.Equal("Open report.txt?", text.Title);
        Assert.StartsWith("It's a program (.exe).", text.Body, StringComparison.Ordinal);
    }
}
