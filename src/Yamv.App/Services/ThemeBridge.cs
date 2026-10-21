using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Yamv.App.Controls;
using Yamv.Core.Diagnostics;
using Yamv.Core.Theming;
using AvaloniaThemeVariant = Avalonia.Styling.ThemeVariant;

namespace Yamv.App.Services;

/// <summary>
/// Carries the chosen theme to the app: a <see cref="SolidColorBrush"/> per chrome colour token and the UI font in the
/// application's resources (for <c>{DynamicResource}</c>), the Fluent theme variant, and the page css of every registered
/// document surface. With the <c>system</c> appearance it follows the platform's light or dark setting. Every member is called
/// on the UI thread.
/// </summary>
public sealed class ThemeBridge : IDisposable
{
    /// <summary>The theme used when the requested one is unknown or invalid.</summary>
    public const string FallbackThemeId = "studio";

    /// <summary>The resource key of the chrome's UI <see cref="FontFamily"/>.</summary>
    public const string UiFontKey = "YamvUiFont";

    private readonly ThemeCatalog _catalog;
    private readonly Application _application;
    private readonly IAppLog _log;
    private readonly IPlatformSettings? _platformSettings;
    private readonly List<IDocumentSurface> _surfaces = [];
    private ThemeDefinition? _theme;
    private bool _followsSystem;
    private bool _disposed;
    private string? _css;
    private ResourceDictionary? _resources;

    /// <summary>Creates a bridge that writes into <paramref name="application"/> and starts following the platform's light or dark setting.</summary>
    /// <param name="catalog">The themes to choose from.</param>
    /// <param name="application">The application whose resources and theme variant are set.</param>
    /// <param name="log">Where an unknown or invalid theme is reported.</param>
    public ThemeBridge(ThemeCatalog catalog, Application application, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _application = application;
        _log = log;
        _platformSettings = application.PlatformSettings;
        _platformSettings?.ColorValuesChanged += OnPlatformColorsChanged;
    }

    /// <summary>
    /// Raised on the thread that called <see cref="Apply"/>, after an <see cref="Apply"/> that changed
    /// <see cref="RequestedThemeId"/> or <see cref="RequestedAppearance"/>; both already hold the new values. An
    /// <see cref="Apply"/> with the same requested values raises nothing.
    /// </summary>
    public event EventHandler? RequestChanged;

    /// <summary>The theme in use, or null before the first <see cref="Apply"/>.</summary>
    public ResolvedTheme? Current { get; private set; }

    /// <summary>
    /// The theme id the last <see cref="Apply"/> asked for, even when it resolved to Studio; <c>studio</c> before any. The session
    /// saves this, so a user theme that is invalid for a moment is not rewritten to Studio.
    /// </summary>
    public string RequestedThemeId { get; private set; } = FallbackThemeId;

    /// <summary>
    /// The appearance the last <see cref="Apply"/> asked for: <c>light</c>, <c>dark</c> or <c>system</c>; <c>system</c> before any.
    /// </summary>
    public string RequestedAppearance { get; private set; } = "system";

    /// <summary>The resource key of a chrome colour token's brush: <c>Yamv</c> and the token name with its first letter upper-cased.</summary>
    public static string ResourceKey(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return "Yamv" + char.ToUpperInvariant(token[0]) + token[1..];
    }

