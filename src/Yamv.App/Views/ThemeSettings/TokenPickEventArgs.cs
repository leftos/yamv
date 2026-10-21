using Yamv.Core.Theming;

namespace Yamv.App.Views.ThemeSettings;

/// <summary>The token a swatch click asks to pick a colour for, and the tab it was clicked in.</summary>
public sealed class TokenPickEventArgs(string token, ThemeTab tab) : EventArgs
{
    public string Token { get; } = token;

    public ThemeTab Tab { get; } = tab;
}
