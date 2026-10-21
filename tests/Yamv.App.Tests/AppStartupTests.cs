using Avalonia;
using Avalonia.Themes.Fluent;

namespace Yamv.App.Tests;

public sealed class AppStartupTests
{
    [Fact]
    public Task App_starts_headless_with_fluent_theme() =>
        Headless.Run(() =>
        {
            Application? application = Application.Current;

            Assert.NotNull(application);
            Assert.IsType<App>(application);
            Assert.Contains(application.Styles, style => style is FluentTheme);
        });
}
