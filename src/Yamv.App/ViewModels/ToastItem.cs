using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Yamv.App.Services;

namespace Yamv.App.ViewModels;

/// <summary>
/// One toast card: its icon, its text cut into plain and inline-code pieces, how many times the same text was shown while it
/// was up, and the share of its fade still to run.
/// </summary>
public sealed partial class ToastItem : ViewModelBase
{
    internal ToastItem(ToastKind kind, string text)
    {
        Kind = kind;
        Text = text;
        Segments = SegmentsOf(text);
    }

    /// <summary>Which icon the card shows.</summary>
    public ToastKind Kind { get; }

    /// <summary>True for a failure, whose card shows the warning triangle.</summary>
    public bool IsWarning => Kind == ToastKind.Warning;

    /// <summary>True for a refusal, whose card shows the "no" circle.</summary>
    public bool IsRefusal => Kind == ToastKind.Refusal;

    /// <summary>The text as it was shown, backticks included.</summary>
    public string Text { get; }

    /// <summary>The text in order, cut where a backtick opens or closes inline code.</summary>
    public IReadOnlyList<ToastSegment> Segments { get; }

    /// <summary>How many times this text was shown while the card was up; 1 for a card shown once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; internal set; } = 1;

    /// <summary>True once the text was shown again while the card was up.</summary>
    public bool HasCount => Count > 1;

    /// <summary>The card's count, as "×2", "×3", ….</summary>
    public string CountText => string.Create(CultureInfo.InvariantCulture, $"×{Count}");

    /// <summary>The share of the card's 5 s still to run, from 1 when shown to 0 when it fades.</summary>
    [ObservableProperty]
    public partial double TimeLeft { get; internal set; } = 1;

    /// <summary>How long the card has counted down while its window was active.</summary>
    internal TimeSpan Elapsed { get; set; }

    /// <summary>
    /// <paramref name="text"/> cut at its backticks, every second piece inline code; a backtick with no partner after it is
    /// kept as text. Empty pieces are left out.
    /// </summary>
    private static List<ToastSegment> SegmentsOf(string text)
    {
        List<string> parts = [.. text.Split('`')];
        if (parts.Count % 2 == 0)
        {
            parts[^2] += "`" + parts[^1];
            parts.RemoveAt(parts.Count - 1);
        }

        List<ToastSegment> segments = [];
        for (int index = 0; index < parts.Count; index++)
        {
            if (parts[index].Length > 0)
            {
                segments.Add(new ToastSegment(parts[index], IsCode: index % 2 == 1));
            }
        }

        return segments;
    }
}

/// <summary>A piece of a toast's text.</summary>
/// <param name="Text">The piece, without its backticks.</param>
/// <param name="IsCode">True when it was between backticks, so it shows as inline code.</param>
public sealed record ToastSegment(string Text, bool IsCode);
