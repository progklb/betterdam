using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// The search vocabulary, and the guarantee that what the interface offers is what the parser
/// accepts. These are the tests that stop the help and the parser drifting apart.
/// </summary>
public class SearchFieldTests
{
    /// <summary>
    /// Every advertised field must actually filter. Colour label was in the first draft of the
    /// catalogue and is not in <c>SearchQuery</c> at all — this is the test that caught it, and it
    /// is the whole reason the catalogue is worth having.
    /// </summary>
    [Fact]
    public void EveryFieldsExampleActuallyFilters()
    {
        Assert.All(SearchFields.All, field =>
        {
            var query = SearchQueryParser.Parse(field.Example);

            Assert.True(query.UnrecognisedTerms.IsDefaultOrEmpty,
                $"{field.Name}: the example '{field.Example}' was not understood.");

            Assert.False(query.IsEmpty, $"{field.Name}: the example '{field.Example}' filtered nothing.");

            // A field whose example lands in free text is not filtering by that field at all — it
            // would still "work", and would quietly match the wrong rows.
            Assert.True(query.FreeText.IsDefaultOrEmpty,
                $"{field.Name}: the example '{field.Example}' fell through to free text.");
        });
    }

    /// <summary>Every spelling the catalogue claims has to resolve to its own field.</summary>
    [Fact]
    public void EverySpellingResolvesToItsField()
    {
        Assert.All(SearchFields.All, field =>
            Assert.All(field.AllSpellings, spelling =>
                Assert.Equal(field.Name, SearchFields.Resolve(spelling))));
    }

    [Fact]
    public void SpellingsAreCaseInsensitive()
    {
        Assert.Equal("keyword", SearchFields.Resolve("KEYWORD"));
        Assert.Equal("keyword", SearchFields.Resolve("K"));
    }

    /// <summary>
    /// The short forms are the point of the exercise, so they are asserted by name rather than only
    /// through the catalogue — a typo that swapped two of them would otherwise pass every other test.
    /// </summary>
    [Theory]
    [InlineData("k", "keyword")]
    [InlineData("r", "rating")]
    [InlineData("t", "type")]
    [InlineData("c", "camera")]
    [InlineData("l", "lens")]
    [InlineData("d", "date")]
    public void ShortFormsMeanWhatTheyLookLike(string shortForm, string expected)
    {
        Assert.Equal(expected, SearchFields.Resolve(shortForm));
    }

    [Fact]
    public void ShortAndLongFormsProduceTheSameQuery()
    {
        var spelledOut = SearchQueryParser.Parse("keyword:motorcycle rating:>=4 type:video");
        var shortForm = SearchQueryParser.Parse("k:motorcycle r:>=4 t:video");

        // Compared part by part rather than as whole records: SearchQuery holds ImmutableArrays,
        // whose equality is by underlying reference, so two identical queries are never Equal.
        // ToArray on both sides: xUnit compares an ImmutableArray against a string[] as different
        // collection types and reports "Collections differ" over two identical lists.
        Assert.Equal(spelledOut.Keywords.Select(k => k.AnyOf.ToArray()).ToArray(),
            shortForm.Keywords.Select(k => k.AnyOf.ToArray()).ToArray());
        Assert.Equal(spelledOut.Rating, shortForm.Rating);
        Assert.Equal(spelledOut.Kinds.ToArray(), shortForm.Kinds.ToArray());
        Assert.Equal(spelledOut.FreeText.ToArray(), shortForm.FreeText.ToArray());
    }

    /// <summary>The alias that shipped before the short forms existed still has to work.</summary>
    [Fact]
    public void TheOlderKeywordAliasStillWorks()
    {
        Assert.Equal([["sand"]], SearchQueryParser.Parse("kw:sand").Keywords.Select(k => k.AnyOf.ToArray()).ToArray());
    }

    [Fact]
    public void SomethingThatIsNotAFieldResolvesToNothing()
    {
        Assert.Null(SearchFields.Resolve("nonsense"));
        Assert.Null(SearchFields.Resolve("http"));
    }

    /// <summary>
    /// A word that only looks like a field must stay free text, so pasting a URL into the box still
    /// searches for it.
    /// </summary>
    [Fact]
    public void AnUnknownFieldIsSearchedForAsText()
    {
        var query = SearchQueryParser.Parse("http://example.com/photo");

        Assert.Contains("http://example.com/photo", query.FreeText);
    }

    [Fact]
    public void ABareColonOffersEverything()
    {
        Assert.Equal(SearchFields.All.Length, SearchFields.Matching(string.Empty).Count());
        Assert.Equal(SearchFields.All.Length, SearchFields.Matching(null).Count());
    }

    [Fact]
    public void APrefixNarrowsTheOffer()
    {
        var matches = SearchFields.Matching("k").ToList();

        Assert.Single(matches);
        Assert.Equal("keyword", matches[0].Name);
    }

