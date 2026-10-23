using Yamv.App.Services;

namespace Yamv.App.Tests.Fakes;

/// <summary>An <see cref="IToasts"/> that keeps every toast shown, so a test can check what the user was told.</summary>
public sealed class RecordingToasts : IToasts
{
    /// <summary>Every toast shown, oldest first.</summary>
    public List<(ToastKind Kind, string Text)> Shown { get; } = [];

    public void Show(ToastKind kind, string text) => Shown.Add((kind, text));
}
