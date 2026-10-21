using System.Diagnostics.CodeAnalysis;
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
    private readonly Func<Appearance> _platformAppearance;
    private readonly List<IDocumentSurface> _surfaces = [];
    private ThemeDefinition? _theme;
    private bool _followsSystem;
    private bool _disposed;
    private bool _keptLastGood;
    private string? _css;
    private ResourceDictionary? _resources;

    /// <summary>Creates a bridge that writes into <paramref name="application"/> and starts following the platform's light or dark setting.</summary>
    /// <param name="catalog">The themes to choose from.</param>
    /// <param name="application">The application whose resources and theme variant are set.</param>
    /// <param name="log">Where an unknown or invalid theme is reported.</param>
    public ThemeBridge(ThemeCatalog catalog, Application application, IAppLog log)
        : this(catalog, application, log, PlatformAppearanceOf(application)) { }

    /// <summary>
    /// Creates a bridge that reads the platform's light or dark setting from <paramref name="platformAppearance"/>, and still
    /// re-reads it when the application's platform settings report a change.
    /// </summary>
    internal ThemeBridge(ThemeCatalog catalog, Application application, IAppLog log, Func<Appearance> platformAppearance)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(platformAppearance);
        _catalog = catalog;
        _application = application;
        _log = log;
        _platformAppearance = platformAppearance;
        _platformSettings = application.PlatformSettings;
        _platformSettings?.ColorValuesChanged += OnPlatformColorsChanged;
    }

    /// <summary>
    /// Raised on the thread that called <see cref="Apply"/>, after an <see cref="Apply"/> that changed
    /// <see cref="RequestedThemeId"/> or <see cref="RequestedAppearance"/>; both already hold the new values. An
    /// <see cref="Apply"/> with the same requested values raises nothing.
    /// </summary>
    public event EventHandler? RequestChanged;

    /// <summary>
    /// Raised after every <see cref="Reload"/> before the bridge is disposed, changed or not and even before the first
    /// <see cref="Apply"/>, once any new version has been applied, so <see cref="Current"/> already holds the theme shown.
    /// </summary>
    public event EventHandler? ThemesReloaded;

    /// <summary>The theme in use, or null before the first <see cref="Apply"/>.</summary>
    public ResolvedTheme? Current { get; private set; }

    /// <summary>
    /// The theme id the last <see cref="Apply"/> asked for, even when it resolved to Studio; <c>studio</c> before any. This and
    /// <see cref="RequestedAppearance"/> are the values <c>App</c> writes to <c>settings.json</c> through <see cref="RequestChanged"/>,
    /// so a user theme that is invalid for a moment is not rewritten to Studio.
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
    /// theme when it is unknown or invalid. While the requested theme's last good version is kept because its file went invalid
    /// or missing, an <see cref="Apply"/> of that same id keeps it too, in the appearance asked for, unless a valid version is
    /// on disk again, which it then uses. An <see cref="Apply"/> of another id ends the keep.
    /// </summary>
    /// <param name="themeId">The id of the theme to use.</param>
    /// <param name="appearance"><c>light</c>, <c>dark</c> or <c>system</c>; anything else is <c>system</c>.</param>
    /// <remarks>Raises <see cref="RequestChanged"/> when the requested theme id or appearance changed.</remarks>
    public void Apply(string themeId, string appearance)
    {
        ArgumentNullException.ThrowIfNull(themeId);
        ArgumentNullException.ThrowIfNull(appearance);
        ThemeDefinition theme = Pick(themeId);
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

    /// <summary>
    /// Reads the themes again and shows the requested theme's new version when its content changed; the same content changes
    /// nothing. When the theme in use has turned invalid or gone from disk, a user file overriding a built-in id included, its
    /// last good version stays in use. A requested theme shown as Studio for want of a good version is used once it is valid.
    /// Applies nothing before the first <see cref="Apply"/>, and does nothing at all after <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// Never changes <see cref="RequestedThemeId"/> or <see cref="RequestedAppearance"/> and never raises
    /// <see cref="RequestChanged"/>; raises <see cref="ThemesReloaded"/> at the end of every call before <see cref="Dispose"/>,
    /// before the first <see cref="Apply"/> included.
    /// </remarks>
    public void Reload()
    {
        if (_disposed)
        {
            return;
        }

        if (_theme is { } shown)
        {
            ReloadRequested(shown, ValidTheme(_catalog.LoadAll(), RequestedThemeId));
        }

        ThemesReloaded?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// The definition an <see cref="Apply"/> of <paramref name="themeId"/> uses: while the requested theme's last good version is
    /// kept and <paramref name="themeId"/> is that theme, the kept version, or its valid version on disk once there is one;
    /// otherwise the catalog's, or Studio.
    /// </summary>
    private ThemeDefinition Pick(string themeId)
    {
        if (!_keptLastGood || _theme is not { } kept || !ThemeIds.Comparer.Equals(themeId, RequestedThemeId))
        {
            _keptLastGood = false;
            return Find(themeId);
        }

        ThemeDefinition? fresh = ValidTheme(_catalog.LoadAll(), themeId);
        if (KeepsLastGood(kept, fresh))
        {
            return kept;
        }

        _keptLastGood = false;
        LogRecovery();
        return fresh;
    }

    /// <summary>
    /// Applies <paramref name="fresh"/>, the requested theme as it is on disk now, unless <see cref="KeepsLastGood"/> keeps what is
    /// shown or the content is the same as the good version in use.
    /// </summary>
    private void ReloadRequested(ThemeDefinition shown, ThemeDefinition? fresh)
    {
        if (KeepsLastGood(shown, fresh))
        {
            return;
        }

        bool hasGoodVersion = ThemeIds.Comparer.Equals(shown.Id, RequestedThemeId);
        bool recovered = _keptLastGood || !hasGoodVersion;
        _keptLastGood = false;
        if (!hasGoodVersion || !fresh.ContentEquals(shown))
        {
            _ = IsFixed(RequestedAppearance, out Appearance requested);
            ApplyResolved(ThemeResolver.Resolve(fresh, _followsSystem ? PlatformAppearance() : requested));
            _theme = fresh;
        }

        if (recovered)
        {
            LogRecovery();
        }
    }

    /// <summary>
    /// Whether <paramref name="shown"/> stays in use rather than <paramref name="fresh"/>, the requested theme as it is on disk now:
    /// when there is no valid version on disk, or only the built-in where <paramref name="shown"/> is the user's override of it.
    /// The first time the requested theme's good version is kept, sets <c>_keptLastGood</c>, which stays set until a valid version
    /// is used again or another theme is applied, and logs a warning that the bad streak has begun.
    /// </summary>
    private bool KeepsLastGood(ThemeDefinition shown, [NotNullWhen(false)] ThemeDefinition? fresh)
    {
        bool hasGoodVersion = ThemeIds.Comparer.Equals(shown.Id, RequestedThemeId);
        bool overrideLost = hasGoodVersion && shown.Source == ThemeSource.User && fresh?.Source == ThemeSource.BuiltIn;
        if (fresh is not null && !overrideLost)
        {
            return false;
        }

        if (hasGoodVersion && !_keptLastGood)
        {
            _keptLastGood = true;
            _log.LogWarning($"The theme '{RequestedThemeId}' is invalid or gone from the themes folder; keeping its last good version.");
        }

        return true;
    }

    private void LogRecovery() => _log.LogInfo($"The theme '{RequestedThemeId}' loads from the themes folder; using it.");

    private static ThemeDefinition? ValidTheme(IReadOnlyList<ThemeEntry> entries, string themeId) =>
        entries.FirstOrDefault(entry => ThemeIds.Comparer.Equals(entry.Id, themeId) && entry.Result.Theme is not null)?.Result.Theme;

    private static Func<Appearance> PlatformAppearanceOf(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        IPlatformSettings? settings = application.PlatformSettings;
        return () => settings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark ? Appearance.Dark : Appearance.Light;
    }

    private Appearance PlatformAppearance() => _platformAppearance();

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

    /// <summary>
    /// Re-resolves the theme in use for the platform's light or dark setting when the bridge follows it; runs when the platform
    /// reports a change.
    /// </summary>
    internal void FollowPlatform()
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
