using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class WikipediaUrlHelperTests
{
    [Theory]
    [InlineData("https://de.wikipedia.org/wiki/Barcamp",            "Barcamp")]
    [InlineData("https://en.wikipedia.org/wiki/Albert_Einstein",    "Albert Einstein")]
    [InlineData("https://en.wikipedia.org/wiki/Caf%C3%A9",          "Café")]
    [InlineData("https://simple.wikipedia.org/wiki/Computer",       "Computer")]
    [InlineData("https://wikipedia.org/wiki/Foo",                   "Foo")]
    public void ExtractArticleTitle_KnownArticle(string raw, string expectedTitle)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expectedTitle, WikipediaUrlHelper.ExtractArticleTitle(url!));
    }

    [Theory]
    [InlineData("https://en.wikipedia.org/")]                       // root
    [InlineData("https://en.wikipedia.org/wiki/")]                  // /wiki/ with no slug
    [InlineData("https://en.wikipedia.org/w/index.php?title=Foo")]  // not /wiki/
    [InlineData("https://en.wikipedia.org/wiki/Special:Search")]    // valid in title slot
    [InlineData("https://example.com/wiki/Foo")]                    // wrong host
    public void ExtractArticleTitle_NullForNonArticleOrUnknownHost(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        var title = WikipediaUrlHelper.ExtractArticleTitle(url!);
        // /wiki/Special:Search returns "Special:Search" — that's still a title-shaped
        // segment, so the helper accepts it. The other rows must return null.
        if (raw.EndsWith("Special:Search"))
            Assert.Equal("Special:Search", title);
        else
            Assert.Null(title);
    }

    [Theory]
    [InlineData("en.wikipedia.org",     true)]
    [InlineData("de.wikipedia.org",     true)]
    [InlineData("simple.wikipedia.org", true)]
    [InlineData("wikipedia.org",        true)]
    [InlineData("wikimedia.org",        false)]
    [InlineData("example.com",          false)]
    public void IsWikipediaHost_RecognisesAllSubdomains(string host, bool expected)
    {
        Assert.Equal(expected, WikipediaUrlHelper.IsWikipediaHost(host));
    }
}
