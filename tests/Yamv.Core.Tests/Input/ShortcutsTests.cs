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
    public void Match_CtrlLetterOnWindows_GivesItsCommand(string key, ShortcutCommand expected) =>
        Assert.Equal(expected, Shortcuts.Match(key, ctrl: true, meta: false, shift: false, alt: false, isMac: false));

    [Theory]
    [InlineData("w", ShortcutCommand.CloseDocument)]
    [InlineData("b", ShortcutCommand.ToggleSidebar)]
    [InlineData("r", ShortcutCommand.Reload)]
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

    [Fact]
    public void Match_CmdWOnWindows_IsNothing() => Assert.Null(Shortcuts.Match("w", ctrl: false, meta: true, shift: false, alt: false, isMac: false));

    [Fact]
    public void Match_CtrlWOnMac_IsNothing() => Assert.Null(Shortcuts.Match("w", ctrl: true, meta: false, shift: false, alt: false, isMac: true));

    [Fact]
    public void Match_CtrlAndCmdTogether_IsNothing() =>
        Assert.Null(Shortcuts.Match("w", ctrl: true, meta: true, shift: false, alt: false, isMac: true));

    [Fact]
    public void Match_CtrlAltW_IsNothing() => Assert.Null(Shortcuts.Match("w", ctrl: true, meta: false, shift: false, alt: true, isMac: false));

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
    [InlineData("o")]
    [InlineData("1")]
    [InlineData("[")]
    [InlineData("F5")]
    [InlineData("")]
    public void Match_AnotherKey_IsNothing(string key) =>
        Assert.Null(Shortcuts.Match(key, ctrl: true, meta: false, shift: false, alt: false, isMac: false));
}
