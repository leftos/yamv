using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Yamv.App.Services;
using Yamv.Core.Theming;

namespace Yamv.App.Views.ThemeSettings;

/// <summary>
/// The theme settings window's preview: a mini YAMV window with a sample document and tree, drawn from <see cref="Resolved"/>
/// through its own resources, so it shows a theme the app does not use. A theme's own css is not drawn.
/// </summary>
public sealed partial class ThemePreview : UserControl
{
    /// <summary>Defines the <see cref="Resolved"/> property.</summary>
    public static readonly StyledProperty<ResolvedTheme?> ResolvedProperty = AvaloniaProperty.Register<ThemePreview, ResolvedTheme?>(
        nameof(Resolved)
    );

    /// <summary>The document's font size, which the mock fixes so the sample fits the pane.</summary>
    private const double BodySize = 13;

    /// <summary>The h1's line height as a multiple of its size, as the page's stylesheet sets it.</summary>
    private const double H1LineHeight = 1.15;

    /// <summary>The colour tokens only the document uses, which the app's resources do not hold.</summary>
    private static readonly string[] PreviewColorTokens =
    [
        "quote",
        "codeLine",
        "syntaxKeyword",
        "syntaxString",
        "syntaxComment",
        "syntaxType",
        "syntaxNumber",
    ];

    private ResourceDictionary? _resources;

    public ThemePreview()
    {
        InitializeComponent();
    }

    /// <summary>The theme to draw, or null for none.</summary>
    public ResolvedTheme? Resolved
    {
        get => GetValue(ResolvedProperty);
        set => SetValue(ResolvedProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResolvedProperty)
        {
            Show(change.GetNewValue<ResolvedTheme?>());
        }
    }

    /// <summary>
    /// The preview's resources for <paramref name="resolved"/>: the chrome brushes and UI font under the app's keys, so they shadow
    /// the app's inside the preview, plus the document-only colours, fonts and sizes.
    /// </summary>
    private static ResourceDictionary BuildResources(ResolvedTheme resolved)
    {
        var resources = new ResourceDictionary();
        ThemeBrushes.FillChrome(resources, resolved);
        foreach (string token in PreviewColorTokens)
        {
            resources[ThemeBridge.ResourceKey(token)] = ThemeBrushes.Brush(resolved, token);
        }

        resources["YamvPreviewBodyFont"] = ThemeBrushes.SansFont(resolved, "bodyFont");
        resources["YamvPreviewHeadingFont"] = ThemeBrushes.SansFont(resolved, "headingFont");
        resources["YamvPreviewMonoFont"] = ThemeBrushes.MonoFont(resolved, "monoFont");
        resources["YamvPreviewLineHeight"] = BodySize * Number(resolved, "lineHeight");
        double h1Size = BodySize * Number(resolved, "h1Size");
        resources["YamvPreviewH1Size"] = h1Size;
        resources["YamvPreviewH1LineHeight"] = h1Size * H1LineHeight;
        resources["YamvPreviewHeadingWeight"] = (FontWeight)(int)Math.Round(Number(resolved, "headingWeight"));
        resources["YamvPreviewH2Rule"] = resolved.Tokens.GetValueOrDefault("h2Rule") is BoolValue { Value: true }
            ? new Thickness(0, 0, 0, 1)
            : new Thickness(0);
        return resources;
    }

    private static double Number(ResolvedTheme resolved, string token) =>
        resolved.Tokens.GetValueOrDefault(token) is NumberValue number
            ? number.Value
            : throw new InvalidOperationException($"The resolved theme '{resolved.Id}' has no number for the token '{token}'.");

    /// <summary>
    /// Draws <paramref name="resolved"/>. The new dictionary goes in before the old one comes out, so no key falls back to the
    /// app's resources in between.
    /// </summary>
    private void Show(ResolvedTheme? resolved)
    {
        IList<IResourceProvider> merged = Resources.MergedDictionaries;
        ResourceDictionary? previous = _resources;
        _resources = resolved is null ? null : BuildResources(resolved);
        if (_resources is not null)
        {
            merged.Add(_resources);
        }

        if (previous is not null)
        {
            merged.Remove(previous);
        }

        if (resolved is null)
        {
            AppearanceTag.Text = "";
            Link.TextDecorations = null;
            return;
        }

        AppearanceTag.Text = resolved.Appearance == Appearance.Dark ? "DARK" : "LIGHT";
        Link.TextDecorations =
        [
            new TextDecoration
            {
                Location = TextDecorationLocation.Underline,
                Stroke = ThemeBrushes.Brush(resolved, "accentSoft"),
                StrokeOffset = 3,
                StrokeOffsetUnit = TextDecorationUnit.Pixel,
            },
        ];
    }
}
