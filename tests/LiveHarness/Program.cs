using BetterLyrics.Sdk.Interfaces.Plugins;
using BetterLyrics.TestSupport;
using System.Text;
using System.Text.Json.Nodes;

// End-to-end test against the REAL Ginka endpoint using the shipped plugin assembly.
//
// The API key is never written to disk: it arrives through BL_LIVE_API_KEY so it stays out of the
// repository and out of any file this harness creates.

Console.OutputEncoding = Encoding.UTF8;

var apiKey = Environment.GetEnvironmentVariable("BL_LIVE_API_KEY");
var apiUrl = Environment.GetEnvironmentVariable("BL_LIVE_API_URL") ?? "https://api.ginka.cloud/v1";
var model = Environment.GetEnvironmentVariable("BL_LIVE_MODEL") ?? "glm-5.3-flash";
var pluginAssembly = args.Length > 0
    ? args[0]
    : TestPaths.PluginAssemblyPath;

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("BL_LIVE_API_KEY is not set.");
    return 2;
}

if (!File.Exists(pluginAssembly))
{
    Console.Error.WriteLine($"Plugin assembly not found: {pluginAssembly}");
    return 2;
}

var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == null ? "" : " — " + detail)}");
    if (!ok) failures.Add(name);
}

var pluginDirectory = Path.GetDirectoryName(pluginAssembly)!;
var loadContext = new PluginLoadContext(pluginAssembly);
var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(pluginAssembly));
var pluginType = assembly.GetTypes().FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);

if (pluginType == null)
{
    Console.Error.WriteLine("No IPlugin implementation found in the assembly.");
    return 2;
}

// The host's own default prompt, as shipped in the plugin's Config. BL_LIVE_PROMPT overrides it so
// the harness can also replay the exact prompt a user configured in the original Python tool.
const string defaultSystemPrompt =
    "你是一位专业的歌词翻译家。请将以下歌词逐行翻译为繁體中文。\n\n" +
    "【翻譯原則】\n1. 保留原文情感、語氣和意境\n2. 用詞貼切自然，符合中文歌詞的表達習慣\n" +
    "3. 專有名詞、人名、品牌名原則上保留原文不翻譯\n4. 押韻或節奏感可適度調整，讓翻譯唱起來也順口\n\n" +
    "【格式規則】\n- 行數必須完全一致，空行保持空行\n- 每行一對一翻譯，不要合併或拆分任何行\n" +
    "- 只輸出翻譯結果，不加任何說明文字";
var systemPrompt = Environment.GetEnvironmentVariable("BL_LIVE_PROMPT") is { Length: > 0 } customPrompt
    ? customPrompt.Replace("\\n", "\n")
    : defaultSystemPrompt;

