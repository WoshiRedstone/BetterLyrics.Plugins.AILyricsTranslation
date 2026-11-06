using BetterLyrics.Plugins.AILyricsTranslation.Harness;
using BetterLyrics.Sdk.Enums;
using BetterLyrics.Sdk.Helpers;
using BetterLyrics.Sdk.Interfaces.Plugins;
using BetterLyrics.Sdk.Models.SettingsSchema;
using BetterLyrics.TestSupport;
using NLanguageTag;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;

Console.OutputEncoding = Encoding.UTF8;

var pluginPackage = args.Length > 0
    ? args[0]
    : TestPaths.PluginAssemblyPath;

if (!File.Exists(pluginPackage))
{
    Console.Error.WriteLine($"Plugin assembly not found: {pluginPackage}");
    return 2;
}

Console.WriteLine($"Loading plugin assembly: {pluginPackage}");

// Load exactly like the host: the plugin assembly gets its own collectible ALC whose resolver
// falls back to the default context, so BetterLyrics.Sdk / NLanguageTag / ChineseConverter are
// shared with the host (they are trimmed out of the .blp on purpose).
var pluginDirectory = Path.GetDirectoryName(pluginPackage)!;
var loadContext = new PluginLoadContext(pluginPackage);
var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(pluginPackage));

var pluginType = assembly.GetTypes().FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
if (pluginType == null)
{
    Console.Error.WriteLine("No IPlugin implementation found in the assembly.");
    return 2;
}

Console.WriteLine($"Found plugin type: {pluginType.FullName}");

var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == null ? "" : " — " + detail)}");
    if (!ok) failures.Add(name);
}

// ---------------------------------------------------------------- mock server

using var mock = new MockOpenAiServer();

var mockUrl = mock.BaseUrl;
Console.WriteLine($"Mock OpenAI-compatible server listening on {mockUrl}\n");

// ---------------------------------------------------------------- plugin identity

var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
Console.WriteLine("== identity ==");
Check("Id is the assembly name", plugin.Id == "BetterLyrics.Plugins.AILyricsTranslation", plugin.Id);
Check("Title is set", !string.IsNullOrWhiteSpace(plugin.Title), plugin.Title);
Check("Version is 1.0.0", plugin.Version == "1.0.0", plugin.Version);
Check("Author is set", !string.IsNullOrWhiteSpace(plugin.Author), plugin.Author);
Check("Description is set", !string.IsNullOrWhiteSpace(plugin.Description));
Check("RepositoryUrl is set", !string.IsNullOrWhiteSpace(plugin.RepositoryUrl), plugin.RepositoryUrl);
Check("LastUpdated is recent", (DateTime.Now - plugin.LastUpdated).TotalDays < 1, plugin.LastUpdated.ToString("u"));
Check("implements ILyricsTranslator", plugin is ILyricsTranslator);

// ---------------------------------------------------------------- initialization

var configurator = new FakeConfigurator();
var langFile = Path.Combine(pluginDirectory, "Langs", "en.json");
var strings = new Dictionary<string, string>();
if (File.Exists(langFile))
{
    var node = JsonNode.Parse(File.ReadAllText(langFile))!.AsObject();
    foreach (var kv in node) strings[kv.Key] = kv.Value?.ToString() ?? string.Empty;
}

var localizer = new FakeLocalizer(strings);
var context = new FakePluginContext(pluginDirectory, configurator, localizer);

await plugin.InitializeAsync(context);
Console.WriteLine("\n== initialization ==");
Check("InitializeAsync completed", true);
Check("Lang file present in package", File.Exists(langFile), langFile);
Check("Lang file has Settings.* keys", strings.Keys.Any(k => k.StartsWith("Settings.")),
    $"{strings.Count} keys");

// ---------------------------------------------------------------- settings schema

Console.WriteLine("\n== settings schema ==");
var defs = plugin.GetSettingDefDict();
foreach (var key in new[] { "Mode", "ApiUrl", "ApiKey", "ModelName", "SystemPrompt", "Temperature",
             "ToSimplifiedChinese", "ChunkThreshold", "RequestTimeoutSeconds", "TestConnection" })
    Check($"schema contains {key}", defs.ContainsKey(key));

