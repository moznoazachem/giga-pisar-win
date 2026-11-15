using System.Text;
using System.Text.RegularExpressions;

namespace GigaPisar.App;

/// <summary>
/// Cuts hesitation sounds out of a take: "э-э", "а-а", "м-м" and their spellings. Runs on the
/// computer, so it works with the Brain off and without a network. Words that are sometimes
/// filler and sometimes meaning ("вот", "ну", "короче") are not touched: that is the Brain's job.
/// </summary>
public static class Hesitations
{
    // A lone "а" is a conjunction and "мм" is millimetres, so those two stay. A sound in quotes
    // is somebody talking about the sound itself.
    private static readonly Regex Sound = new(
        @"(?<![\p{L}\p{N}«""„“-])" +
        @"(?:[эЭ]+(?:-[эЭ]+)*[мМ]*|[аА]+(?:-[аА]+)+|[аА]{3,}|[мМ]+(?:-[мМ]+)+|[мМ]{3,})" +
        @"(?![\p{L}\p{N}»""“”-])",
        RegexOptions.Compiled);

    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private const string SentenceEnds = ".!?…";
    private const string AfterSound = ",.!?…";

    public static string Removed(string text)
    {
        if (!Sound.IsMatch(text)) return text;
        var result = new StringBuilder();
        int at = 0;
        bool capitalize = false;

        void Push(string chunk)
        {
            if (chunk.Length == 0) return;
            if (capitalize) result.Append(char.ToUpperInvariant(chunk[0])).Append(chunk, 1, chunk.Length - 1);
            else result.Append(chunk);
            capitalize = false;
        }

        foreach (Match match in Sound.Matches(text))
        {
            if (match.Index < at) continue;
            Push(text[at..match.Index]);
            at = match.Index + match.Length;
            int kept = result.Length;
            while (kept > 0 && (result[kept - 1] == ' ' || result[kept - 1] == '\t')) kept--;
            bool opensSentence = kept == 0 || SentenceEnds.Contains(result[kept - 1]) || result[kept - 1] == '\n';
            if (opensSentence)
            {
                // "А-а, просто сразу" -> "Просто сразу": the sound goes with its own punctuation.
                while (at < text.Length && AfterSound.Contains(text[at])) at++;
                while (at < text.Length && (text[at] == ' ' || text[at] == '\t')) at++;
                if (char.IsUpper(match.Value[0])) capitalize = true;
                continue;
            }
            result.Length = kept;
            char? next = at < text.Length ? text[at] : null;
            if (result[^1] == ',')
            {
                // "сообщения, э-э, которые" -> "сообщения, которые": one comma is enough.
                if (next == ',') at++;
                // "завтра, э-э." -> "завтра."
                else if (next == null || SentenceEnds.Contains(next.Value)) result.Length--;
            }
        }
        Push(text[at..]);

        var cleaned = Spaces.Replace(result.ToString(), " ").Trim().TrimEnd(',').TrimEnd();
        return cleaned.Any(char.IsLetterOrDigit) ? cleaned : "";
    }
}