    /// <summary>
    /// Uses the theme <paramref name="themeId"/>, preferring its valid entry when the catalog lists the id twice, or the Studio
    /// theme when it is unknown or invalid.
    /// </summary>
    /// <param name="themeId">The id of the theme to use.</param>
    /// <param name="appearance"><c>light</c>, <c>dark</c> or <c>system</c>; anything else is <c>system</c>.</param>
    /// <remarks>Raises <see cref="RequestChanged"/> when the requested theme id or appearance changed.</remarks>
    public void Apply(string themeId, string appearance)
    {
        ArgumentNullException.ThrowIfNull(themeId);
        ArgumentNullException.ThrowIfNull(appearance);
        ThemeDefinition theme = Find(themeId);
        bool followsSystem = !IsFixed(appearance, out Appearance requested);
        ApplyResolved(ThemeResolver.Resolve(theme, followsSystem ? PlatformAppearance() : requested));
        _theme = theme;
        _followsSystem = followsSystem;
        string requestedAppearance = followsSystem ? "system" : appearance;
        bool changed =
            !string.Equals(RequestedThemeId, themeId, StringComparison.Ordinal)
            || !string.Equals(RequestedAppearance, requestedAppearance, StringComparison.Ordinal);
        RequestedThemeId = themeId;
        RequestedAppearance = requestedAppearance;
        if (changed)
        {
            RequestChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Starts sending the theme css to <paramref name="surface"/>, beginning with the current one.</summary>
    public void Register(IDocumentSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_surfaces.Contains(surface))
        {
            return;
        }

        _surfaces.Add(surface);
        if (_css is not null)
        {
            Send(surface, _css);
        }
    }

    /// <summary>Stops sending the theme css to <paramref name="surface"/>.</summary>
    public void Unregister(IDocumentSurface surface) => _surfaces.Remove(surface);

    /// <summary>Stops following the platform's light or dark setting.</summary>
    public void Dispose()
    {
        _disposed = true;
        _platformSettings?.ColorValuesChanged -= OnPlatformColorsChanged;

        _surfaces.Clear();
    }

    private static bool IsFixed(string appearance, out Appearance requested)
    {
        switch (appearance)
        {
            case "light":
                requested = Appearance.Light;
                return true;
            case "dark":
                requested = Appearance.Dark;
                return true;
            default:
                requested = Appearance.Light;
                return false;
        }
    }

    private ThemeDefinition Find(string themeId)
    {
        IReadOnlyList<ThemeEntry> entries = _catalog.LoadAll();
        ThemeDefinition? wanted = ValidTheme(entries, themeId);
        if (wanted is not null)
        {
            return wanted;
        }

        _log.LogWarning($"The theme '{themeId}' is unknown or invalid; using '{FallbackThemeId}' instead.");
        return ValidTheme(entries, FallbackThemeId)
            ?? throw new InvalidOperationException($"The fallback theme '{FallbackThemeId}' is not in the catalog or is invalid.");
    }

    private static ThemeDefinition? ValidTheme(IReadOnlyList<ThemeEntry> entries, string themeId) =>
        entries.FirstOrDefault(entry => ThemeIds.Comparer.Equals(entry.Id, themeId) && entry.Result.Theme is not null)?.Result.Theme;

    private Appearance PlatformAppearance() =>
        _platformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark ? Appearance.Dark : Appearance.Light;

    /// <summary>
    /// Builds the palette and css first, so a theme that cannot be shown changes nothing, then swaps one merged dictionary of
    /// brushes and font into the application's resources.
    /// </summary>
    private void ApplyResolved(ResolvedTheme resolved)
    {
        var resources = new ResourceDictionary();
        ThemeBrushes.FillChrome(resources, resolved);
        string css = ThemeCss.Build(resolved);

        Current = resolved;
        _css = css;
        _application.RequestedThemeVariant = resolved.Appearance == Appearance.Dark ? AvaloniaThemeVariant.Dark : AvaloniaThemeVariant.Light;
        IList<IResourceProvider> merged = _application.Resources.MergedDictionaries;
        int index = _resources is null ? -1 : merged.IndexOf(_resources);
        if (index >= 0)
        {
            merged[index] = resources;
        }
        else
        {
            merged.Add(resources);
        }

        _resources = resources;
        foreach (IDocumentSurface surface in _surfaces)
        {
            Send(surface, _css);
        }
    }

    private void Send(IDocumentSurface surface, string css) =>
        surface
            .SetThemeCssAsync(css)
            .ContinueWith(
                task => _log.LogError("Cannot send the theme css to a document view.", task.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default
            );

    private void OnPlatformColorsChanged(object? sender, PlatformColorValues values)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            FollowPlatform();
        }
        else
        {
            Dispatcher.UIThread.Post(FollowPlatform);
        }
    }

    private void FollowPlatform()
    {
        if (_disposed || !_followsSystem || _theme is null)
        {
            return;
        }

        ResolvedTheme resolved = ThemeResolver.Resolve(_theme, PlatformAppearance());
        if (resolved.Appearance != Current?.Appearance)
        {
            ApplyResolved(resolved);
        }
    }
}