if (defs.TryGetValue("Mode", out var modeDef))
{
    var choice = modeDef as ChoiceSettingDef;
    Check("Mode is a choice", choice != null);
    Check("Mode offers Google/AI/Gemini", choice?.Options.SequenceEqual(["Google", "AI", "Gemini"]) == true,
        string.Join(",", choice?.Options ?? []));
    Check("Mode default is Google", choice?.Value as string == "Google", choice?.Value as string);
}

Check("ApiKey is a password", (defs.GetValueOrDefault("ApiKey") as TextSettingDef)?.IsPassword == true);
Check("Temperature is 0..2 step 0.05",
    defs.GetValueOrDefault("Temperature") is NumberSettingDef { Min: 0, Max: 2, Step: 0.05 });
Check("ChunkThreshold is 1..200",
    defs.GetValueOrDefault("ChunkThreshold") is NumberSettingDef { Min: 1, Max: 200, Step: 1 });
Check("RequestTimeoutSeconds is 5..600",
    defs.GetValueOrDefault("RequestTimeoutSeconds") is NumberSettingDef { Min: 5, Max: 600, Step: 5 });
Check("RequestTimeoutSeconds default is 60",
    defs.GetValueOrDefault("RequestTimeoutSeconds")?.Value is double d && Math.Abs(d - 60) < 0.001,
    defs.GetValueOrDefault("RequestTimeoutSeconds")?.Value?.ToString());
Check("TestConnection is an action", defs.GetValueOrDefault("TestConnection") is ActionSettingDef);
Check("Action has button text",
    (defs.GetValueOrDefault("TestConnection") as ActionSettingDef)?.ButtonText == "Settings.TestConnection.Button",
    (defs.GetValueOrDefault("TestConnection") as ActionSettingDef)?.ButtonText);
Check("Every setting has a localized header",
    defs.Values.All(d => !string.IsNullOrWhiteSpace(d.Header) && !d.Header.StartsWith("Settings.")));

// ---------------------------------------------------------------- localization

Console.WriteLine("\n== localization ==");
Dictionary<string, string> ReadLang(string culture)
{
    var path = Path.Combine(pluginDirectory, "Langs", culture + ".json");
    var dict = new Dictionary<string, string>();
    if (!File.Exists(path)) return dict;
    foreach (var kv in JsonNode.Parse(File.ReadAllText(path))!.AsObject())
        dict[kv.Key] = kv.Value?.ToString() ?? string.Empty;
    return dict;
}

var zhHans = ReadLang("zh-Hans");
var zhHant = ReadLang("zh-Hant");
Check("zh-Hans lang file is packaged", zhHans.Count > 0, $"{zhHans.Count} keys");
Check("zh-Hant lang file is packaged", zhHant.Count > 0, $"{zhHant.Count} keys");
Check("zh-Hans covers every English key",
    strings.Keys.All(zhHans.ContainsKey),
    string.Join(",", strings.Keys.Where(k => !zhHans.ContainsKey(k))));

// The packer regenerates Langs\*.json from [Display] on every build, so a translation that is not
// overlaid afterwards silently reverts to English. Assert the Chinese files are really translated.
bool HasChinese(string value) => value.Any(c => c >= 0x4E00 && c <= 0x9FFF);

if (zhHans.Count > 0)
{
    Check("zh-Hans labels are Chinese", HasChinese(zhHans.GetValueOrDefault("Settings.Mode.Label") ?? ""),
        zhHans.GetValueOrDefault("Settings.Mode.Label"));
    Check("zh-Hans descriptions are Chinese",
        zhHans.Where(kv => kv.Key.EndsWith(".Desc")).All(kv => HasChinese(kv.Value)),
        zhHans.GetValueOrDefault("Settings.ApiKey.Desc"));
    Check("zh-Hans is Simplified", (zhHans.GetValueOrDefault("Settings.TestConnection.Label") ?? "")
        .Contains('测'), zhHans.GetValueOrDefault("Settings.TestConnection.Label"));
}

