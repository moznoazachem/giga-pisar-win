// The Brain: an AI model that edits dictated text, as in the macOS app.
//
// Say the text and finish with an address in plain words:
//   "…waiting for your answer. Pisar, fix it"
//   "…call at five. Giga Pisar, translate into English"
// Everything after "Pisar" is the command. No address, no Brain: the text is
// inserted at once and the model never sees it. With "edit every take" on,
// every dictation goes through the Brain with the cleanup instructions.
//
// The model runs either on this computer (LocalBrain) or on a server the user
// chose (OpenAI-compatible API). Whatever goes wrong, dictation must not break:
// the caller inserts the text as recognized.

using System.Text.RegularExpressions;

namespace GigaPisar.App;

public enum BrainSource { Off, Local, Server }

public static partial class Brain
{
    /// <summary>Same wording as the macOS app, so both edit text alike.</summary>
    private const string CommandPrompt =
        "Ты обрабатываешь надиктованный голосом текст перед вставкой. Правила: " +
        "убери слова-паразиты и оговорки (э, ну, типа, вот, как бы), убери повторы " +
        "и самоисправления, расставь знаки препинания, исправь очевидные ошибки " +
        "распознавания. Сохраняй смысл и лексику, ничего не добавляй от себя и " +
        "не комментируй. Живой тон автора сохраняй, если только команда не велит " +
        "его изменить: команда важнее тона. Выполни команду пользователя: она " +
        "дана в конце этой инструкции, в сам текст не входит, и упоминать её " +
        "в ответе нельзя. Верни ТОЛЬКО готовый текст, без кавычек вокруг него.";

    /// <summary>For commands on a selection: the text is already written, touch only what the command asks.</summary>
    private const string SelectionPrompt =
        "Ты редактируешь текст, который пользователь выделил в своём документе, " +
        "и выполняешь над ним команду пользователя. Сохраняй смысл и разбиение " +
        "на абзацы, ничего не добавляй от себя и не комментируй. Тон и стиль " +
        "сохраняй, если только команда не велит их изменить: команда важнее. " +
        "Команда дана в конце этой инструкции, в сам текст не входит, и " +
        "упоминать её в ответе нельзя. Верни ТОЛЬКО готовый текст, без кавычек " +
        "вокруг него.";

    /// <summary>Recognition may hear "песарь" or "писарь" with various endings; "Гига" is optional.</summary>
    [GeneratedRegex(@"(?:гига[\s,—-]+)?п[еиэ]сар[ьяюе]?\b[\s,.:!—-]*", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    /// <summary>Splits "text. Pisar, command" into body and command; null when there is no address.</summary>
    public static (string body, string command)? ParseCommand(string text)
    {
        var matches = AddressRegex().Matches(text);
        if (matches.Count == 0) return null;
        var m = matches[^1];   // the last address wins: the text itself may mention Pisar
        var command = text[(m.Index + m.Length)..].Trim();
        var body = text[..m.Index].TrimEnd();
        while (body.Length > 0 && ",—–-".Contains(body[^1])) body = body[..^1].TrimEnd();
        if (command.Length == 0 || body.Length == 0) return null;
        return (body, command.TrimEnd('.', '!'));
    }

    [GeneratedRegex(@"^\s*(?:гига[\s,—-]+)?п[еиэ]сар[ьяюе]?\b[\s,.:!—-]*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingAddressRegex();

    /// <summary>"Писарь, сделай короче" -> "сделай короче": the address before a command on a selection is optional.</summary>
    public static string StripAddress(string text) => LeadingAddressRegex().Replace(text, "").Trim().TrimEnd('.', '!');

    /// <summary>What the pill says while the model works.</summary>
    public static string ActionLabel(string? command)
    {
        var c = (command ?? "").ToLowerInvariant();
        if (c.Contains("перевед") || c.Contains("англ")) return L.T("Перевожу…", "Translating…");
        if (c.Contains("сократ") || c.Contains("короче")) return L.T("Сокращаю…", "Shortening…");
        if (c.Contains("сглад") || c.Contains("мягче") || c.Contains("вежлив")) return L.T("Сглаживаю…", "Smoothing…");
        if (c.Contains("исправ") || c.Contains("ошибк")) return L.T("Исправляю…", "Fixing…");
        return L.T("Причёсываю…", "Polishing…");
    }

    public static bool ServerConfigured(Settings s) =>
        SpeechCleanup.TryGetCompletionsUrl(s.CleanupEndpointUrl, out _) && s.CleanupModel.Length > 0;

    /// <summary>
    /// Runs the text through the chosen Brain. command == null means "edit every take" mode.
    /// Throws BrainException (with a human reason) or HttpRequestException on failure.
    /// </summary>
    public static async Task<string> TransformAsync(Settings s, string body, string? command,
        Action<string> status, CancellationToken ct, bool selection = false)
    {
        string prompt = command == null ? s.EffectiveCleanupPrompt
            : (selection ? SelectionPrompt : CommandPrompt) + "\n\nКоманда пользователя к тексту: " + command + ".";
        string action = ActionLabel(command);

        if (s.Brain == BrainSource.Local)
        {
            if (!LocalBrain.Downloaded) throw new BrainException(L.T("модель Мозга не скачана", "the Brain model is not downloaded"));
            if (!LocalBrain.Running) status(L.T("Запускаю нейронку…", "Starting the Brain…"));
            await LocalBrain.EnsureStartedAsync(sec => status(L.T($"Запускаю нейронку… {sec} с", $"Starting the Brain… {sec}s")), ct);
            status(action);
            var extra = new Dictionary<string, object>
            {
                ["temperature"] = 0.3,
                ["max_tokens"] = 1024,
                // Qwen3 can think aloud in a <think> block; for editing text that is only slow.
                ["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false },
            };
            return await SpeechCleanup.CleanAsync(body, LocalBrain.EndpointUrl, LocalBrain.ApiKey, "local", prompt, ct,
                extra, TimeSpan.FromSeconds(120));
        }

        status(action);
        return await SpeechCleanup.CleanAsync(body, s.CleanupEndpointUrl, s.CleanupApiKey, s.CleanupModel, prompt, ct);
    }
}
