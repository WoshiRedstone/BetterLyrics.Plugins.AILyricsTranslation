using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// The translation engines ported from BetterLyrics.py:
/// Google web endpoint, OpenAI-compatible chat completions, Gemini generateContent and DeepL.
/// Every engine returns the raw translated text; alignment / Chinese conversion happen in
/// <see cref="TranslationEngine"/>.
/// </summary>
internal sealed class TranslationBackends(HttpClient http, PluginLogger logger)
{
    private const string GoogleEndpoint = "https://translate.googleapis.com/translate_a/single";
    private const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1/models";

    // Google and DeepL are fast machine translation services; the values are ported from
    // BetterLyrics.py. The LLM backends generate the whole translation token by token and routinely
    // take 25 s for a single verse, so their timeout is user-configurable (default 60 s) instead of
    // the fixed 30 s of the original — with 30 s a slow model silently returned the untranslated text.
    private static readonly TimeSpan GoogleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeepLTimeout = TimeSpan.FromSeconds(10);

    private const double MinRequestTimeoutSeconds = 1;
    private const double MaxRequestTimeoutSeconds = 600;

    private static TimeSpan RequestTimeout(Config config)
    {
        var seconds = config.RequestTimeoutSeconds;
        if (double.IsNaN(seconds) || seconds <= 0) seconds = 60;
        seconds = Math.Clamp(seconds, MinRequestTimeoutSeconds, MaxRequestTimeoutSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private readonly HttpClient _http = http;
    private readonly PluginLogger _logger = logger;

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request,
        TimeSpan timeout, CancellationToken token)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var response = await _http.SendAsync(request, timeoutSource.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            return (response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A cancellation the caller did not ask for is our own timeout. Surface it as one so the
            // log says "timed out" instead of hiding behind a generic TaskCanceledException.
            throw new TimeoutException(
                $"The request did not finish within {timeout.TotalSeconds:0.#} s. " +
                "Increase \"Request Timeout (seconds)\" if the model is slow.");
        }
    }

    // ---------------------------------------------------------------- Google

    public async Task<string> GoogleAsync(string text, Config config, string targetLanguage, CancellationToken token)
    {
        var url = BuildGoogleUrl(config.ApiUrl, targetLanguage, text);

        _logger.Info($"Google request: {text.Length} chars, tl={targetLanguage}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var (status, body) = await SendAsync(request, GoogleTimeout, token);

        if (status != HttpStatusCode.OK)
        {
            _logger.Warn($"Google failed HTTP {(int)status}: {Truncate(body)}");
            return text;
        }

        var parsed = JsonNode.Parse(body);
        var parts = parsed?[0]?.AsArray();
        if (parts == null) return text;

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var piece = part?[0]?.ToString();
            if (!string.IsNullOrEmpty(piece)) builder.Append(piece);
        }

        var result = builder.ToString();
        _logger.Info($"Google success: out={CountLines(result)} lines");
        return result;
    }

    // ------------------------------------------------------- OpenAI-compatible

    public async Task<string> ChatAsync(string text, Config config, CancellationToken token)
    {
        var targetUrl = BuildChatCompletionsUrl(config.ApiUrl);
        var payload = new JsonObject
        {
            ["model"] = config.ModelName,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = config.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = text }
            },
            ["temperature"] = config.Temperature
        };

        _logger.Info($"Chat request: {targetUrl}, model={config.ModelName}, in={CountLines(text)} lines");

