using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;

namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// Traditional → Simplified Chinese conversion, mirroring the host's
/// <c>LanguageHelper.ConvertTCToSC</c>. The host only calls the plugin for translation when the
/// target language is Chinese, so the plugin performs the conversion itself (a plugin cannot
/// reference BetterLyrics.Core).
/// </summary>
public static class ChineseConverterHelper
{
    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        try
        {
            return ChineseConverter.Convert(text, ChineseConversionDirection.TraditionalToSimplified);
        }
        catch
        {
            return text;
        }
    }

    public static string ToTraditional(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        try
        {
            return ChineseConverter.Convert(text, ChineseConversionDirection.SimplifiedToTraditional);
        }
        catch
        {
            return text;
        }
    }
}
