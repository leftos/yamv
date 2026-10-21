using Avalonia.Controls;

namespace Yamv.App.Views;

/// <summary>
/// What a window shows in place of the page when its selected document is no longer on disk: the document's full path,
/// <b>Locate…</b>, <b>Remove from list</b> and the note that YAMV keeps watching for it.
/// </summary>
public partial class MissingDocumentPane : UserControl
{
    public MissingDocumentPane() => InitializeComponent();
}