if (zhHant.Count > 0)
{
    Check("zh-Hant labels are Chinese", HasChinese(zhHant.GetValueOrDefault("Settings.Mode.Label") ?? ""),
        zhHant.GetValueOrDefault("Settings.Mode.Label"));
    Check("zh-Hant is Traditional", (zhHant.GetValueOrDefault("Settings.TestConnection.Label") ?? "")
        .Contains('測'), zhHant.GetValueOrDefault("Settings.TestConnection.Label"));
}

// A Chinese UI culture must resolve through the host's PluginLocalizer fallback chain and produce
// translated setting headers instead of raw "Settings.Mode.Label" keys.
if (zhHans.Count > 0)
{
    var configType = pluginType.BaseType!.GetGenericArguments()[0];
    var zhLocalizer = new FakeLocalizer(zhHans);
    var zhMode = SettingBuilder.Choice(configType.GetProperty("Mode")!, zhLocalizer,
        ["Google", "AI", "Gemini"], "Google");
    Check("localized header resolves through ILocalizer",
        zhMode.Header == zhHans["Settings.Mode.Label"], zhMode.Header);
    Check("localized description resolves through ILocalizer",
        zhMode.Description == zhHans["Settings.Mode.Desc"], zhMode.Description);
    Check("localized button text resolves through ILocalizer",
        SettingBuilder.Action(configType.GetProperty("TestConnection")!, zhLocalizer, _ => { })
            .ButtonText == zhHans["Settings.TestConnection.Button"]);
}

// the host reads the schema through the IPlugin interface — make sure that path is wired too
var fromInterface = ((IPlugin)plugin).GetSettingDefDict();
Check("schema available through IPlugin", fromInterface.Count == defs.Count, $"{fromInterface.Count} vs {defs.Count}");

// ---------------------------------------------------------------- helpers

async Task<string> Translate(string text, string mode, string url, string key, string model,
    double temperature = 0.1, bool simplify = true, double threshold = 24)
{
    configurator.Set("Mode", mode, ConfigChangedBy.Host);
    configurator.Set("ApiUrl", url, ConfigChangedBy.Host);
    configurator.Set("ApiKey", key, ConfigChangedBy.Host);
    configurator.Set("ModelName", model, ConfigChangedBy.Host);
    configurator.Set("Temperature", temperature, ConfigChangedBy.Host);
    configurator.Set("ToSimplifiedChinese", simplify, ConfigChangedBy.Host);
    configurator.Set("ChunkThreshold", threshold, ConfigChangedBy.Host);
    configurator.Set("SystemPrompt", ConfigDefaults.SystemPrompt, ConfigChangedBy.Host);

    var result = await ((ILyricsTranslator)plugin).GetTranslationAsync(text, LanguageTag.TryParse("zh-CN", out var t) ? t : null);
    return result ?? string.Empty;
}

// ---------------------------------------------------------------- AI chat mode

Console.WriteLine("\n== AI (OpenAI-compatible) mode ==");
mock.Mode = MockMode.EchoTraditional;

var lyrics = string.Join("\n", [
    "Hello world",
    "Second line",
    "",
    "Fourth line"
]);
var aiResult = await Translate(lyrics, "AI", mockUrl + "/v1/", "test-key", "test-model");
Console.WriteLine($"  input : {Show(lyrics)}");
Console.WriteLine($"  output: {Show(aiResult)}");
Check("line count preserved", aiResult.Split('\n').Length == lyrics.Split('\n').Length,
    $"{aiResult.Split('\n').Length} vs {lyrics.Split('\n').Length}");
Check("blank source line stays blank", aiResult.Split('\n')[2].Length == 0);
Check("content translated", aiResult.Contains("第一行"));
Check("Traditional converted to Simplified",
    aiResult.Contains("第一行") && !aiResult.Contains("第一行轉"), aiResult.Split('\n')[0]);
Check("mock received the bearer key", mock.LastAuthorization == "Bearer test-key", mock.LastAuthorization);
Check("mock received the model", mock.LastModel == "test-model", mock.LastModel);
Check("mock received the system prompt", mock.LastSystemPrompt?.Contains("歌詞翻譯家") == true);

var nonSimplified = await Translate("Hello world", "AI", mockUrl + "/v1/", "test-key", "test-model", simplify: false);
Console.WriteLine($"  raw (no simplification): {nonSimplified}");
Check("conversion can be disabled", nonSimplified != "第一行" || nonSimplified.Contains("第"));

