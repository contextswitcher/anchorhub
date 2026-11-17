using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class IeeeFulltextAdapterTests
{
    private static readonly IeeeFulltextAdapter Adapter = new();

    [Theory]
    [InlineData("https://ieeexplore.ieee.org/document/10589877")]
    [InlineData("https://ieeexplore.ieee.org/document/10589877/")]
    [InlineData("https://www.ieeexplore.ieee.org/document/12345")]
    public void Matches_DocumentPages(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(Adapter.Matches(url!));
    }

    [Theory]
    [InlineData("https://ieeexplore.ieee.org/")]
    [InlineData("https://ieeexplore.ieee.org/Xplore/home.jsp")]
    [InlineData("https://ieeexplore.ieee.org/document/")]
    [InlineData("https://ieeexplore.ieee.org/document/abc")]
    [InlineData("https://ieeexplore.ieee.org/author/37086420877")]
    [InlineData("https://example.com/document/10589877")]
    public void DoesNotMatch_NonDocumentPages(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.False(Adapter.Matches(url!));
    }

    [Fact]
    public void PublisherKey_IsCanonicalHost()
    {
        var url = UrlNormalizer.TryNormalize("https://www.ieeexplore.ieee.org/document/10589877");
        Assert.NotNull(url);
        Assert.Equal("ieeexplore.ieee.org", Adapter.PublisherKey(url!));
    }

    [Fact]
    public void DocumentUrl_ReturnsNormalisedInput()
    {
        var url = UrlNormalizer.TryNormalize("https://ieeexplore.ieee.org/document/10589877/");
        Assert.NotNull(url);
        Assert.Equal("https://ieeexplore.ieee.org/document/10589877", Adapter.DocumentUrl(url!));
    }

    [Fact]
    public void ContentScriptName_IsIeee()
    {
        Assert.Equal("ieee", Adapter.ContentScriptName);
    }
}
