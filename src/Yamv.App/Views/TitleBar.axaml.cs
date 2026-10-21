using Avalonia;
using Avalonia.Controls;

namespace Yamv.App.Views;

/// <summary>
/// The 38 px custom title bar: the document name and its location, and the <c>TitleBarTools</c> slot for title-bar buttons,
/// left to right: the ⟷ content-width button (<see cref="ContentWidthButton"/>, which the window opens its popover from and
/// anchors it to), then any tool added after it. The window's caption buttons are the ones Avalonia's drawn decorations put
/// over the bar's right end, so the slot keeps clear of them on Windows. On macOS the traffic lights stay on the left, so the
/// text starts 78 px in.
/// </summary>
public partial class TitleBar : UserControl
{
    /// <summary>How far in a custom title bar's text starts on macOS, clear of the traffic lights.</summary>
    internal const double MacTrafficLightInset = 78;

    /// <summary>
    /// The width the drawn minimize, maximize and close buttons take from the bar's right end: three 45 px buttons 2 px apart and
    /// the decorations' frame inset, measured on a Windows capture as 141.5 px.
    /// </summary>
    private const double CaptionButtonsWidth = 142;

    public TitleBar()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            TitleText.Margin = new Thickness(MacTrafficLightInset, 0, 0, 0);
        }
        else
        {
            TitleBarTools.Margin = new Thickness(0, 0, CaptionButtonsWidth, 0);
        }
    }
}
