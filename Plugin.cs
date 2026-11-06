using BetterLyrics.Sdk.Abstractions.Plugins;
using BetterLyrics.Sdk.Helpers;
using BetterLyrics.Sdk.Interfaces.Plugins;
using BetterLyrics.Sdk.Models.SettingsSchema;
using NLanguageTag;
using System.Reflection;

namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// Built-in translation plugin for BetterLyrics.
///
/// It is a 1:1 feature port of the standalone
/// "BetterLyrics AI Lyrics Translation Assistant" (BetterLyrics.py):
/// the Flask HTTP service of the original tool is replaced by the host's
/// <see cref="ILyricsTranslator"/> extension point, so the translation pipeline runs in-process
/// and every translated lyric is emitted as Simplified Chinese.
/// </summary>
public class Plugin : PluginBase<Config>, ILyricsTranslator, IPlugin
{
    private PluginLogger? _logger;
    private TranslationBackends? _backends;
    private TranslationEngine? _engine;
    private HttpClient? _http;

    public override string Title { get; set; } = "AI Lyrics Translation";

    /// <summary>
    /// Exposed so the test harness can drive the pipeline directly.
    /// </summary>
    internal TranslationEngine? Engine => _engine;

    public async Task<string?> GetTranslationAsync(string text, LanguageTag? targetLangTag)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var engine = _engine;
        if (engine == null)
        {
            _logger?.Warn("Translation requested before initialization completed");
            return text;
        }

        return await engine.TranslateAsync(text, Config, targetLangTag, CancellationToken.None);
    }

    protected override Task OnInitializeAsync()
    {
        _logger = new PluginLogger(Path.Combine(Context.PluginDirectory, "logs"));
        _logger.Info($"Plugin initializing in {Context.PluginDirectory}");

        // A system proxy without a local bypass entry answers http://127.0.0.1:... with 404, which
        // silently turns a local mirror into "the original text comes back". The bypass lives in
        // the handler because HttpClient exposes Proxy only through its constructor.
        var handler = new HttpClientHandler
        {
            Proxy = new LoopbackBypassProxy(HttpClient.DefaultProxy),
            UseProxy = true
        };

        _http = new HttpClient(handler)
        {
            // Every request gets its own deadline from TranslationBackends.SendAsync, which honours
            // the user's "Request Timeout (seconds)" setting. This outer limit would fire first and
            // mask it, so it is disabled — the per-request token is the single source of truth.
            Timeout = Timeout.InfiniteTimeSpan
        };

        _backends = new TranslationBackends(_http, _logger);
        _engine = new TranslationEngine(_backends, _logger);

        _logger.Info($"Mode={Config.Mode}, Url={Config.ApiUrl}, Model={Config.ModelName}, " +
                     $"ToSimplifiedChinese={Config.ToSimplifiedChinese}, " +
                     $"RequestTimeout={Config.RequestTimeoutSeconds:0.#}s");

        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _logger?.Info("Plugin shutting down");
        _http?.Dispose();
        _http = null;
        _engine = null;
        _backends = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The base implementation renders every string as a free-text box and can never emit a
    /// button or a password box. Re-implementing <see cref="IPlugin"/> here makes the host pick up
    /// this richer schema: <c>Mode</c> becomes a combo box, <c>Temperature</c> and
    /// <c>ChunkThreshold</c> get real ranges, <c>ApiKey</c> becomes a password box and
    /// <c>TestConnection</c> becomes a button.
    /// </summary>
    public new Dictionary<string, SettingDef> GetSettingDefDict() => BuildSettingDefDict();

    Dictionary<string, SettingDef> IPlugin.GetSettingDefDict() => BuildSettingDefDict();

    private Dictionary<string, SettingDef> BuildSettingDefDict()
    {
        var loc = Context.Localizer;
        var props = typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var dict = new Dictionary<string, SettingDef>();

        foreach (var prop in props)
        {
            var value = prop.GetValue(Config);
            SettingDef? def = prop.Name switch
            {
                nameof(Config.Mode) => SettingBuilder.Choice(prop, loc, [.. Config.Modes],
                    value as string ?? Config.ModeGoogle),
                nameof(Config.Temperature) => SettingBuilder.Number(prop, loc,
                    value is double d ? d : 0.1, 0, 2, 0.05),
                nameof(Config.ChunkThreshold) => SettingBuilder.Number(prop, loc,
                    value is double t ? t : 24, 1, 200, 1),
                nameof(Config.RequestTimeoutSeconds) => SettingBuilder.Number(prop, loc,
                    value is double s ? s : 60, 5, 600, 5),
                nameof(Config.ApiKey) => SettingBuilder.Password(prop, loc, value as string ?? string.Empty),
                nameof(Config.SystemPrompt) => SettingBuilder.Text(prop, loc,
                    value as string ?? Config.DefaultSystemPrompt),
                nameof(Config.ToSimplifiedChinese) => SettingBuilder.Bool(prop, loc, value is true),
                nameof(Config.TestConnection) => SettingBuilder.Action(prop, loc, RunConnectionTest),
                _ => SettingBuilder.Text(prop, loc, value?.ToString() ?? string.Empty)
            };

            if (def != null) dict[prop.Name] = def;
        }

        return dict;
    }

    private void RunConnectionTest(string key)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await _backends!.TestAsync(Config, CancellationToken.None);
                _logger?.Info($"Connection test: {(success ? "SUCCESS" : "FAILURE")} — {message}");
            }
            catch (Exception ex)
            {
                _logger?.Error("Connection test threw", ex);
            }
        });
    }
}
