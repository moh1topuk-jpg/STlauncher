using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.App.Services;

/// <summary>
/// In-app translation of mod descriptions. Uses the public Google endpoint first and
/// falls back to MyMemory; results are cached so repeated pages cost nothing.
/// </summary>
public sealed class TranslationService
{
    private const int MaxConcurrency = 4;

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _throttle = new(MaxConcurrency);

    /// <summary>
    /// The Google endpoint answers 429 when it throttles us. Once that happens we stop
    /// calling it for a while instead of burning a failed request per string.
    /// </summary>
    private DateTimeOffset _googleBlockedUntil = DateTimeOffset.MinValue;

    public TranslationService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Returns the translated text, or null when no service could handle it. The caller
    /// keeps the original text in that case.
    /// </summary>
    public async Task<string?> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(targetLanguage))
        {
            return null;
        }

        var key = targetLanguage + "\u0001" + text;

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string? translated = null;

            if (DateTimeOffset.Now >= _googleBlockedUntil)
            {
                translated = await TryGoogleAsync(text, targetLanguage, cancellationToken).ConfigureAwait(false);

                if (translated is null)
                {
                    _googleBlockedUntil = DateTimeOffset.Now.AddMinutes(10);
                }
            }

            translated ??= await TryMyMemoryAsync(text, targetLanguage, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(translated) &&
                !string.Equals(translated, text, StringComparison.Ordinal))
            {
                _cache[key] = translated;
                return translated;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<string?> TryGoogleAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single" +
                      $"?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}&dt=t" +
                      $"&q={Uri.EscapeDataString(text)}";

            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            var segments = root[0];

            if (segments.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new System.Text.StringBuilder();

            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array &&
                    segment.GetArrayLength() > 0 &&
                    segment[0].ValueKind == JsonValueKind.String)
                {
                    builder.Append(segment[0].GetString());
                }
            }

            var result = builder.ToString();
            return result.Length == 0 ? null : result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> TryMyMemoryAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        try
        {
            // The free tier is happiest with short inputs.
            var payload = text.Length > 480 ? text[..480] : text;
            var url = "https://api.mymemory.translated.net/get?langpair=autodetect|" +
                      $"{Uri.EscapeDataString(targetLanguage)}&q={Uri.EscapeDataString(payload)}";

            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("responseData", out var data) &&
                data.TryGetProperty("translatedText", out var translated) &&
                translated.ValueKind == JsonValueKind.String)
            {
                return translated.GetString();
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}