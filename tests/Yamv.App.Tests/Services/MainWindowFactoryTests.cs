using Avalonia;
using Avalonia.Controls;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core;
using Yamv.Core.Session;
using Yamv.Core.Theming;
using Yamv.Core.Widths;

namespace Yamv.App.Tests.Services;

/// <summary>Builds viewer and main windows over a temp data folder, so each test has its own session file and themes.</summary>
[Collection(nameof(EnvironmentVariables))]
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

    [Fact]
    public Task Viewer_with_one_missing_file_selects_it_and_hides_the_tree() =>
        Headless.RunAsync(async () =>
        {
            await using var viewer = new ViewerFixture();

            viewer.Build(viewer.PathOf("gone.md"));

            Assert.False(viewer.ViewModel.IsEmpty);
            Assert.False(viewer.ViewModel.ShowTree);
            Assert.True(viewer.ViewModel.SelectedIsMissing);
            Assert.Equal(viewer.PathOf("gone.md"), viewer.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Viewer_with_an_existing_and_a_missing_file_shows_both_and_selects_the_existing() =>
        Headless.RunAsync(async () =>
        {
            await using var viewer = new ViewerFixture();
            string existing = viewer.Write("plan.md");
            string missing = viewer.PathOf("gone.md");

            viewer.Build(missing, existing);

            Assert.True(viewer.ViewModel.ShowTree);
            Assert.Equal(existing, viewer.ViewModel.SelectedPath);
            Assert.False(viewer.ViewModel.SelectedIsMissing);
            Assert.IsType<DocumentItemViewModel>(viewer.ViewModel.SelectedItem);
            List<DocumentItemViewModel> rows = viewer.DocumentRows();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.Path == existing && !row.IsMissing);
            Assert.Contains(rows, row => row.Path == missing && row.IsMissing);
        });

    [Fact]
    public Task Viewer_leaves_the_bridges_theme_alone_and_creates_no_session_file() =>
        Headless.RunAsync(async () =>
        {
            await using var viewer = new ViewerFixture();
            viewer.Bridge.Apply("ink", "dark");

            viewer.Build(viewer.PathOf("gone.md"));

            Assert.Equal("ink", viewer.Bridge.RequestedThemeId);
            Assert.Equal("dark", viewer.Bridge.RequestedAppearance);
            Assert.False(File.Exists(viewer.SessionFile));
        });

    [Fact]
    public Task Main_window_leaves_the_bridges_theme_alone_whatever_the_session_holds() =>
        Headless.RunAsync(async () =>
        {
            await using var viewer = new ViewerFixture();
            viewer.WriteSession("""{ "themeId": "paper", "appearance": "light" }""");
            viewer.Bridge.Apply("ink", "dark");

            viewer.BuildMain();

            Assert.Equal("ink", viewer.Bridge.RequestedThemeId);
            Assert.Equal("dark", viewer.Bridge.RequestedAppearance);
        });

    [Fact]
    public Task Viewer_leaves_a_corrupt_session_file_in_place() =>
        Headless.RunAsync(async () =>
        {
            await using var viewer = new ViewerFixture();
            viewer.WriteSession("{ not json");

            viewer.Build(viewer.PathOf("gone.md"));

            Assert.Equal("{ not json", File.ReadAllText(viewer.SessionFile));
            Assert.False(File.Exists(viewer.SessionFile + ".bad"));
        });

    [Fact]
    public Task Viewer_shows_its_document_at_its_stored_width() =>
        Headless.RunAsync(async () =>
        {
            await using var viewer = new ViewerFixture();
            string plan = viewer.Write("plan.md");
            Assert.True(viewer.Widths.Set(viewer.Documents, ContentWidth.FromPx(720)));

            viewer.Build(plan);
            await viewer.ViewModel.WhenRendersSettled();

            ShownDocument shown = Assert.Single(viewer.Surface.Shown);
            Assert.Equal(plan, shown.DocumentPath);
            Assert.Equal(720, shown.ContentWidth);
        });

    /// <summary>
    /// Builds a viewer window or a main window over a temp data folder whose session file a test may seed. A viewer draws its
    /// pages on a <see cref="FakeDocumentSurface"/> that completes its renders, so the fixture can wait for every render
    /// before it deletes the documents a render reads.
    /// </summary>
    private sealed class ViewerFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-viewer-" + Guid.NewGuid().ToString("N"));
        private readonly string? _dataBefore = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);

        public ViewerFixture()
        {
            Documents = Path.Combine(_root, "docs");
            DataFolder = Path.Combine(_root, "data");
            Directory.CreateDirectory(Documents);
            Directory.CreateDirectory(DataFolder);
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, DataFolder);
            Bridge = new ThemeBridge(new ThemeCatalog(AppPaths.ThemesDirectory, Log), Application.Current!, Log);
            Widths = new WidthStore(Path.Combine(DataFolder, "widths.json"), Log);
        }

        public string Documents { get; }

        public string DataFolder { get; }

        public string SessionFile => Path.Combine(DataFolder, "session.json");

        public RecordingAppLog Log { get; } = new();

        public FakeDocumentSurface Surface { get; } = new();

        public ThemeBridge Bridge { get; }

        /// <summary>The app's one width store, over <c>widths.json</c> in the temp data folder.</summary>
        public WidthStore Widths { get; }

        /// <summary>The window the test built; null until <see cref="Build"/>.</summary>
        public MainWindowComposition Composition { get; private set; } = null!;

        public DocumentWindowViewModel ViewModel => Composition.ViewModel;

        /// <summary>Creates a viewer window over <paramref name="files"/>, drawn on <see cref="Surface"/>.</summary>
        /// <param name="files">The full paths the viewer shows.</param>
        public void Build(params string[] files) => Composition = MainWindowFactory.CreateViewer(Bridge, Widths, files, Log, new Border(), Surface);

        /// <summary>Creates a main window over the temp data folder's session.</summary>
        public void BuildMain() => Composition = MainWindowFactory.Create(Bridge, Widths, Log);

        /// <summary>The full path of a file in the documents folder; it is not created.</summary>
        /// <param name="name">The file's name.</param>
        public string PathOf(string name) => Path.Combine(Documents, name);

        /// <summary>Writes a document into the documents folder and returns its full path.</summary>
        /// <param name="name">The file's name.</param>
        public string Write(string name)
        {
            string path = PathOf(name);
            File.WriteAllText(path, "# " + name);
            return path;
        }

        /// <summary>Writes the session file the viewer reads.</summary>
        /// <param name="json">The session JSON.</param>
        public void WriteSession(string json) => File.WriteAllText(SessionFile, json);

        /// <summary>The viewer's document rows, wherever the grouper placed them in the tree.</summary>
        public List<DocumentItemViewModel> DocumentRows() => Rows(ViewModel.Items);

        public async ValueTask DisposeAsync()
        {
            Composition?.Dispose();
            Bridge.Dispose();
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, _dataBefore);
            if (Composition is not null)
            {
                await Composition.ViewModel.WhenRendersSettled();
            }

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static List<DocumentItemViewModel> Rows(IReadOnlyList<TreeItemViewModel> items)
        {
            List<DocumentItemViewModel> rows = [];
            foreach (TreeItemViewModel item in items)
            {
                switch (item)
                {
                    case DocumentItemViewModel document:
                        rows.Add(document);
                        break;
                    case GroupItemViewModel group:
                        rows.AddRange(Rows(group.Children));
                        break;
                    case FolderItemViewModel folder:
                        rows.AddRange(Rows(folder.Children));
                        break;
                }
            }

            return rows;
        }
    }
}
