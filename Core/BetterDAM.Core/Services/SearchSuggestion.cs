namespace BetterDAM.Core.Services;

/// <summary>
/// Decides when the search box should offer the list of fields, and rewrites the text when one is
/// chosen.
///
/// Kept out of the view because it is entirely string work and the interesting cases — a colon in
/// the middle of a word, a second colon, a caret that is not at the end — are far easier to state as
/// tests than to reproduce by typing into a window.
/// </summary>
public static class SearchSuggestion
{
    /// <summary>
    /// The partly typed field name to offer completions for, or null when the box should not be
    /// offering anything.
    ///
    /// The trigger is a colon at the caret: <c>:</c> on its own means "remind me what there is" and
    /// returns an empty prefix, while <c>k:</c> returns <c>k</c>. Once a value is typed after the
    /// colon the offer goes away, because by then the user is answering rather than asking.
    /// </summary>
    public static string? PrefixAt(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
        {
            return null;
        }

        if (text[caret - 1] != ':')
        {
            return null;
        }

        var start = TokenStart(text, caret - 1);
        var prefix = text[start..(caret - 1)];

        // A token that already holds a colon is a field with a value, not a field being named —
        // "lens:RF:" should not reopen the list.
        return prefix.Contains(':') ? null : prefix;
    }

    /// <summary>
    /// Replaces the field being typed with <paramref name="field"/>, and reports where the caret
    /// should land — after the colon, ready for the value.
    /// </summary>
    public static (string Text, int Caret) Accept(string? text, int caret, string field)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);

        // Everything from the start of the token up to the caret is what the user was typing, and is
        // what the chosen field replaces.
        var start = caret > 0 && text[caret - 1] == ':' ? TokenStart(text, caret - 1) : TokenStart(text, caret);

        var replacement = field + ":";
        return (text[..start] + replacement + text[caret..], start + replacement.Length);
    }

    private static int TokenStart(string text, int from)
    {
        var start = from;

        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        return start;
    }
}
