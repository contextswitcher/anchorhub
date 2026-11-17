using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class FulltextAdaptersTests
{
    [Fact]
    public void Pick_ReturnsIeeeAdapter_ForIeeeDocument()
    {
        var url = UrlNormalizer.TryNormalize("https://ieeexplore.ieee.org/document/10589877");
        Assert.NotNull(url);
        var adapter = FulltextAdapters.Pick(url!);
        Assert.IsType<IeeeFulltextAdapter>(adapter);
    }

    [Fact]
    public void Pick_ReturnsGenericFallback_ForUnknownHost()
    {
        var url = UrlNormalizer.TryNormalize("https://example.com/papers/foo.pdf");
        Assert.NotNull(url);
        var adapter = FulltextAdapters.Pick(url!);
        Assert.IsType<GenericFulltextAdapter>(adapter);
    }

    [Fact]
    public void Pick_ReturnsGenericFallback_ForIeeeNonDocumentPage()
    {
        var url = UrlNormalizer.TryNormalize("https://ieeexplore.ieee.org/author/37086420877");
        Assert.NotNull(url);
        var adapter = FulltextAdapters.Pick(url!);
        Assert.IsType<GenericFulltextAdapter>(adapter);
    }

    [Fact]
    public void HasPublisherAdapter_True_ForIeeeDocument()
    {
        var url = UrlNormalizer.TryNormalize("https://ieeexplore.ieee.org/document/10589877");
        Assert.NotNull(url);
        Assert.True(FulltextAdapters.HasPublisherAdapter(url!));
    }

    [Fact]
    public void HasPublisherAdapter_False_ForUnknownHost()
    {
        var url = UrlNormalizer.TryNormalize("https://example.com/papers/foo.pdf");
        Assert.NotNull(url);
        Assert.False(FulltextAdapters.HasPublisherAdapter(url!));
    }

    [Fact]
    public void GenericAdapter_Matches_Everything()
    {
        var generic = new GenericFulltextAdapter();
        var url1 = UrlNormalizer.TryNormalize("https://example.com/foo");
        var url2 = UrlNormalizer.TryNormalize("https://ieeexplore.ieee.org/document/10589877");
        Assert.NotNull(url1);
        Assert.NotNull(url2);
        Assert.True(generic.Matches(url1!));
        Assert.True(generic.Matches(url2!));
    }

    [Fact]
    public void GenericAdapter_PublisherKey_IsHost()
    {
        var generic = new GenericFulltextAdapter();
        var url = UrlNormalizer.TryNormalize("https://link.springer.com/article/10.1007/foo");
        Assert.NotNull(url);
        Assert.Equal("link.springer.com", generic.PublisherKey(url!));
    }
}
