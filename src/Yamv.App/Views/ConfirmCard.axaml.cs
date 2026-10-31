using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Yamv.App.Views;

/// <summary>
/// The card that asks before a link opens a file YAMV does not show, centred on a scrim over the document pane. Cancel is the
/// default: only Open opens, by a click or by Enter with focus moved to it, and Enter anywhere else and Esc cancel.
/// </summary>
public partial class ConfirmCard : UserControl
{
    // The scrim and the shadow are not theme tokens: each is one value per theme, from the approved mock.
    private static readonly Color LightScrim = Color.Parse("#521F2328");
    private static readonly Color DarkScrim = Color.Parse("#85000000");
    private static readonly Color LightShadow = Color.Parse("#2E1F2328");
    private static readonly Color DarkShadow = Color.Parse("#99000000");

    /// <summary>The double click time of a window with no platform settings.</summary>
    private static readonly TimeSpan FallbackDoubleClickTime = TimeSpan.FromMilliseconds(500);

    private ITimer? _openGuard;
    private int _show;

    public ConfirmCard()
    {
        InitializeComponent();
        OpenButton.Click += OnOpenClick;
        CancelButton.Click += OnCancelClick;
        OpenButton.PropertyChanged += OnOpenButtonPropertyChanged;
        AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnCardKeyUp, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        UpdateTheme();
    }

    /// <summary>Raised with true for Open and false for Cancel, Esc or Enter away from Open.</summary>
    public event EventHandler<bool>? Answered;

    /// <summary>
    /// Starts a show of the card: keyboard focus goes to Cancel, and Open takes no click for a double click's time, so the second
    /// click of the double click that raised the card cannot land on it.
    /// </summary>
    /// <param name="time">The clock the wait runs on.</param>
    public void StartShow(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        EndShow();
        CancelButton.Focus(NavigationMethod.Directional);
        OpenButton.IsHitTestVisible = false;
        TimeSpan wait = Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? FallbackDoubleClickTime;
        int show = _show;
        _openGuard = time.CreateTimer(_ => Dispatcher.UIThread.Post(() => OnOpenGuardEnded(show)), null, wait, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Ends a show of the card, stopping the wait on Open.</summary>
    public void EndShow()
    {
        _show++;
        _openGuard?.Dispose();
        _openGuard = null;
    }

    /// <summary>A wait left over from a show that has ended restores nothing.</summary>
    private void OnOpenGuardEnded(int show)
    {
        if (show == _show)
        {
            OpenButton.IsHitTestVisible = true;
        }
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Answered?.Invoke(this, true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Answered?.Invoke(this, false);

    /// <summary>Keys reach the card first, so neither button acts on one on its own.</summary>
    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter))
        {
            return;
        }

        Answered?.Invoke(this, e.Key == Key.Enter && OpenButton.IsFocused);
        e.Handled = true;
    }

    /// <summary>A button clicks on Space's release, so a Space pressed before the card showed is kept from reaching one.</summary>
    private void OnCardKeyUp(object? sender, KeyEventArgs e)
    {
        Button? focused =
            OpenButton.IsFocused ? OpenButton
            : CancelButton.IsFocused ? CancelButton
            : null;
        if (e.Key == Key.Space && focused is not { IsPressed: true })
        {
            e.Handled = true;
        }
    }

    /// <summary>The accent ring shows what Enter answers: it moves to Open while Open has focus.</summary>
    private void OnOpenButtonPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsFocusedProperty)
        {
            OpenButton.Classes.Set("def", OpenButton.IsFocused);
            CancelButton.Classes.Set("def", !OpenButton.IsFocused);
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => UpdateTheme();

    /// <summary>Gives the scrim its colour for the theme and the card its far (0 18 44) and near (0 2 6) shadows.</summary>
    private void UpdateTheme()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        Color shadow = dark ? DarkShadow : LightShadow;
        Scrim.Background = new SolidColorBrush(dark ? DarkScrim : LightScrim);
        Card.BoxShadow = new BoxShadows(
            new BoxShadow
            {
                OffsetY = 18,
                Blur = 44,
                Color = shadow,
            },
            [
                new BoxShadow
                {
                    OffsetY = 2,
                    Blur = 6,
                    Color = shadow,
                },
            ]
        );
    }
}