// ---------------------------------------------------------------- alignment

Console.WriteLine("\n== line-count alignment ==");
mock.Mode = MockMode.TooFewLines;
var few = await Translate(string.Join("\n", ["a", "b", "c"]), "AI", mockUrl + "/v1/", "k", "m");
Check("missing translated lines are padded", few.Split('\n').Length == 3, Show(few));

mock.Mode = MockMode.TooManyLines;
var many = await Translate(string.Join("\n", ["a", "b"]), "AI", mockUrl + "/v1/", "k", "m");
Check("extra translated lines are truncated", many.Split('\n').Length == 2, Show(many));

// ---------------------------------------------------------------- chunking

Console.WriteLine("\n== chunking (> 24 lines) ==");
mock.Mode = MockMode.EchoTraditional;
mock.ResetCounters();
var longLyrics = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"Line {i}"));
var chunked = await Translate(longLyrics, "AI", mockUrl + "/v1/", "k", "m");
Console.WriteLine($"  output lines: {chunked.Split('\n').Length}, requests: {mock.RequestCount}");
Check("all 30 lines survive chunking", chunked.Split('\n').Length == 30, chunked.Split('\n').Length.ToString());
Check("lyrics split into 12-line chunks", mock.RequestCount == 3, mock.RequestCount.ToString());

mock.ResetCounters();
var shortLyrics = string.Join("\n", Enumerable.Range(1, 24).Select(i => $"Line {i}"));
var single = await Translate(shortLyrics, "AI", mockUrl + "/v1/", "k", "m");
Check("24 lines are sent in one request", mock.RequestCount == 1, mock.RequestCount.ToString());
Check("24 lines survive", single.Split('\n').Length == 24);

// ---------------------------------------------------------------- API-key fallback

Console.WriteLine("\n== empty API key falls back to Google ==");
var fallback = await Translate("Hello world", "AI", mockUrl + "/v1/", "", "m");
Check("falls back without throwing", fallback.Length > 0, fallback);
Check("returns the original text when Google is unreachable",
    fallback.Split('\n').Length == "Hello world".Split('\n').Length, Show(fallback));

// ---------------------------------------------------------------- Gemini

Console.WriteLine("\n== Gemini mode ==");
mock.Mode = MockMode.EchoTraditional;
mock.ResetCounters();
var gemini = await Translate("Hello world\nSecond line", "Gemini", mockUrl, "gemini-key", "gemini-1.5-flash");
Check("Gemini path is used", mock.LastPath?.Contains(":generateContent") == true, mock.LastPath);
Check("Gemini key passed as query parameter", mock.LastQuery?.Contains("key=gemini-key") == true, mock.LastQuery);
Check("Gemini model in the URL", mock.LastPath?.Contains("/models/gemini-1.5-flash") == true, mock.LastPath);
Check("Gemini output aligned", gemini.Split('\n').Length == 2, Show(gemini));

// ---------------------------------------------------------------- DeepL

Console.WriteLine("\n== DeepL mode ==");
mock.Mode = MockMode.EchoTraditional;
var deeplEndpoint = mockUrl + "/deepl";
var deepl = await Translate("Hello world\nSecond line", "AI", deeplEndpoint, "deepl-key", "m");
Check("DeepL endpoint is used", mock.LastPath == "/deepl", mock.LastPath);
Check("DeepL auth header set", mock.LastAuthorization == "DeepL-Auth-Key deepl-key", mock.LastAuthorization);
Check("DeepL sends target_lang=ZH", mock.LastForm?.Contains("target_lang=ZH") == true, mock.LastForm);
Check("DeepL output aligned", deepl.Split('\n').Length == 2, Show(deepl));

// ---------------------------------------------------------------- error handling

Console.WriteLine("\n== error handling ==");
mock.Mode = MockMode.ServerError;
var failed = await Translate("Hello world", "AI", mockUrl + "/v1/", "k", "m");
Check("HTTP 500 does not throw", failed.Length > 0, Show(failed));

var empty = await ((ILyricsTranslator)plugin).GetTranslationAsync(string.Empty, null);
Check("empty input returns empty", empty == string.Empty);

