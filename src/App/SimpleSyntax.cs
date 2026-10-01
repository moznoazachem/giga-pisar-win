// "Simplify punctuation", as on macOS 3.9: a single dictated sentence goes in like a chat
// reply, lowercase and without the closing period. Off by default: letters and documents
// need the period. Only plain dictation goes through it; Brain answers to commands and
// edits of a selection are inserted exactly as they are.

namespace GigaPisar.App;

public static class SimpleSyntax
{
    private static readonly HashSet<char> Ends = ['.', '!', '?', '…', ';'];

    public static string Apply(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Contains('\n')) return text;
        // A sentence end inside the text (not at its very end) means several sentences: leave them be.
        // Abbreviations like "т.д." fall under this too, and that is fine: no guessing for the user.
        if (t[..^1].Any(Ends.Contains)) return text;

        var result = t;
        // A question or an exclamation carries meaning: only the period goes.
        if (result.EndsWith('.')) result = result[..^1];
        // Leave acronyms and the like alone: lowercase only a capital that just starts the sentence.
        var firstWord = result.Split(' ')[0];
        bool allCaps = firstWord.Length > 1 && firstWord == firstWord.ToUpperInvariant();
        if (!allCaps && result.Length > 0 && char.IsUpper(result[0]))
            result = char.ToLowerInvariant(result[0]) + result[1..];
        return result;
    }
}
