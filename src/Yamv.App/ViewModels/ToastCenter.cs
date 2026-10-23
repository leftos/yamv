using System.Collections.ObjectModel;
using Avalonia.Threading;
using Yamv.App.Services;

namespace Yamv.App.ViewModels;

/// <summary>
/// A document window's toasts, oldest first: at most three cards, each fading 5 s after it was shown, counting only time while
/// the window is active. A text shown again while its card is up adds no card: the card counts the repeat and its 5 s start
/// over. Once disposed, with its window, it shows nothing more. Every member is called on the UI thread.
/// </summary>
/// <param name="time">The clock the fades run on.</param>
public sealed class ToastCenter(TimeProvider time) : IToasts, IDisposable
{
    /// <summary>How long a card stays up while its window is active.</summary>
    public static readonly TimeSpan FadeTime = TimeSpan.FromSeconds(5);

    /// <summary>The most cards up at once; a new one beyond it takes the oldest's place.</summary>
    public const int MaxCards = 3;

    /// <summary>How often the fades are counted down, which also moves each card's timer line.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private readonly ObservableCollection<ToastItem> _items = [];
    private ITimer? _timer;
    private DateTimeOffset _countedTo;
    private bool _isActive = true;
    private bool _disposed;

    /// <summary>The cards up, oldest first.</summary>
    public ReadOnlyObservableCollection<ToastItem> Items => field ??= new(_items);

    /// <summary>
    /// Whether the window is active, so its cards' fades run; true until the window says otherwise. While false, the cards stay
    /// up and their fades hold where they are.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value == _isActive)
            {
                return;
            }

            CountDown();
            _isActive = value;
            UpdateTimer();
        }
    }

    public void Show(ToastKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // A failure can finish after its window closed; it was logged, and there is no window left to show it in.
        if (_disposed)
        {
            return;
        }

        CountDown();
        if (_items.FirstOrDefault(item => item.Text == text) is { } shown)
        {
            shown.Count++;
            shown.Elapsed = TimeSpan.Zero;
            shown.TimeLeft = 1;
        }
        else
        {
            if (_items.Count == MaxCards)
            {
                _items.RemoveAt(0);
            }

            _items.Add(new ToastItem(kind, text));
        }

        UpdateTimer();
    }

    /// <summary>Takes <paramref name="item"/>'s card down, as its × does.</summary>
    /// <param name="item">The card to take down; nothing happens when it is no longer up.</param>
    public void Dismiss(ToastItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Remove(item);
        UpdateTimer();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Runs the tick while the window is active and a card is up, and stops it otherwise.</summary>
    private void UpdateTimer()
    {
        bool run = _isActive && _items.Count > 0 && !_disposed;
        if (run && _timer is null)
        {
            _countedTo = time.GetUtcNow();
            _timer = time.CreateTimer(_ => OnTick(), null, Tick, Tick);
        }
        else if (!run && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }

    private void OnTick()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CountDownAndFade();
        }
        else
        {
            Dispatcher.UIThread.Post(CountDownAndFade);
        }
    }

    private void CountDownAndFade()
    {
        if (_timer is null)
        {
            return;
        }

        CountDown();
        foreach (ToastItem faded in _items.Where(item => item.Elapsed >= FadeTime).ToList())
        {
            _items.Remove(faded);
        }

        UpdateTimer();
    }

    /// <summary>Adds the active time since the last count to every card's fade; nothing while the window is inactive.</summary>
    private void CountDown()
    {
        if (_timer is null)
        {
            return;
        }

        DateTimeOffset now = time.GetUtcNow();
        TimeSpan step = now - _countedTo;
        _countedTo = now;
        foreach (ToastItem item in _items)
        {
            item.Elapsed += step;
            item.TimeLeft = Math.Max(0, 1 - (item.Elapsed / FadeTime));
        }
    }
}
