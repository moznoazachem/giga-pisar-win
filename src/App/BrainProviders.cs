// Known cloud services for the server Brain. People usually have a key and the
// name of the service, not its API address, so the dialog asks for the service
// (or guesses it from the key) and fills in the address and a sensible model.

using System.Text.RegularExpressions;

namespace GigaPisar.App;

public sealed record BrainProvider(string Id, string Name, string BaseUrl, string[] PreferredModels, string KeysUrl)
{
    public bool IsCustom => BaseUrl.Length == 0;
}

public static partial class BrainProviders
{
    public static readonly BrainProvider DeepSeek = new("deepseek", "DeepSeek", "https://api.deepseek.com/v1",
        ["deepseek-flash", "deepseek-chat"], "https://platform.deepseek.com/api_keys");
    public static readonly BrainProvider OpenRouter = new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1",
        ["deepseek/deepseek-chat-v3-0324", "deepseek/deepseek-chat", "google/gemini-2.5-flash", "openai/gpt-4.1-mini"], "https://openrouter.ai/keys");
    public static readonly BrainProvider OpenAI = new("openai", "OpenAI", "https://api.openai.com/v1",
        ["gpt-4.1-mini", "gpt-4o-mini"], "https://platform.openai.com/api-keys");
    public static readonly BrainProvider Groq = new("groq", "Groq", "https://api.groq.com/openai/v1",
        ["llama-3.3-70b-versatile"], "https://console.groq.com/keys");
    public static readonly BrainProvider Gemini = new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai",
        ["gemini-2.5-flash", "gemini-2.0-flash"], "https://aistudio.google.com/apikey");
    public static readonly BrainProvider Anthropic = new("anthropic", "Anthropic (Claude)", "https://api.anthropic.com/v1",
        ["claude-haiku-4-5"], "https://console.anthropic.com/settings/keys");
    public static readonly BrainProvider Custom = new("custom", "", "", [], "");

    public static readonly BrainProvider[] All = [DeepSeek, OpenRouter, OpenAI, Groq, Gemini, Anthropic, Custom];

    public static string Title(BrainProvider p) => p.IsCustom ? L.T("Свой сервер (LM Studio, Ollama…)", "Own server (LM Studio, Ollama…)") : p.Name;

    [GeneratedRegex("^sk-[0-9a-f]{32}$")]
    private static partial Regex DeepSeekKey();

    /// <summary>Guesses the service from the look of the key; null when it could be anything.</summary>
    public static BrainProvider? FromKey(string key)
    {
        key = key.Trim();
        if (key.StartsWith("sk-or-")) return OpenRouter;
        if (key.StartsWith("sk-ant-")) return Anthropic;
        if (key.StartsWith("gsk_")) return Groq;
        if (key.StartsWith("AIza")) return Gemini;
        if (DeepSeekKey().IsMatch(key)) return DeepSeek;
        if (key.StartsWith("sk-proj-") || key.StartsWith("sk-svcacct-") || key.StartsWith("sk-")) return OpenAI;
        return null;
    }

    /// <summary>The service a saved address belongs to; Custom for anything else.</summary>
    public static BrainProvider FromUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) u = u[..^"/chat/completions".Length];
        return All.FirstOrDefault(p => !p.IsCustom && string.Equals(p.BaseUrl, u, StringComparison.OrdinalIgnoreCase)) ?? Custom;
    }

    /// <summary>Only chat models are useful here: drop embeddings, speech, images and the like.</summary>
    public static IEnumerable<string> ChatModels(IEnumerable<string> ids) =>
        ids.Where(id => !Regex.IsMatch(id, "embed|whisper|tts|dall-e|moderation|image|audio|realtime|transcribe|search|guard|imagen|veo|aqa", RegexOptions.IgnoreCase))
           .Select(id => id.StartsWith("models/") ? id["models/".Length..] : id);

    /// <summary>A sensible default among the models the server offers.</summary>
    public static string? PickDefault(BrainProvider p, IReadOnlyList<string> models)
    {
        foreach (var want in p.PreferredModels)
        {
            var hit = models.FirstOrDefault(m => m.Equals(want, StringComparison.OrdinalIgnoreCase))
                      ?? models.FirstOrDefault(m => m.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return models.Count > 0 ? models[0] : p.PreferredModels.FirstOrDefault();
    }
}
