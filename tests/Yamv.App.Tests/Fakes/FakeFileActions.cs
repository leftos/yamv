using Avalonia.Controls;
using Yamv.App.Services;
using Yamv.Core.Platform;

namespace Yamv.App.Tests.Fakes;

/// <summary>An <see cref="IFileActions"/> that answers picks with set paths and records reveals and copies.</summary>
internal sealed class FakeFileActions : IFileActions
{
    /// <summary>What the next Open files pick returns; empty is a cancelled pick.</summary>
    public IReadOnlyList<string> DocumentsToPick { get; set; } = [];

    /// <summary>How many Open files picks were asked for.</summary>
    public int DocumentPicks { get; private set; }

    /// <summary>What the next Locate pick returns; null is a cancelled pick.</summary>
    public string? ReplacementToPick { get; set; }

    /// <summary>The old path of every Locate pick asked for, oldest first.</summary>
    public List<string> ReplacementRequests { get; } = [];

    /// <summary>Every reveal asked for, oldest first.</summary>
    public List<(string Path, RevealKind Kind)> Reveals { get; } = [];

    /// <summary>Every text copied, oldest first.</summary>
    public List<string> Copied { get; } = [];

    public Task<IReadOnlyList<string>> PickDocumentsAsync(TopLevel owner)
    {
        DocumentPicks++;
        return Task.FromResult(DocumentsToPick);
    }

    public Task<string?> PickReplacementAsync(TopLevel owner, string oldPath)
    {
        ReplacementRequests.Add(oldPath);
        return Task.FromResult(ReplacementToPick);
    }

    public void Reveal(string path, RevealKind kind) => Reveals.Add((path, kind));

    public Task CopyTextAsync(TopLevel owner, string text)
    {
        Copied.Add(text);
        return Task.CompletedTask;
    }
}
