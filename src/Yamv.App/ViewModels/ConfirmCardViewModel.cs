namespace Yamv.App.ViewModels;

/// <summary>
/// The one question a window's confirm card asks at a time. A newer question cancels the one before it. Every member is called
/// on the UI thread.
/// </summary>
public sealed class ConfirmCardViewModel : ViewModelBase, IDisposable
{
    private TaskCompletionSource<bool>? _answer;
    private bool _disposed;

    /// <summary>The question; null while no card is up.</summary>
    public string? Title
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>What the file is and what will open it; null while no card is up.</summary>
    public string? Body
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Whether a card is up.</summary>
    public bool IsOpen
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Shows a card, cancelling any already up.</summary>
    /// <param name="title">The question.</param>
    /// <param name="body">What the file is and what will open it.</param>
    /// <returns>True when the user chose Open; false when the card was cancelled in any way.</returns>
    public Task<bool> AskAsync(string title, string body)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        Cancel();
        if (_disposed)
        {
            return Task.FromResult(false);
        }

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        Title = title;
        Body = body;
        IsOpen = true;
        return answer.Task;
    }

    /// <summary>Takes the card down with this answer; does nothing when no card is up.</summary>
    /// <param name="open">Whether the user chose Open.</param>
    public void Answer(bool open)
    {
        if (_answer is not { } answer)
        {
            return;
        }

        _answer = null;
        answer.SetResult(open);
        IsOpen = false;
        Title = null;
        Body = null;
    }

    /// <summary>Takes the card down with nothing opened; does nothing when no card is up.</summary>
    public void Cancel() => Answer(false);

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
