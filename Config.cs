using BetterLyrics.Sdk.Abstractions.Plugins;
using System.ComponentModel.DataAnnotations;

namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// Settings of the AI Lyrics Translation plugin.
/// Keys are the property names — they map 1:1 to the configuration keys persisted by the host
/// in &lt;pluginDir&gt;/config/config.json.
/// </summary>
public class Config : PluginConfigBase
{
    public const string ModeGoogle = "Google";
    public const string ModeAI = "AI";
    public const string ModeGemini = "Gemini";

    public static readonly string[] Modes = [ModeGoogle, ModeAI, ModeGemini];

    /// <summary>Default prompt ported from BetterLyrics.py (the Taiwan-Traditional one).</summary>
    public const string DefaultSystemPrompt =
        "你是一位專業的歌詞翻譯家。請將以下歌詞逐行翻譯為繁體中文。\n\n" +
        "【翻譯原則】\n" +
        "1. 保留原文情感、語氣和意境\n" +
        "2. 用詞貼切自然，符合中文歌詞的表達習慣\n" +
        "3. 專有名詞、人名、品牌名原則上保留原文不翻譯\n" +
        "4. 押韻或節奏感可適度調整，讓翻譯唱起來也順口\n\n" +
        "【格式規則】\n" +
        "- 行數必須完全一致，空行保持空行\n" +
        "- 每行一對一翻譯，不要合併或拆分任何行\n" +
        "- 只輸出翻譯結果，不加任何說明文字";

    [Display(Name = "Translation Engine",
        Description = "Google = free web endpoint (works without a key). AI = any OpenAI-compatible /chat/completions endpoint. Gemini = Google AI Studio.")]
    public string Mode
    {
        get => Get(ModeGoogle);
        set => Set(value);
    }

    [Display(Name = "API URL",
        Description = "Base URL of the OpenAI-compatible endpoint, or the full DeepL / Gemini / Google gtx endpoint. Leave empty to use the public endpoint of the selected engine.")]
    public string ApiUrl
    {
        get => Get(string.Empty);
        set => Set(value);
    }

    [Display(Name = "API Key",
        Description = "Bearer key for the OpenAI-compatible endpoint, the Gemini key, or the DeepL auth key.")]
    public string ApiKey
    {
        get => Get(string.Empty);
        set => Set(value);
    }

    [Display(Name = "Model Name",
        Description = "Model used for AI / Gemini mode, e.g. gpt-4o-mini, deepseek-chat, gemini-2.0-flash.")]
    public string ModelName
    {
        get => Get("gpt-3.5-turbo");
        set => Set(value);
    }

    [Display(Name = "System Prompt",
        Description = "Instruction sent to the model. Must force a strict one-line-in / one-line-out mapping.")]
    public string SystemPrompt
    {
        get => Get(DefaultSystemPrompt);
        set => Set(value);
    }

    [Display(Name = "Temperature",
        Description = "Sampling temperature for AI mode (0 = deterministic).")]
    public double Temperature
    {
        get => Get(0.1);
        set => Set(value);
    }

    [Display(Name = "Output Simplified Chinese",
        Description = "Convert the translated lyrics from Traditional to Simplified Chinese before returning them.")]
    public bool ToSimplifiedChinese
    {
        get => Get(true);
        set => Set(value);
    }

    [Display(Name = "Max Lines Per Request",
        Description = "Lyrics with more lines than this are split into parallel chunks of 12 lines to avoid timeouts.")]
    public double ChunkThreshold
    {
        get => Get(24d);
        set => Set(value);
    }

    [Display(Name = "Request Timeout (seconds)",
        Description = "How long to wait for one translation request. Slow models that write a whole verse can take 25 s or more; the original Python tool used a fixed 30 s and would silently keep the untranslated lyrics.")]
    public double RequestTimeoutSeconds
    {
        get => Get(60d);
        set => Set(value);
    }

    [Display(Name = "Test Connection",
        Description = "Sends a tiny request to the configured endpoint and reports the result in the log.")]
    public bool TestConnection
    {
        get => Get(false);
        set => Set(value);
    }
}