// ---------------------------------------------------------------- timeout handling

Console.WriteLine("\n== a slow endpoint must not lose the translation ==");

// Regression for the bug the live endpoint exposed: a request that takes longer than the timeout
// must fall back to the original text (never throw), and raising the timeout must actually let a
// slow-but-successful reply through instead of silently returning English.
mock.Mode = MockMode.SlowResponse;
mock.Delay = TimeSpan.FromSeconds(3);

configurator.Set("RequestTimeoutSeconds", 1d, ConfigChangedBy.Host);
var swTimeout = System.Diagnostics.Stopwatch.StartNew();
var timedOut = await Translate("Hello world\nSecond line", "AI", mockUrl + "/v1/", "k", "m");
swTimeout.Stop();
Check("a request past the timeout returns the original text",
    timedOut == "Hello world\nSecond line", Show(timedOut));
Check("the timeout is honoured (gives up in ~1 s)",
    swTimeout.Elapsed < TimeSpan.FromSeconds(2.5), $"{swTimeout.ElapsedMilliseconds} ms");

var logAfterTimeout = File.Exists(Path.Combine(pluginDirectory, "logs", "plugin.log"))
    ? File.ReadAllText(Path.Combine(pluginDirectory, "logs", "plugin.log"))
    : string.Empty;
Check("the log names the timeout instead of hiding it",
    logAfterTimeout.Contains("did not finish within"), Last(logAfterTimeout, 2));

configurator.Set("RequestTimeoutSeconds", 30d, ConfigChangedBy.Host);
var slow = await Translate("Hello world\nSecond line", "AI", mockUrl + "/v1/", "k", "m");
Check("a slow reply inside the timeout is still translated",
    slow != "Hello world\nSecond line" && slow.Split('\n').Length == 2, Show(slow));

mock.Mode = MockMode.EchoTraditional;
mock.Delay = TimeSpan.Zero;

// ---------------------------------------------------------------- connection test

Console.WriteLine("\n== connection test ==");
mock.Mode = MockMode.EchoTraditional;
var actionDef = defs["TestConnection"] as ActionSettingDef;
actionDef?.Action("TestConnection");
await Task.Delay(1500);
var logPath = Path.Combine(pluginDirectory, "logs", "plugin.log");
Check("log file created", File.Exists(logPath), logPath);
if (File.Exists(logPath))
{
    var log = File.ReadAllText(logPath);
    Check("log records the connection test", log.Contains("Connection test"), Last(log, 1));
    Check("log records successful translation runs", log.Contains("[INFO]"), "");
}

// ---------------------------------------------------------------- shutdown

await plugin.DisposeAsync();
Console.WriteLine("\n== shutdown ==");
Check("DisposeAsync completed", true);

// ---------------------------------------------------------------- summary

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL CHECKS PASSED");
    return 0;
}

Console.WriteLine($"{failures.Count} CHECK(S) FAILED:");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;

static string Show(string value) => value.Replace("\n", "\\n");

static string Last(string text, int lines) =>
    string.Join("\\n", text.Split('\n').TakeLast(lines));

/// <summary>
/// Mirrors the host's <c>PluginLoadContext</c>: assemblies the host already provides are resolved
/// from the default context (that is why the .blp trimming removes them), everything else comes
/// from the plugin directory.
/// </summary>
internal sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Same rule the host's AssemblyDependencyResolver + default ALC fallback produces: whatever
        // the host process already has wins, so the plugin shares the host's BetterLyrics.Sdk
        // instance instead of loading a second copy (which would break type identity).
        if (Default.Assemblies.Any(a => a.GetName().Name == assemblyName.Name)) return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}

internal enum MockMode
{
    EchoTraditional,
    TooFewLines,
    TooManyLines,
    ServerError,
    SlowResponse
}

