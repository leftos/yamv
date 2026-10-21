using Yamv.Core;

namespace Yamv.Core.Tests;

/// <summary>Tests that change <c>YAMV_DATA_DIR</c>, which is process-wide, so they never run beside each other.</summary>
[CollectionDefinition(nameof(DataDirectoryVariable), DisableParallelization = true)]
public sealed class DataDirectoryVariable;

[Collection(nameof(DataDirectoryVariable))]
public sealed class AppPathsTests
{
    private const string Variable = AppPaths.DataDirectoryVariable;

    [Fact]
    public void Data_directory_variable_moves_every_path_under_it()
    {
        string folder = Path.Combine(Path.GetTempPath(), "yamv-apppaths-" + Guid.NewGuid().ToString("N"));
        WithVariable(folder, () => AssertUnder(folder));
    }

    private static void AssertUnder(string folder)
    {
        Assert.Equal(folder, AppPaths.DataDirectory);
        Assert.Equal(Path.Combine(folder, "session.json"), AppPaths.SessionFile);
        Assert.Equal(Path.Combine(folder, "logs", "yamv.log"), AppPaths.LogFile);
        Assert.Equal(Path.Combine(folder, "themes"), AppPaths.ThemesDirectory);
    }

    [Fact]
    public void Relative_data_directory_variable_is_made_full() =>
        WithVariable("relative-data", () => Assert.Equal(Path.GetFullPath("relative-data"), AppPaths.DataDirectory));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unset_or_empty_variable_gives_the_platform_folder(string? value) =>
        WithVariable(value, () => Assert.Equal(PlatformFolder(), AppPaths.DataDirectory));

    private static string PlatformFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YAMV");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "YAMV");
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "yamv");
    }

    private static void WithVariable(string? value, Action body)
    {
        string? before = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, before);
        }
    }
}
