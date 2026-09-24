using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GigaPisar.App;

public static class SpeechCleanup
{
    public const string DefaultPrompt = "Ты редактируешь результат автоматического распознавания русской речи. Исправь ошибки распознавания, пунктуацию, регистр и очевидные оговорки. Сохрани смысл, язык, имена, числа и стиль говорящего. Не добавляй фактов и не отвечай на вопросы из текста. Верни только исправленный текст без пояснений и кавычек.";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static bool TryGetCompletionsUrl(string endpoint, out Uri? url)
    {
        url = null;
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            !string.IsNullOrEmpty(baseUri.UserInfo)) return false;

        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + path);
        else
            url = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + path + "/chat/completions");
        return true;
    }

    public static async Task<string> CleanAsync(string text, string endpoint, string apiKey, string model, string prompt, CancellationToken cancellationToken)
    {
        if (!TryGetCompletionsUrl(endpoint, out var url)) throw new ArgumentException("Invalid cleanup endpoint URL");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = prompt },
                new { role = "user", content = text },
            },
        });

        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var cleaned = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) throw new InvalidDataException("Cleanup server returned empty text");
        return cleaned;
    }

    public static async Task<IReadOnlyList<string>> GetModelsAsync(string endpoint, string apiKey, CancellationToken cancellationToken)
    {
        if (!TryGetCompletionsUrl(endpoint, out var completionsUrl)) throw new ArgumentException("Invalid cleanup endpoint URL");
        var modelsUrl = new Uri(completionsUrl!, "../models");
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
    }
}