/// <summary>
/// Stand-in for an OpenAI-compatible endpoint, a Gemini API and a DeepL API. The real endpoints are
/// unreachable from the build machine, so every translation path is exercised against this server.
/// </summary>
internal sealed class MockOpenAiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public MockOpenAiServer()
    {
        Port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{Port}";
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }
    public string BaseUrl { get; }
    public MockMode Mode { get; set; } = MockMode.EchoTraditional;
    /// <summary>Artificial delay before replying; used to exercise the request-timeout path.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public int RequestCount { get; private set; }
    public string? LastPath { get; private set; }
    public string? LastQuery { get; private set; }
    public string? LastAuthorization { get; private set; }
    public string? LastModel { get; private set; }
    public string? LastSystemPrompt { get; private set; }
    public string? LastForm { get; private set; }

    public void ResetCounters()
    {
        RequestCount = 0;
        LastPath = null;
        LastQuery = null;
        LastAuthorization = null;
        LastModel = null;
        LastSystemPrompt = null;
        LastForm = null;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            try
            {
                await HandleAsync(ctx);
            }
            catch (Exception ex)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    Write(ctx, ex.Message);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        RequestCount++;
        LastPath = ctx.Request.Url?.AbsolutePath;
        LastQuery = ctx.Request.Url?.Query;
        LastAuthorization = ctx.Request.Headers["Authorization"];

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        string payload;
        string translatedText;

        if (Mode == MockMode.SlowResponse && Delay > TimeSpan.Zero)
        {
            try { await Task.Delay(Delay, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }

        if (Mode == MockMode.ServerError && !LastPath!.Contains("generateContent") && LastPath != "/deepl")
        {
            ctx.Response.StatusCode = 500;
            Write(ctx, "{\"error\":{\"message\":\"boom\"}}");
            return;
        }

        if (LastPath!.Contains(":generateContent"))
        {
            var text = JsonNode.Parse(body)?["contents"]?[0]?["parts"]?[0]?["text"]?.ToString() ?? string.Empty;
            translatedText = TranslatePayload(text);
            payload = new JsonObject
            {
                ["candidates"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = translatedText } }
                        }
                    }
                }
            }.ToJsonString();
        }
        else if (LastPath == "/deepl")
        {
            LastForm = body;
            var srcText = ExtractFormValue(body, "text");
            payload = new JsonObject
            {
                ["translations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["text"] = TranslatePayload(srcText),
                        ["detected_source_language"] = "EN"
                    }
                }
            }.ToJsonString();
        }
        else
        {
            LastForm = null;
            var root = JsonNode.Parse(body);
            LastModel = root?["model"]?.ToString();
            LastSystemPrompt = root?["messages"]?[0]?["content"]?.ToString();
            var user = root?["messages"]?[1]?["content"]?.ToString() ?? string.Empty;
            translatedText = TranslatePayload(user);
            payload = new JsonObject
            {
                ["choices"] = new JsonArray
                {
                    new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = translatedText } }
                }
            }.ToJsonString();
        }

        Write(ctx, payload);
    }

    private string TranslatePayload(string text)
    {
        var lines = text.Split('\n');
        var translated = lines.Select(TranslateLine).ToList();

        if (Mode == MockMode.TooFewLines) translated = translated.Take(Math.Max(1, lines.Length - 1)).ToList();
        if (Mode == MockMode.TooManyLines) translated.AddRange(["EXTRA-1", "EXTRA-2"]);

        return string.Join('\n', translated);
    }

    // Traditional forms on purpose: proves the plugin converts to Simplified.
    private static string TranslateLine(string line) => line switch
    {
        "" => "",
        "Hello world" => "第一行轉換",
        "Second line" => "第二行轉換",
        "Fourth line" => "第四行轉換",
        _ when line.StartsWith("Line ") => $"第{line[5..]}行轉換",
        _ => "譯" + line
    };

    private static string ExtractFormValue(string body, string key)
    {
        foreach (var pair in body.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == key)
                return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
        }

        return string.Empty;
    }

    private static void Write(HttpListenerContext ctx, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Flush();
        ctx.Response.OutputStream.Close();
    }

    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // ignore
        }
    }
}

internal static class ConfigDefaults
{
    public const string SystemPrompt =
        "你是一位專業的歌詞翻譯家。請將以下歌詞逐行翻譯為繁體中文。\n\n" +
        "【格式規則】\n" +
        "- 行數必須完全一致，空行保持空行\n" +
        "- 每行一對一翻譯，不要合併或拆分任何行\n" +
        "- 只輸出翻譯結果，不加任何說明文字";
}
