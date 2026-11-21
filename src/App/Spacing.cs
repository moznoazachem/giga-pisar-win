// Keeps a dictation from sticking to the text that is already before the caret.
// Some apps drop the trailing space of a pasted fragment (issue #19), so the
// space that separates two dictations cannot be left to the previous paste.

namespace GigaPisar.App;

public static class Spacing
{
    private const string Closing = ".,!?;:…)]}»\"”'’%";
    private const string Opening = "«\"“([";

    /// <summary>
    /// True when <paramref name="text"/> goes right after a word or a closing mark and starts a word itself.
    /// At the start of a field or a line, after a space or an opening bracket no space is needed.
    /// </summary>
    public static bool NeedsSeparator(char? before, string text)
    {
        if (before is not char b || text.Length == 0) return false;
        char first = text[0];
        bool afterWord = char.IsLetterOrDigit(b) || Closing.Contains(b);
        bool startsWord = char.IsLetterOrDigit(first) || Opening.Contains(first);
        return afterWord && startsWord;
    }
}
