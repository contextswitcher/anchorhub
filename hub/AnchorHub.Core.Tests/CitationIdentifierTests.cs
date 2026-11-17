using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class CitationIdentifierTests
{
    [Theory]
    [InlineData("doi:10.1000/foo",            "doi",   "10.1000/foo")]
    [InlineData("DOI:10.1000/foo",            "doi",   "10.1000/foo")]
    [InlineData("arxiv:2311.01302",           "arxiv", "2311.01302")]
    [InlineData("isbn:978-3-16-148410-0",     "isbn",  "978-3-16-148410-0")]
    [InlineData("url:https://example.com/x",  "url",   "https://example.com/x")]
    [InlineData("  doi : 10.1/x  ",           "doi",   "10.1/x")]
    public void TryParse_Valid(string raw, string expectedScheme, string expectedValue)
    {
        Assert.True(CitationIdentifier.TryParse(raw, out var id, out var err));
        Assert.NotNull(id);
        Assert.Equal(expectedScheme, id!.Scheme);
        Assert.Equal(expectedValue, id.Value);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("nope:foo")]      // unknown scheme
    [InlineData("doi:")]          // empty value
    [InlineData(":foo")]          // empty scheme
    [InlineData("no-colon-here")] // no separator
    [InlineData("")]              // empty
    [InlineData("   ")]           // whitespace only
    public void TryParse_Invalid(string raw)
    {
        Assert.False(CitationIdentifier.TryParse(raw, out var id, out var err));
        Assert.Null(id);
        Assert.NotNull(err);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        Assert.True(CitationIdentifier.TryParse("doi:10.1/x", out var id, out _));
        Assert.Equal("doi:10.1/x", id!.ToString());
    }
}
