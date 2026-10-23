namespace Yamv.App.Services;

/// <summary>
/// Where a document window shows that an operation the user started failed or was refused: a toast at the bottom centre of
/// its document pane. The failure is still logged by whoever reports it.
/// </summary>
public interface IToasts
{
    /// <summary>
    /// Shows <paramref name="text"/> in a toast; text between backticks shows as inline code. A toast with the same text still
    /// up counts the repeat instead of adding a card.
    /// </summary>
    /// <param name="kind">Which icon the card shows.</param>
    /// <param name="text">One line saying what went wrong.</param>
    void Show(ToastKind kind, string text);
}

/// <summary>What a toast reports, which sets its icon.</summary>
public enum ToastKind
{
    /// <summary>An operation failed: the warning triangle.</summary>
    Warning,

    /// <summary>YAMV refused to do what was asked: the "no" circle.</summary>
    Refusal,
}
