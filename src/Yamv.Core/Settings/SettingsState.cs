using System.Text.Json;

namespace Yamv.Core.Settings;

/// <summary>
/// What the settings file holds, read-only: the two values and the top-level fields the format does not define, kept so a
/// rewrite does not lose them.
/// </summary>
internal sealed record SettingsState(AppSettings Values, IReadOnlyDictionary<string, JsonElement>? UnknownFields)
{
    public static SettingsState Empty { get; } = new(AppSettings.Default, null);
}
