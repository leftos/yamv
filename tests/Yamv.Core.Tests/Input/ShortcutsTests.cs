using Yamv.Core.Input;

namespace Yamv.Core.Tests.Input;

public sealed class ShortcutsTests
{
    [Theory]
    [InlineData("w", ShortcutCommand.CloseDocument)]
    [InlineData("W", ShortcutCommand.CloseDocument)]
    [InlineData("b", ShortcutCommand.ToggleSidebar)]
    [InlineData("B", ShortcutCommand.ToggleSidebar)]
    [InlineData("r", ShortcutCommand.Reload)]
    [InlineData("R", ShortcutCommand.Reload)]
    [InlineData("o", ShortcutCommand.OpenFiles)]
    [InlineData("O", ShortcutCommand.OpenFiles)]
    public void Match_CtrlLetterOnWindows_GivesItsCommand(string key, ShortcutCommand expected) =>
        Assert.Equal(expected, Shortcuts.Match(key, ctrl: true, meta: false, shift: false, alt: false, isMac: false));

    [Theory]
    [InlineData("w", ShortcutCommand.CloseDocument)]
    [InlineData("b", ShortcutCommand.ToggleSidebar)]
    [InlineData("r", ShortcutCommand.Reload)]
    [InlineData("o", ShortcutCommand.OpenFiles)]
    public void Match_CmdLetterOnMac_GivesItsCommand(string key, ShortcutCommand expected) =>
        Assert.Equal(expected, Shortcuts.Match(key, ctrl: false, meta: true, shift: false, alt: false, isMac: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Match_CtrlTab_IsNextDocumentOnBothPlatforms(bool isMac) =>
        Assert.Equal(ShortcutCommand.NextDocument, Shortcuts.Match("Tab", ctrl: true, meta: false, shift: false, alt: false, isMac));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Match_CtrlShiftTab_IsPreviousDocumentOnBothPlatforms(bool isMac) =>
        Assert.Equal(ShortcutCommand.PreviousDocument, Shortcuts.Match("Tab", ctrl: true, meta: false, shift: true, alt: false, isMac));

    [Fact]
    public void Match_ShiftedCapitalW_ClosesTheDocument() =>
        Assert.Equal(ShortcutCommand.CloseDocument, Shortcuts.Match("W", ctrl: true, meta: false, shift: true, alt: false, isMac: false));

    [Theory]
    [InlineData("w")]
    [InlineData("o")]
    public void Match_CmdLetterOnWindows_IsNothing(string key) =>
        Assert.Null(Shortcuts.Match(key, ctrl: false, meta: true, shift: false, alt: false, isMac: false));

    [Theory]
    [InlineData("w")]
    [InlineData("o")]
    public void Match_CtrlLetterOnMac_IsNothing(string key) =>
        Assert.Null(Shortcuts.Match(key, ctrl: true, meta: false, shift: false, alt: false, isMac: true));

    [Fact]
    public void Match_CtrlAndCmdTogether_IsNothing() =>
        Assert.Null(Shortcuts.Match("w", ctrl: true, meta: true, shift: false, alt: false, isMac: true));

    [Theory]
    [InlineData("w", false)]
    [InlineData("o", false)]
    [InlineData("o", true)]
    public void Match_AltWithTheCommandKey_IsNothing(string key, bool isMac) =>
        Assert.Null(Shortcuts.Match(key, ctrl: !isMac, meta: isMac, shift: false, alt: true, isMac));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Match_MetaTab_IsNothing(bool isMac) => Assert.Null(Shortcuts.Match("Tab", ctrl: false, meta: true, shift: false, alt: false, isMac));

    [Fact]
    public void Match_CtrlAltTab_IsNothing() => Assert.Null(Shortcuts.Match("Tab", ctrl: true, meta: false, shift: false, alt: true, isMac: false));

    [Theory]
    [InlineData("w")]
    [InlineData("Tab")]
    public void Match_WithoutTheCommandKey_IsNothing(string key) =>
        Assert.Null(Shortcuts.Match(key, ctrl: false, meta: false, shift: false, alt: false, isMac: false));

    [Theory]
    [InlineData("p")]
    [InlineData("1")]
    [InlineData("[")]
    [InlineData("F5")]
    [InlineData("")]
    public void Match_AnotherKey_IsNothing(string key) =>
        Assert.Null(Shortcuts.Match(key, ctrl: true, meta: false, shift: false, alt: false, isMac: false));
}