    /// <summary>
    /// Two fields claiming one spelling would make a search filter by the wrong thing, so the
    /// catalogue refuses to build. Touching it is enough to run this.
    /// </summary>
    [Fact]
    public void TheCatalogueHasNoClashingSpellings()
    {
        var spellings = SearchFields.All.SelectMany(field => field.AllSpellings).ToList();

        Assert.Equal(spellings.Count, spellings.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

/// <summary>
/// When the search box offers the field list, and what happens to the text when one is picked.
/// </summary>
public class SearchSuggestionTests
{
    [Theory]
    [InlineData(":", 1, "")]                       // a bare colon means "remind me"
    [InlineData("k:", 2, "k")]                     // a partly typed field
    [InlineData("key:", 4, "key")]
    [InlineData("sand k:", 7, "k")]                // the token being typed, not the whole box
    public void OffersCompletionsAtAColon(string text, int caret, string expected)
    {
        Assert.Equal(expected, SearchSuggestion.PrefixAt(text, caret));
    }

    [Theory]
    [InlineData("", 0)]                            // nothing typed
    [InlineData("sand", 4)]                        // free text
    [InlineData("k:motorcycle", 12)]               // a value is being typed, so the asking is over
    [InlineData("lens:RF:", 8)]                    // a colon inside a value must not reopen it
    public void StaysQuietOtherwise(string text, int caret)
    {
        Assert.Null(SearchSuggestion.PrefixAt(text, caret));
    }

    /// <summary>The caret is rarely at the end; a colon typed mid-string still offers.</summary>
    [Fact]
    public void OffersWhereTheCaretIsRatherThanAtTheEnd()
    {
        Assert.Equal("k", SearchSuggestion.PrefixAt("k: sand", 2));
        Assert.Null(SearchSuggestion.PrefixAt("k: sand", 7));
    }

    [Fact]
    public void AcceptingReplacesTheHalfTypedField()
    {
        var (text, caret) = SearchSuggestion.Accept("k:", 2, "keyword");

        Assert.Equal("keyword:", text);
        Assert.Equal(8, caret);
    }

    [Fact]
    public void AcceptingLeavesTheRestOfTheQueryAlone()
    {
        var (text, caret) = SearchSuggestion.Accept("sand r:", 7, "rating");

        Assert.Equal("sand rating:", text);
        Assert.Equal(12, caret);
    }

    /// <summary>What follows the caret survives, so completing mid-query does not truncate it.</summary>
    [Fact]
    public void AcceptingKeepsWhatComesAfterTheCaret()
    {
        var (text, caret) = SearchSuggestion.Accept("k: t:video", 2, "keyword");

        Assert.Equal("keyword: t:video", text);
        Assert.Equal(8, caret);
    }

    [Fact]
    public void AcceptingIntoAnEmptyBoxJustWritesTheField()
    {
        var (text, caret) = SearchSuggestion.Accept(":", 1, "type");

        Assert.Equal("type:", text);
        Assert.Equal(5, caret);
    }
}

/// <summary>
/// How several values combine, which was the thing nobody could tell from the interface.
/// </summary>
public class SearchCombinationTests
{
    /// <summary>Repeating a field asks for all of them.</summary>
    [Fact]
    public void RepeatingKeywordMeansAllOfThem()
    {
        var query = SearchQueryParser.Parse("k:sand k:dust");

        Assert.Equal(2, query.Keywords.Length);
        Assert.Equal(["sand"], query.Keywords[0].AnyOf.ToArray());
        Assert.Equal(["dust"], query.Keywords[1].AnyOf.ToArray());
    }

    /// <summary>
    /// A comma asks for any of them. Before this, "k:sand,dust" looked for a single keyword
    /// literally named "sand,dust" and therefore matched nothing at all — valid-looking and silent.
    /// </summary>
    [Fact]
    public void CommaInKeywordMeansAnyOfThem()
    {
        var query = SearchQueryParser.Parse("k:sand,dust");

        Assert.Single(query.Keywords);
        Assert.Equal(["sand", "dust"], query.Keywords[0].AnyOf.ToArray());
    }

    [Fact]
    public void CommaInTypeMeansAnyOfThem()
    {
        Assert.Equal(
            [MediaKind.Raw, MediaKind.Video],
            SearchQueryParser.Parse("t:raw,video").Kinds.ToArray());
    }

    /// <summary>
    /// The question that prompted all this: do rating, keyword and type combine? They do, and every
    /// field narrows the result together with the others.
    /// </summary>
    [Fact]
    public void RatingKeywordAndTypeCombine()
    {
        var query = SearchQueryParser.Parse("r:>=4 k:sand,dust k:wide t:raw c:Fujifilm");

        Assert.Equal(4, query.Rating!.Value);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, query.Rating.Operator);
        Assert.Equal([MediaKind.Raw], query.Kinds.ToArray());
        Assert.Equal(["Fujifilm"], query.Cameras.ToArray());

        Assert.Equal(2, query.Keywords.Length);
        Assert.Equal(["sand", "dust"], query.Keywords[0].AnyOf.ToArray());
        Assert.Equal(["wide"], query.Keywords[1].AnyOf.ToArray());

        Assert.Empty(query.UnrecognisedTerms);
        Assert.Empty(query.FreeText);
    }
}
