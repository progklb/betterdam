using System.Text;

namespace BetterDAM.Core.Services;

/// <summary>
/// Edits one field inside a typed query, leaving the rest of it alone.
///
/// This is what lets the filter controls write into the search box rather than keep a filter state
/// of their own. There is then one query, visible and editable, and clicking three stars teaches the
/// user that it is spelled <c>r:&gt;=3</c> — which is the point of having short forms at all. A
/// parallel set of filters would have needed reconciling with the text every time either changed.
/// </summary>
public static class SearchQueryText
{
    /// <summary>
    /// Returns <paramref name="text"/> with <paramref name="field"/> set to
    /// <paramref name="value"/>, replacing any existing term for that field, or removing it when
    /// the value is null.
    ///
    /// The field is written in its short form: the box is the only place the syntax is on show, so
    /// it may as well show the form worth learning.
    /// </summary>
    public static string WithField(string? text, string field, string? value)
    {
        var canonical = SearchFields.Resolve(field)
            ?? throw new ArgumentException($"'{field}' is not a search field.", nameof(field));

        var shortForm = SearchFields.All.First(f => f.Name == canonical).Short;
        var rebuilt = new StringBuilder();

        foreach (var token in SearchQueryParser.Tokenize(text ?? string.Empty))
        {
            var separator = token.IndexOf(':');

            // Any spelling of the same field goes, so setting a value never leaves an older term
            // for it further along the query still filtering.
            if (separator > 0 && SearchFields.Resolve(token[..separator]) == canonical)
            {
                continue;
            }

            Append(rebuilt, token);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            Append(rebuilt, $"{shortForm}:{Quote(value)}");
        }

        return rebuilt.ToString();
    }

    private static void Append(StringBuilder builder, string token)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(token);
    }

    /// <summary>A value with a space in it has to come back quoted or it tokenizes as two terms.</summary>
    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) && !value.StartsWith('"') ? $"\"{value}\"" : value;
}
