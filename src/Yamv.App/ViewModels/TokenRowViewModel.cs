using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yamv.Core.Theming;

namespace Yamv.App.ViewModels;

/// <summary>One group of the token editor (Page, Chrome, Code, Syntax, Type, Layout) and its rows, in token order.</summary>
/// <param name="Name">The group's name as <see cref="TokenInfo.Group"/> holds it.</param>
/// <param name="Rows">The group's rows.</param>
public sealed record TokenGroupViewModel(string Name, IReadOnlyList<TokenRowViewModel> Rows)
{
    /// <summary>The group's header, upper-cased as drawn.</summary>
    public string Header => Name.ToUpperInvariant();
}

/// <summary>
/// One token row of the token editor's open tab: the value the token resolves to there, whether the tab sets it, whether it differs
/// from the saved theme, and the text its field shows. An edit commits through the settings view model, so the row follows the
/// working copy after every edit.
/// </summary>
public sealed partial class TokenRowViewModel : ViewModelBase
{
    private readonly ThemeSettingsViewModel _owner;

    /// <summary>Creates the row of <paramref name="token"/>, which shows nothing until <see cref="Update"/>.</summary>
    public TokenRowViewModel(TokenInfo token, ThemeSettingsViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        Token = token;
        _owner = owner;
        Value = ThemeTokens.DefaultsFor(Appearance.Light)[token.Name];
        Text = TextOf(Value);
    }

    public TokenInfo Token { get; }

    /// <summary>The token's name in theme files, such as <c>selectionBackground</c>.</summary>
    public string Name => Token.Name;

    /// <summary>The friendly label, such as "Selected row".</summary>
    public string Label => Token.Label;

    /// <summary>The row's tooltip: the label, then the token name and its CSS custom property.</summary>
    public string Tooltip => $"{Token.Label}\n{Token.Name} · {Token.CssVariable}";

    public string Unit => Token.Unit;

    public bool IsColor => Token.Kind == TokenKind.Color;

    public bool IsFont => Token.Kind == TokenKind.Font;

    public bool IsNumber => Token.Kind == TokenKind.Number;

    public bool IsBool => Token.Kind == TokenKind.Bool;

    /// <summary>The value the token resolves to in the open tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Swatch), nameof(IsChecked))]
    public partial ThemeValue Value { get; private set; }

    /// <summary>Whether the open tab's own layer sets the token; otherwise the value is inherited and shown grey.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInherited), nameof(CanReset))]
    public partial bool IsSet { get; private set; }

    /// <summary>Whether the token's value in the open tab's layer differs from the saved theme's: the unsaved dot.</summary>
    [ObservableProperty]
    public partial bool IsChanged { get; private set; }

    /// <summary>Whether the row can be edited: a valid user theme, on Shared or on a half the theme has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    public partial bool IsEditable { get; private set; }

    /// <summary>The text the field shows; typing changes it, and <see cref="Commit"/> applies it.</summary>
    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>Why the last committed text was not applied, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool IsInherited => !IsSet;

    /// <summary>Whether the reset arrow shows: the tab sets the token and the row is editable.</summary>
    public bool CanReset => IsSet && IsEditable;

    /// <summary>The colour of a colour token's swatch; transparent for the other kinds.</summary>
    public Color Swatch => Value is ColorValue color ? Color.FromUInt32(color.ToArgb()) : Colors.Transparent;

    /// <summary>Whether a switch token is on.</summary>
    public bool IsChecked => Value is BoolValue { Value: true };

    /// <summary>Shows <paramref name="row"/>, puts the field's text back to its value and clears any error.</summary>
    public void Update(ThemeTokenRow row, bool changed, bool editable)
    {
        ArgumentNullException.ThrowIfNull(row);
        Value = row.Value;
        IsSet = row.SetHere;
        IsChanged = changed;
        IsEditable = editable;
        Text = TextOf(row.Value);
        Error = null;
    }

    /// <summary>
    /// Applies <paramref name="text"/> to the open tab; returns whether it was valid. Text that parses to the value shown changes
    /// nothing when focus leaves the field, so leaving an untouched field does not set an inherited value; Enter on an inherited
    /// row sets it all the same, so the tab keeps it when the layer under it changes. Invalid text sets <see cref="Error"/> and
    /// leaves the working copy as it was.
    /// </summary>
    /// <param name="text">The field's text.</param>
    /// <param name="fromEnter">Whether Enter committed the field, rather than focus leaving it.</param>
    public bool Commit(string text, bool fromEnter)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsEditable)
        {
            return false;
        }

        (ThemeValue? value, string? problem) = Parse(text.Trim());
        if (value is null)
        {
            Text = text;
            Error = problem;
            return false;
        }

        if (Equals(value, Value) && (IsSet || !fromEnter))
        {
            Text = TextOf(Value);
            Error = null;
            return true;
        }

        try
        {
            _owner.SetToken(_owner.SelectedTab, Name, value);
        }
        catch (ThemeException exception)
        {
            Text = text;
            Error = exception.Message;
            return false;
        }

        return true;
    }

    /// <summary>Puts the field's text back to the value shown, without applying anything.</summary>
    public void CancelEdit()
    {
        Text = TextOf(Value);
        Error = null;
    }

    /// <summary>Removes the token from the open tab, so it inherits again.</summary>
    [RelayCommand]
    public void Reset()
    {
        if (CanReset)
        {
            _owner.ResetToken(_owner.SelectedTab, Name);
        }
    }

    /// <summary>Turns a switch token over.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (IsEditable && IsBool)
        {
            _owner.SetToken(_owner.SelectedTab, Name, new BoolValue(!IsChecked));
        }
    }

    private static string TextOf(ThemeValue value) =>
        value switch
        {
            ColorValue color => color.Hex,
            FontValue font => font.Family,
            NumberValue number => number.Value.ToString(CultureInfo.InvariantCulture),
            BoolValue flag => flag.Value ? "true" : "false",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown theme value."),
        };

    private (ThemeValue? Value, string? Problem) Parse(string text) =>
        Token.Kind switch
        {
            TokenKind.Color => ColorValue.Normalize(text) is { } hex ? (new ColorValue(hex), null) : (null, ColorValue.NotAColour(text)),
            TokenKind.Number => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? (new NumberValue(number), null)
                : (null, $"'{text}' is not a number."),
            TokenKind.Font => (new FontValue(text), null),
            _ => (null, $"'{Token.Name}' is set with its switch, not as text."),
        };
}
