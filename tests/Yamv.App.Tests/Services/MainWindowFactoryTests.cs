using Yamv.App.Services;
using Yamv.App.Views;
using Yamv.Core.Session;

namespace Yamv.App.Tests.Services;

public sealed class MainWindowFactoryTests
{
    [Fact]
    public Task RestoreLayout_sets_the_sessions_sidebar_width() =>
        Headless.Run(() =>
        {
            var window = new MainWindow();

            MainWindowFactory.RestoreLayout(window, new SessionState { SidebarWidth = 360 });

            Assert.Equal(360, window.SidebarWidth);
        });
}
