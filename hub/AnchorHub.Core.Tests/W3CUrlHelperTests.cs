using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class W3CUrlHelperTests
{
    [Theory]
    [InlineData("https://www.w3.org/OWL/",                "OWL")]
    [InlineData("https://www.w3.org/RDF/",                "RDF")]
    [InlineData("https://w3.org/SPARQL/",                 "SPARQL")]
    [InlineData("https://www.w3.org/TR/owl2-overview/",   "owl2-overview")]
    [InlineData("https://www.w3.org/TR/sparql11-query",   "sparql11-query")]
    public void ExtractSpecName_KnownPatterns(string raw, string expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, W3CUrlHelper.ExtractSpecName(url!));
    }

    [Theory]
    [InlineData("https://www.w3.org/")]                // root
    [InlineData("https://example.com/OWL/")]           // wrong host
    public void ExtractSpecName_ReturnsNull(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(W3CUrlHelper.ExtractSpecName(url!));
    }
}
