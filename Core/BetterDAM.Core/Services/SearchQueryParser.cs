using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using BetterDAM.Core.Models;

namespace BetterDAM.Core.Services;

/// <summary>
/// Turns a typed query into a <see cref="SearchQuery"/>.
///
/// Supports the syntax from the project README:
/// <code>
/// keyword:motorcycle
/// rating:&gt;=4
/// camera:Sony
/// lens:"RF 100-500"
/// type:video
/// date:&gt;=2024-01-01
/// </code>
/// Terms combine with implicit AND; a literal <c>AND</c> is accepted and ignored. Bare words become
/// free text. Anything unrecognised is collected rather than dropped, so the UI can tell the user
/// their filter did nothing instead of quietly returning the wrong results.
/// </summary>
public static class SearchQueryParser
{
    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SearchQuery.Empty;
        }

        var freeText = ImmutableArray.CreateBuilder<string>();
        var keywords = ImmutableArray.CreateBuilder<string>();
        var cameras = ImmutableArray.CreateBuilder<string>();
        var lenses = ImmutableArray.CreateBuilder<string>();
        var unrecognised = ImmutableArray.CreateBuilder<string>();

        MediaType? mediaType = null;
        RatingFilter? rating = null;
        DateFilter? captureDate = null;

        foreach (var token in Tokenize(text))
        {
            if (string.Equals(token, "AND", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = token.IndexOf(':');
            if (separator <= 0)
            {
                freeText.Add(Unquote(token));
                continue;
            }

            var field = token[..separator].ToLowerInvariant();
            var value = Unquote(token[(separator + 1)..]);

            if (string.IsNullOrWhiteSpace(value))
            {
                unrecognised.Add(token);
                continue;
            }

            switch (field)
            {
                case "keyword" or "kw":
                    keywords.Add(value);
                    break;

                case "camera":
                    cameras.Add(value);
                    break;

                case "lens":
                    lenses.Add(value);
                    break;

                case "type":
                    if (TryParseMediaType(value, out var parsedType))
                    {
                        mediaType = parsedType;
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                case "rating":
                    if (TryParseRating(value, out var parsedRating))
                    {
                        rating = parsedRating;
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                case "date":
                    if (TryParseDate(value, out var parsedDate))
                    {
                        captureDate = parsedDate;
                    }
                    else
                    {
                        unrecognised.Add(token);
                    }

                    break;

                default:
                    // An unknown field is treated as free text rather than discarded — someone
                    // searching for "http://example.com" should still get results.
                    freeText.Add(Unquote(token));
                    break;
            }
        }

        return new SearchQuery
        {
            FreeText = freeText.ToImmutable(),
            Keywords = keywords.ToImmutable(),
            Cameras = cameras.ToImmutable(),
            Lenses = lenses.ToImmutable(),
            MediaType = mediaType,
            Rating = rating,
            CaptureDate = captureDate,
            UnrecognisedTerms = unrecognised.ToImmutable()
        };
    }

    /// <summary>
    /// Splits on whitespace but keeps quoted runs together, so <c>lens:"RF 100-500"</c> survives as
    /// one token.
    /// </summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in text)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                current.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')
            ? value[1..^1]
            : value.Replace("\"", string.Empty);

    private static bool TryParseMediaType(string value, out MediaType mediaType)
    {
        switch (value.ToLowerInvariant())
        {
            case "video" or "videos" or "movie":
                mediaType = Models.MediaType.Video;
                return true;

            case "image" or "images" or "photo" or "photos" or "still":
                mediaType = Models.MediaType.Image;
                return true;

            default:
                mediaType = Models.MediaType.Unsupported;
                return false;
        }
    }

    private static bool TryParseRating(string value, out RatingFilter? filter)
    {
        var (op, remainder) = SplitOperator(value);

        if (int.TryParse(remainder, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is >= 0 and <= 5)
        {
            filter = new RatingFilter(op, parsed);
            return true;
        }

        filter = null;
        return false;
    }

    private static bool TryParseDate(string value, out DateFilter? filter)
    {
        var (op, remainder) = SplitOperator(value);

        // A bare year means the whole year, which is what someone typing "date:2024" expects.
        if (remainder.Length == 4 &&
            int.TryParse(remainder, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) &&
            year is > 1800 and < 3000)
        {
            filter = op == ComparisonOperator.Equal
                ? new DateFilter(ComparisonOperator.GreaterThanOrEqual, new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero))
                : new DateFilter(op, new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero));
            return true;
        }

        if (DateTimeOffset.TryParse(remainder, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            filter = new DateFilter(op, parsed);
            return true;
        }

        filter = null;
        return false;
    }

    private static (ComparisonOperator Operator, string Remainder) SplitOperator(string value)
    {
        if (value.StartsWith(">=", StringComparison.Ordinal))
        {
            return (ComparisonOperator.GreaterThanOrEqual, value[2..].Trim());
        }

        if (value.StartsWith("<=", StringComparison.Ordinal))
        {
            return (ComparisonOperator.LessThanOrEqual, value[2..].Trim());
        }

        if (value.StartsWith('>'))
        {
            return (ComparisonOperator.GreaterThan, value[1..].Trim());
        }

        if (value.StartsWith('<'))
        {
            return (ComparisonOperator.LessThan, value[1..].Trim());
        }

        return (ComparisonOperator.Equal, value.Trim());
    }
}