        using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey.Trim()}");

        var response = await SendAsync(request, RequestTimeout(config), token);
        var status = response.Status;
        var body = response.Body;

        if (status != HttpStatusCode.OK)
        {
            _logger.Warn($"Chat failed HTTP {(int)status}: {Truncate(body)}");
            return string.Empty;
        }

        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        _logger.Info($"Chat success: out={CountLines(content)} lines");
        return content.Trim();
    }

    public static string BuildChatCompletionsUrl(string rawUrl)
    {
        var target = (rawUrl ?? string.Empty).Trim();
        if (target.Contains("chat/completions", StringComparison.OrdinalIgnoreCase)) return target;

        if (!target.EndsWith('/')) target += '/';
        if (!target.Contains("v1", StringComparison.OrdinalIgnoreCase)) target += "v1/";
        target += "chat/completions";
        return target;
    }

    /// <summary>
    /// Builds the generateContent URL. Without an API URL the public Google endpoint is used (exactly
    /// like BetterLyrics.py); setting one lets a self-hosted Gemini-compatible proxy be targeted.
    /// </summary>
    public static string BuildGeminiUrl(string rawUrl, string model, string apiKey)
    {
        var key = Uri.EscapeDataString((apiKey ?? string.Empty).Trim());
        var target = (rawUrl ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(target) || target.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase))
            return $"{GeminiEndpoint}/{model}:generateContent?key={key}";

        if (target.Contains(":generateContent", StringComparison.OrdinalIgnoreCase)) return target;

        if (!target.EndsWith('/')) target += '/';
        if (!target.Contains("v1", StringComparison.OrdinalIgnoreCase)) target += "v1/";
        target += "models";
        return $"{target}/{model}:generateContent?key={key}";
    }

    /// <summary>DeepL is selected by URL, exactly like <c>"deepl.com" in url.lower()</c> in the Python original.</summary>
    public static bool IsDeepLUrl(string? rawUrl) =>
        !string.IsNullOrWhiteSpace(rawUrl) &&
        rawUrl.Contains("deepl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the gtx request URL. Without an API URL the public Google endpoint is used (exactly
    /// like BetterLyrics.py); setting one lets a self-hosted mirror be targeted, which is also the
    /// only way to exercise this backend on a machine that cannot reach googleapis.com.
    /// </summary>
    public static string BuildGoogleUrl(string? rawUrl, string targetLanguage, string text)
    {
        var target = (rawUrl ?? string.Empty).Trim();
        var endpoint = string.IsNullOrEmpty(target) ||
                       target.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase)
            ? GoogleEndpoint
            : target;

        // A bare host or base URL is completed the same way BuildChatCompletionsUrl does it, so a
        // self-hosted mirror can be configured as "http://host:port" instead of the full path.
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) &&
            (string.IsNullOrEmpty(parsed.AbsolutePath) || parsed.AbsolutePath == "/"))
        {
            endpoint = endpoint.TrimEnd('/') + "/translate_a/single";
        }

        var separator = endpoint.Contains('?') ? '&' : '?';
        return $"{endpoint}{separator}client=gtx&sl=auto" +
               $"&tl={Uri.EscapeDataString(targetLanguage)}&dt=t&q={Uri.EscapeDataString(text)}";
    }

    // ---------------------------------------------------------------- Gemini

    public async Task<string> GeminiAsync(string text, Config config, CancellationToken token)
    {
        var model = string.IsNullOrWhiteSpace(config.ModelName) ? "gemini-1.5-flash" : config.ModelName.Trim();
        var url = BuildGeminiUrl(config.ApiUrl, model, config.ApiKey);

        var payload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = config.SystemPrompt + "\n\n" + text }
                    }
                }
            }
        };

        _logger.Info($"Gemini request: model={model}, in={CountLines(text)} lines");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        var response = await SendAsync(request, RequestTimeout(config), token);
        var status = response.Status;
        var body = response.Body;

        if (status != HttpStatusCode.OK)
        {
            _logger.Warn($"Gemini failed HTTP {(int)status}: {Truncate(body)}");
            return string.Empty;
        }

        var content = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        _logger.Info($"Gemini success: out={CountLines(content)} lines");
        return content.Trim();
    }

    // ----------------------------------------------------------------- DeepL

    public async Task<string> DeepLAsync(string text, Config config, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl.Trim())
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("text", text),
                new KeyValuePair<string, string>("target_lang", "ZH")
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {config.ApiKey.Trim()}");

        _logger.Info($"DeepL request: in={CountLines(text)} lines");

        var response = await SendAsync(request, DeepLTimeout, token);
        var status = response.Status;
        var body = response.Body;

        if (status != HttpStatusCode.OK)
        {
            _logger.Warn($"DeepL failed HTTP {(int)status}: {Truncate(body)}");
            return string.Empty;
        }

        var content = JsonNode.Parse(body)?["translations"]?[0]?["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        _logger.Info("DeepL success");
        return content;
    }

    // ------------------------------------------------------------ connection test

    public async Task<(bool Success, string Message)> TestAsync(Config config, CancellationToken token)
    {
        var first = await TestOnceAsync(config, token);
        if (first.Success || token.IsCancellationRequested || !IsTransientFailure(first.Message))
            return first;

        // Public endpoints stall now and then: the same probe answered in 3 s, 23 s and >90 s
        // within one minute while testing. A single retry keeps a hiccup from being reported as
        // bad credentials. A real rejection (HTTP 401 / 404) is not transient and is not retried.
        return await TestOnceAsync(config, token);
    }

    private static bool IsTransientFailure(string message) =>
        message.Contains("did not finish within", StringComparison.Ordinal) ||
        message.Contains("HttpRequestException", StringComparison.Ordinal) ||
        message.Contains("SSL connection", StringComparison.Ordinal);

    private async Task<(bool Success, string Message)> TestOnceAsync(Config config, CancellationToken token)
    {
        try
        {
            if (string.Equals(config.Mode, Config.ModeGoogle, StringComparison.OrdinalIgnoreCase))
                return (true, "Google mode needs no connection test.");

            if (string.IsNullOrWhiteSpace(config.ApiKey))
                return (false, "API Key is empty.");

            if (string.Equals(config.Mode, Config.ModeGemini, StringComparison.OrdinalIgnoreCase))
            {
                var model = string.IsNullOrWhiteSpace(config.ModelName) ? "gemini-1.5-flash" : config.ModelName.Trim();
                var url = BuildGeminiUrl(config.ApiUrl, model, config.ApiKey);
                var payload = new JsonObject
                {
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = "Hello" } }
                        }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                };
                var geminiTest = await SendAsync(request, RequestTimeout(config), token);
                var body = geminiTest.Body;

                if (geminiTest.Status != HttpStatusCode.OK)
                    return (false, $"Gemini test failed (HTTP {(int)geminiTest.Status}): {Truncate(body)}");

                var reply = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                return (true, $"Gemini OK: {(reply ?? "<empty>").Trim()}");
            }

            if (string.IsNullOrWhiteSpace(config.ApiUrl))
                return (false, "API URL is empty.");

            if (IsDeepLUrl(config.ApiUrl))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl.Trim())
                {
                    Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("text", "Hello"),
                        new KeyValuePair<string, string>("target_lang", "ZH")
                    })
                };
                request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {config.ApiKey.Trim()}");

                var deeplTest = await SendAsync(request, DeepLTimeout, token);
                var body = deeplTest.Body;

                if (deeplTest.Status != HttpStatusCode.OK)
                    return (false, $"DeepL test failed (HTTP {(int)deeplTest.Status}): {Truncate(body)}");

                var reply = JsonNode.Parse(body)?["translations"]?[0]?["text"]?.ToString();
                return (true, $"DeepL OK: {reply}");
            }

            var chatUrl = BuildChatCompletionsUrl(config.ApiUrl);
            var chatPayload = new JsonObject
            {
                ["model"] = config.ModelName,
                // A dedicated probe prompt rather than the lyric system prompt: with the real
                // translation prompt a reasoning model spent the whole max_tokens budget on
                // hidden tokens and returned an empty string, which reports success with no reply.
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = "You are a connectivity probe. Reply with the single word: OK"
                    },
                    new JsonObject { ["role"] = "user", ["content"] = "Reply with OK" }
                },
                ["temperature"] = 0.4,
                ["max_tokens"] = 128
            };

            using var chatRequest = new HttpRequestMessage(HttpMethod.Post, chatUrl)
            {
                Content = new StringContent(chatPayload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            chatRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey.Trim()}");

            var chatResponse = await SendAsync(chatRequest, RequestTimeout(config), token);
            var chatBody = chatResponse.Body;

            if (chatResponse.Status != HttpStatusCode.OK)
                return (false, $"AI test failed (HTTP {(int)chatResponse.Status}): {Truncate(chatBody)}");

            var chatReply = JsonNode.Parse(chatBody)?["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(chatReply))
            {
                // A reasoning model can burn the whole token budget on hidden tokens and still
                // answer 200 with no content, so an empty reply must not be reported as a pass.
                var finish = JsonNode.Parse(chatBody)?["choices"]?[0]?["finish_reason"]?.ToString();
                return (false, $"AI returned an empty reply (finish_reason={finish ?? "?"}).");
            }

            return (true, $"AI OK: {chatReply}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection test threw: {ex.Message}");
        }
    }

    private static int CountLines(string text) => text.Split('\n').Length;

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200];
}