var configurator = new LiveConfigurator();
configurator.Set("Mode", "AI", BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
configurator.Set("ApiUrl", apiUrl, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
configurator.Set("ApiKey", apiKey, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
configurator.Set("ModelName", model, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
configurator.Set("SystemPrompt", systemPrompt, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
configurator.Set("Temperature", 1.0d, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
configurator.Set("ToSimplifiedChinese", true, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
// The real model needs ~25 s per verse; give the harness room so a slow reply is measured as a
// translation rather than being cut off by the plugin's own deadline.
configurator.Set("RequestTimeoutSeconds", 120d, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);

var localizer = new LiveLocalizer();
var context = new LivePluginContext(pluginDirectory, configurator, localizer);

var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
await plugin.InitializeAsync(context);
var translator = (ILyricsTranslator)plugin;

Console.WriteLine($"Endpoint: {apiUrl}");
Console.WriteLine($"Model:    {model}");
Console.WriteLine($"Key:      {Mask(apiKey)} (length {apiKey.Length})");
Console.WriteLine();

// ------------------------------------------------------------------ 1. English lyrics

Console.WriteLine("== English lyrics -> Simplified Chinese ==");
var english = """
    Hello darkness my old friend
    I've come to talk with you again
    Because a vision softly creeping
    Left its seeds while I was sleeping
    And the vision that was planted in my brain
    Still remains
    Within the sound of silence
    """;

var englishResult = await TranslateAsync(translator, english);
var englishOriginal = english.Split('\n');
var englishLines = englishResult.Split('\n');
Check("line count preserved", englishLines.Length == englishOriginal.Length,
    $"{englishOriginal.Length} in / {englishLines.Length} out");
Check("output is not the input (translation happened)", englishResult != english,
    Truncate(englishResult.Replace('\n', '|'), 90));
Check("output contains CJK", ContainsCjk(englishResult),
    CountCjk(englishResult) + " CJK chars");
Check("translation is in Chinese, not English",
    !englishLines.All(l => l.Length == 0 || IsAscii(l)),
    Truncate(englishResult.Replace('\n', '|'), 90));

// ------------------------------------------------------------------ 2. Traditional -> Simplified

Console.WriteLine();
Console.WriteLine("== Traditional Chinese input must come out Simplified ==");
var traditional = """
    我們都已經長大
    學會了不再說話
    時間它帶走一切
    留下了什麼
    """;

var traditionalResult = await TranslateAsync(translator, traditional);
var tradLines = traditionalResult.Split('\n');
Check("line count preserved", tradLines.Length == traditional.Split('\n').Length,
    tradLines.Length.ToString());
Check("no Traditional-only characters remain",
    !tradLines.Any(ContainsTraditionalOnly), Truncate(traditionalResult.Replace('\n', '|'), 90));
Check("specific conversion 說話 -> 说话 (or reworded to Simplified)",
    !traditionalResult.Contains('說') && !traditionalResult.Contains('們'),
    Truncate(traditionalResult.Replace('\n', '|'), 90));

// ------------------------------------------------------------------ 3. Long lyrics -> chunking

Console.WriteLine();
Console.WriteLine("== 36-line lyrics exercise the chunking path (12 lines x 3) ==");
var longLyrics = string.Join('\n', Enumerable.Range(1, 36).Select(i => $"Line number {i} of the test song"));
var longResult = await TranslateAsync(translator, longLyrics);
Check("all 36 lines survive", longResult.Split('\n').Length == 36,
    longResult.Split('\n').Length.ToString());
Check("chunked output is Chinese", ContainsCjk(longResult), CountCjk(longResult) + " CJK chars");

// ------------------------------------------------------------------ 4. Blank lines stay blank

Console.WriteLine();
Console.WriteLine("== blank lines are preserved ==");
var withBlanks = "First line\n\nThird line\n\nFifth line";
var blankResult = await TranslateAsync(translator, withBlanks);
var blankLines = blankResult.Split('\n');
Check("line count preserved with blanks", blankLines.Length == 5, blankLines.Length.ToString());
Check("blank lines stayed blank", blankLines[1].Length == 0 && blankLines[3].Length == 0,
    string.Join("|", blankLines.Select(l => l.Length == 0 ? "<blank>" : l)));

// ------------------------------------------------------------------ 5. Special characters

Console.WriteLine();
Console.WriteLine("== punctuation / apostrophes / non-ASCII survive ==");
var tricky = "Don't stop — believin'\nIt's 100% \"quoted\" & <tagged>\nEmoji 🎵 test";
var trickyResult = await TranslateAsync(translator, tricky);
Check("line count preserved", trickyResult.Split('\n').Length == 3,
    trickyResult.Split('\n').Length.ToString());
Check("no JSON escape debris (\\u or \\n literals)",
    !trickyResult.Contains("\\u") && !trickyResult.Contains("\\n"),
    Truncate(trickyResult.Replace('\n', '|'), 90));

// ------------------------------------------------------------------ 6. Empty input

Console.WriteLine();
Console.WriteLine("== edge cases ==");
Check("empty input returns empty", await TranslateAsync(translator, "") == "");

// ------------------------------------------------------------------ 7. Bad key must not throw

Console.WriteLine();
Console.WriteLine("== an invalid API key must not throw (falls back gracefully) ==");
var badKeyConfigurator = new LiveConfigurator();
badKeyConfigurator.Set("Mode", "AI", BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
badKeyConfigurator.Set("ApiUrl", apiUrl, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
badKeyConfigurator.Set("ApiKey", "sk-invalid-key-for-test", BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
badKeyConfigurator.Set("ModelName", model, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
badKeyConfigurator.Set("SystemPrompt", systemPrompt, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);
badKeyConfigurator.Set("Temperature", 1.0d, BetterLyrics.Sdk.Enums.ConfigChangedBy.Host);

var badPlugin = (IPlugin)Activator.CreateInstance(pluginType)!;
await badPlugin.InitializeAsync(new LivePluginContext(pluginDirectory, badKeyConfigurator, localizer));

var badThrew = false;
string badResult = string.Empty;
try
{
    badResult = await ((ILyricsTranslator)badPlugin).GetTranslationAsync("Hello world", null) ?? "";
}
catch (Exception ex)
{
    badThrew = true;
    Console.WriteLine("      threw: " + ex.GetType().Name + ": " + ex.Message);
}

Check("invalid key does not throw", !badThrew);
Check("invalid key returns the original text unchanged", badResult == "Hello world",
    Truncate(badResult, 60));
await badPlugin.DisposeAsync();

// ------------------------------------------------------------------ 8. Connection test

Console.WriteLine();
Console.WriteLine("== the Test Connection action against the real endpoint ==");
var backendsType = assembly.GetTypes().FirstOrDefault(t => t.Name == "TranslationBackends");
if (backendsType == null)
{
    Check("TranslationBackends type found", false);
}
else
{
    var configType = pluginType.BaseType!.GetGenericArguments()[0];
    var realConfig = Activator.CreateInstance(configType)!;
    var bind = configType.GetMethod("BindConfigurator", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    var bindBase = typeof(BetterLyrics.Sdk.Abstractions.Plugins.PluginConfigBase)
        .GetMethod("BindConfigurator")!;
    bindBase.Invoke(realConfig, [configurator]);

    var loggerType = assembly.GetTypes().First(t => t.Name == "PluginLogger");
    var loggerInstance = Activator.CreateInstance(loggerType, Path.Combine(pluginDirectory, "logs"))!;
    // Same as the plugin itself: the per-request deadline comes from the config, so the client
    // must not impose a second, shorter one that would fire first and mask the real limit.
    var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    var backends = Activator.CreateInstance(backendsType, http, loggerInstance)!;
    var testAsync = backendsType.GetMethod("TestAsync")!;
    var task = (Task<(bool Success, string Message)>)testAsync.Invoke(backends,
        [realConfig, CancellationToken.None])!;
    var (success, message) = await task;

    Check("connection test reports success", success, message);
    // "AI OK:" itself contains OK, so require the reply after the prefix to carry it too.
    var reply = message.StartsWith("AI OK:", StringComparison.Ordinal) ? message["AI OK:".Length..].Trim() : message;
    Check("connection test actually got a model reply", reply.Contains("OK", StringComparison.Ordinal), Truncate(message, 80));
}

// ------------------------------------------------------------------ 9. Log content

Console.WriteLine();
Console.WriteLine("== plugin log ==");
var logPath = Path.Combine(pluginDirectory, "logs", "plugin.log");
Check("log file written", File.Exists(logPath), logPath);
if (File.Exists(logPath))
{
    var log = File.ReadAllText(logPath);
    Check("log records chat requests", log.Contains("Chat request"), "");
    Check("log does NOT contain the API key", !log.Contains(apiKey), "");
}

await plugin.DisposeAsync();

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? "ALL LIVE CHECKS PASSED"
    : $"{failures.Count} LIVE CHECK(S) FAILED:\n  - " + string.Join("\n  - ", failures));
return failures.Count == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers

static async Task<string> TranslateAsync(ILyricsTranslator translator, string text)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await translator.GetTranslationAsync(text, null) ?? "";
    sw.Stop();
    Console.WriteLine($"      [{sw.ElapsedMilliseconds} ms]");
    return result;
}

static string Mask(string key) =>
    key.Length <= 8 ? "****" : key[..4] + new string('*', key.Length - 8) + key[^4..];

static string Truncate(string value, int max) =>
    value.Length <= max ? value : value[..max] + "…";

static bool ContainsCjk(string text) => text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

static int CountCjk(string text) => text.Count(c => c >= 0x4E00 && c <= 0x9FFF);

static bool IsAscii(string text) => text.All(c => c <= 0x7F);

// Characters that exist in Traditional but not in Simplified (a small but telling sample).
static bool ContainsTraditionalOnly(string line) =>
    line.Any(c => "說話們學會經帶過開關點這樣麼為與時間發現問題題".Contains(c));

internal sealed class LiveConfigurator : IConfigurator
{
    private readonly Dictionary<string, object?> _config = new(StringComparer.Ordinal);

    public event EventHandler<string, BetterLyrics.Sdk.Enums.ConfigChangedBy>? OnConfigChanged;

    public object Get(string key, object defaultValue) =>
        _config.TryGetValue(key, out var value) && value != null ? value : defaultValue;

    public void Set(string key, object value, BetterLyrics.Sdk.Enums.ConfigChangedBy configChangedBy)
    {
        _config[key] = value;
        OnConfigChanged?.Invoke(key, configChangedBy);
    }
}

internal sealed class LiveLocalizer : ILocalizer
{
    public string CurrentLanguage => "zh-Hans";

    public string this[string key] => GetString(key);

    public string GetString(string key) => key;
}

internal sealed class LivePluginContext(string pluginDirectory, IConfigurator configurator, ILocalizer localizer)
    : IPluginContext
{
    public string PluginDirectory { get; } = pluginDirectory;

    public IAIService? AIService => null;

    public ILocalizer Localizer { get; } = localizer;

    public IConfigurator Configurator { get; } = configurator;
}

internal sealed class PluginLoadContext : System.Runtime.Loader.AssemblyLoadContext
{
    private readonly System.Runtime.Loader.AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true) =>
        _resolver = new System.Runtime.Loader.AssemblyDependencyResolver(pluginPath);

    protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName)
    {
        if (Default.Assemblies.Any(a => a.GetName().Name == assemblyName.Name)) return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path != null && File.Exists(path)) return LoadFromAssemblyPath(path);

        return null;
    }
}
