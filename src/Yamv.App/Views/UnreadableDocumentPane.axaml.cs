using Avalonia.Controls;

namespace Yamv.App.Views;

/// <summary>
/// What a window shows in place of the page when its selected document exists but cannot be read: why, the document's full
/// path, <b>Retry</b> and <b>Remove from list</b>, and the note that YAMV tries again when the file changes.
/// </summary>
public partial class UnreadableDocumentPane : UserControl
{
    public UnreadableDocumentPane() => InitializeComponent();
}
