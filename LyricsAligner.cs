using System.Text;

namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// Port of <c>translate_server.align_lyrics</c> from BetterLyrics.py.
/// Forces the translated text to have exactly the same number of lines as the original:
/// extra lines are truncated, missing lines are padded with empty strings, and lines that
/// are blank in the original stay blank in the translation.
/// </summary>
public static class LyricsAligner
{
    public static string Align(string originalText, string translatedText)
    {
        if (string.IsNullOrEmpty(originalText)) return string.Empty;

        var originalLines = originalText.Split('\n');
        var translatedLines = translatedText.Split('\n');

        if (translatedLines.Length > originalLines.Length)
        {
            translatedLines = translatedLines[..originalLines.Length];
        }
        else if (translatedLines.Length < originalLines.Length)
        {
            var padding = originalLines.Length - translatedLines.Length;
            Array.Resize(ref translatedLines, originalLines.Length);
            for (var i = translatedLines.Length - padding; i < translatedLines.Length; i++)
                translatedLines[i] = string.Empty;
        }

        var builder = new StringBuilder(translatedText.Length + originalLines.Length);
        for (var i = 0; i < originalLines.Length; i++)
        {
            if (i > 0) builder.Append('\n');

            var original = originalLines[i];
            if (string.IsNullOrWhiteSpace(original))
            {
                builder.Append(string.Empty);
                continue;
            }

            builder.Append(i < translatedLines.Length ? translatedLines[i].Trim() : string.Empty);
        }

        return builder.ToString();
    }
}
