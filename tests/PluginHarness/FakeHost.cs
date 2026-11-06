using BetterLyrics.Sdk.Enums;
using BetterLyrics.Sdk.Interfaces.Plugins;

namespace BetterLyrics.Plugins.AILyricsTranslation.Harness;

/// <summary>
/// Minimal stand-in for the host's <c>PluginConfigurator</c>: keeps an in-memory dictionary and
/// persists nothing, which is enough to drive a plugin under test.
/// </summary>
internal sealed class FakeConfigurator : IConfigurator
{
    private readonly Dictionary<string, object?> _config = new(StringComparer.Ordinal);

    public event EventHandler<string, ConfigChangedBy>? OnConfigChanged;

    public object Get(string key, object defaultValue) =>
        _config.TryGetValue(key, out var value) && value != null ? value : defaultValue;

    public void Set(string key, object value, ConfigChangedBy configChangedBy)
    {
        _config[key] = value;
        OnConfigChanged?.Invoke(key, configChangedBy);
    }
}

/// <summary>
/// Minimal stand-in for the host's <c>PluginLocalizer</c>: returns the key itself, exactly like the
/// real one does when a translation is missing, so missing Langs keys are visible in the output.
/// </summary>
internal sealed class FakeLocalizer : ILocalizer
{
    private readonly Dictionary<string, string> _strings;

    public FakeLocalizer(Dictionary<string, string>? strings = null) => _strings = strings ?? [];

    public string CurrentLanguage => "en";

    public string this[string key] => GetString(key);

    public string GetString(string key) => _strings.TryGetValue(key, out var value) ? value : key;
}

internal sealed class FakePluginContext(string pluginDirectory, IConfigurator configurator, ILocalizer localizer)
    : IPluginContext
{
    public string PluginDirectory { get; } = pluginDirectory;

    public IAIService? AIService => null;

    public ILocalizer Localizer { get; } = localizer;

    public IConfigurator Configurator { get; } = configurator;
}
