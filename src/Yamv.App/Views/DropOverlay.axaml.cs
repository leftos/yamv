using Avalonia.Controls;
using Yamv.App.ViewModels;
using Yamv.Core.Documents;

namespace Yamv.App.Views;

/// <summary>
/// What a file drag over a window shows, below the title bar: a tint with a dashed outline and "Drop to open", or a grey refusal
/// naming the document types. While the files are not known yet it is transparent, yet still takes the drag.
/// </summary>
public partial class DropOverlay : UserControl
{
    /// <summary>What a drag holding a document is told.</summary>
    internal const string OpenText = "Drop to open";

    public DropOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Draws <paramref name="state"/>.</summary>
    /// <param name="state">What the overlay shows.</param>
    internal void Show(DropOverlayState state)
    {
        bool refused = state == DropOverlayState.Refused;
        Form.IsVisible = refused || state == DropOverlayState.Open;
        Form.Classes.Set("no", refused);
        OpenIcon.IsVisible = !refused;
        RefusedIcon.IsVisible = refused;
        Say.Text = refused ? DocumentFileTypes.DropHint : OpenText;
    }
}
