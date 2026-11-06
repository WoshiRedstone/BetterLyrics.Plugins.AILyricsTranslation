using BetterLyrics.Sdk.Models.Lyrics;
using NLanguageTag;

namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// Orchestrates the translation pipeline ported from BetterLyrics.py:
/// <list type="bullet">
/// <item>mode dispatch (Google / AI / Gemini / DeepL) with 1 cache keyed by (mode, url, model, text)</item>
/// <item>chunking: ≤ 24 lines are sent in one request, larger lyrics are split into 12-line chunks
///       translated in parallel (max 10 concurrent) with per-chunk alignment</item>
/// <item>strict line-count alignment against the original lyrics</item>
/// <item>optional Traditional → Simplified Chinese conversion</item>
/// </list>
/// </summary>
internal sealed class TranslationEngine(TranslationBackends backends, PluginLogger logger)
{
    private const int ChunkSize = 12;
    private const int MaxParallelChunks = 10;
    private const int CacheCapacity = 256;

    private readonly TranslationBackends _backends = backends;
    private readonly PluginLogger _logger = logger;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public async Task<string> TranslateAsync(string text, Config config, LanguageTag? targetLangTag,
        CancellationToken token)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var target = ResolveTargetLanguage(targetLangTag);
        var cacheKey = string.Join('\u0001', config.Mode, config.ApiUrl, config.ModelName, text);

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                _logger.Info("Cache hit");
                return Finish(text, cached, config);
            }
        }

        string translated;
        try
        {
            translated = await TranslateRawAsync(text, config, target, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The caller cancelled: let it unwind.
            throw;
        }
        catch (Exception ex)
        {
            // Timeouts surface as TaskCanceledException, which is also an OperationCanceledException.
            // The ported behaviour is to never fail the host: fall back to the original text.
            _logger.Error("Translation failed, returning the original text", ex);
            return text;
        }

        if (string.IsNullOrEmpty(translated)) return text;

        lock (_cacheGate)
        {
            if (_cache.Count >= CacheCapacity) _cache.Clear();
            _cache[cacheKey] = translated;
        }

        return Finish(text, translated, config);
    }

    private string Finish(string original, string translated, Config config)
    {
        // Traditional → Simplified is applied after alignment so that the line mapping stays stable.
        var result = LyricsAligner.Align(original, translated);
        return config.ToSimplifiedChinese ? ChineseConverterHelper.ToSimplified(result) : result;
    }

    private async Task<string> TranslateRawAsync(string text, Config config, string targetLanguage,
        CancellationToken token)
    {
        var mode = config.Mode ?? Config.ModeGoogle;
        var key = (config.ApiKey ?? string.Empty).Trim();

        if (string.Equals(mode, Config.ModeGoogle, StringComparison.OrdinalIgnoreCase))
            return await _backends.GoogleAsync(text, config, targetLanguage, token);

        if (string.IsNullOrEmpty(key))
        {
            _logger.Warn("API key is empty, falling back to Google");
            return await _backends.GoogleAsync(text, config, targetLanguage, token);
        }

        if (string.Equals(mode, Config.ModeGemini, StringComparison.OrdinalIgnoreCase))
            return await TranslateChunkedAsync(text, config, targetLanguage, token);

        var url = (config.ApiUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(url))
        {
            _logger.Warn("API URL is empty, falling back to Google");
            return await _backends.GoogleAsync(text, config, targetLanguage, token);
        }

        if (url.Contains("deepl", StringComparison.OrdinalIgnoreCase))
            return await _backends.DeepLAsync(text, config, token);

        return await TranslateChunkedAsync(text, config, targetLanguage, token);
    }

    private async Task<string> TranslateChunkedAsync(string text, Config config, string targetLanguage,
        CancellationToken token)
    {
        var lines = text.Split('\n');
        var threshold = config.ChunkThreshold <= 0 ? 24 : (int)config.ChunkThreshold;

        if (lines.Length <= threshold)
            return await TranslateOnceAsync(text, config, targetLanguage, token);

        var chunks = new List<string>();
        for (var i = 0; i < lines.Length; i += ChunkSize)
            chunks.Add(string.Join('\n', lines.Skip(i).Take(ChunkSize)));

        _logger.Info($"Splitting into {chunks.Count} chunks of {ChunkSize} lines");

        var results = new string[chunks.Count];
        using var throttle = new SemaphoreSlim(MaxParallelChunks);

        var tasks = chunks.Select(async (chunk, index) =>
        {
            await throttle.WaitAsync(token);
            try
            {
                var chunkLines = chunk.Split('\n').Length;
                var translated = await TranslateOnceAsync(chunk, config, targetLanguage, token);

                if (string.IsNullOrEmpty(translated))
                {
                    _logger.Warn($"Chunk {index + 1}/{chunks.Count} returned nothing, keeping the original");
                    results[index] = chunk;
                    return;
                }

                var aligned = LyricsAligner.Align(chunk, translated);
                var alignedLines = aligned.Split('\n').Length;
                if (alignedLines == chunkLines)
                {
                    results[index] = aligned;
                }
                else
                {
                    _logger.Warn($"Chunk {index + 1}/{chunks.Count} still misaligned after fixing, keeping the original");
                    results[index] = chunk;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Chunk {index + 1}/{chunks.Count} threw, keeping the original", ex);
                results[index] = chunk;
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
            results[i] ??= chunks[i];

        _logger.Info($"Chunked translation done, {string.Join('\n', results).Split('\n').Length} lines");
        return string.Join('\n', results);
    }

    private async Task<string> TranslateOnceAsync(string text, Config config, string targetLanguage,
        CancellationToken token)
    {
        if (string.Equals(config.Mode, Config.ModeGemini, StringComparison.OrdinalIgnoreCase))
            return await _backends.GeminiAsync(text, config, token);

        return await _backends.ChatAsync(text, config, token);
    }

    /// <summary>
    /// The host passes the requested target language. The AI prompt already forces Chinese output,
    /// so only the Google endpoint needs an explicit <c>tl</c>; everything Chinese maps to zh-CN
    /// because the plugin emits Simplified Chinese.
    /// </summary>
    private static string ResolveTargetLanguage(LanguageTag? targetLangTag)
    {
        var code = targetLangTag?.ToString();
        if (string.IsNullOrWhiteSpace(code)) return "zh-CN";

        if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";

        return code;
    }
}
