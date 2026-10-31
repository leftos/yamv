using Yamv.App.ViewModels;

namespace Yamv.App.Tests.ViewModels;

public sealed class ConfirmCardViewModelTests
{
    [Fact]
    public async Task Open_answers_true()
    {
        using var card = new ConfirmCardViewModel();

        Task<bool> asked = card.AskAsync("Open a.ps1?", "It's a script.");

        Assert.True(card.IsOpen);
        Assert.Equal("Open a.ps1?", card.Title);
        Assert.Equal("It's a script.", card.Body);
        card.Answer(open: true);
        Assert.True(await asked);
        Assert.False(card.IsOpen);
    }

    [Fact]
    public async Task Cancel_answers_false()
    {
        using var card = new ConfirmCardViewModel();

        Task<bool> asked = card.AskAsync("Open a.ps1?", "It's a script.");
        card.Cancel();

        Assert.False(await asked);
        Assert.False(card.IsOpen);
    }

    [Fact]
    public async Task A_second_ask_cancels_the_first()
    {
        using var card = new ConfirmCardViewModel();

        Task<bool> first = card.AskAsync("Open a.ps1?", "One.");
        Task<bool> second = card.AskAsync("Open b.ps1?", "Two.");

        Assert.False(await first);
        Assert.False(second.IsCompleted);
        Assert.True(card.IsOpen);
        Assert.Equal("Open b.ps1?", card.Title);
        card.Answer(open: true);
        Assert.True(await second);
    }

    [Fact]
    public async Task Answering_twice_counts_once()
    {
        using var card = new ConfirmCardViewModel();

        Task<bool> asked = card.AskAsync("Open a.ps1?", "It's a script.");
        card.Answer(open: false);
        card.Answer(open: true);
        card.Cancel();

        Assert.False(await asked);
    }

    [Fact]
    public async Task Disposing_cancels()
    {
        var card = new ConfirmCardViewModel();

        Task<bool> asked = card.AskAsync("Open a.ps1?", "It's a script.");
        card.Dispose();

        Assert.False(await asked);
        Assert.False(card.IsOpen);
        Assert.False(await card.AskAsync("Open b.ps1?", "Too late."));
        Assert.False(card.IsOpen);
    }
}
