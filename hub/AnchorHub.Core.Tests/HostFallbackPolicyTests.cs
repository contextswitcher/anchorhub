using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class HostFallbackPolicyTests
{
    [Theory]
    [InlineData("https://www.openstreetmap.org/way/12345")]
    [InlineData("https://openstreetmap.org/node/678")]
    [InlineData("https://openstreetmap.org/")]
    public void ShouldSkipHostFallback_OpenStreetMap(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(HostFallbackPolicy.ShouldSkipHostFallback(url!));
    }

    [Theory]
    [InlineData("https://learning.oreilly.com/library/view/learning-git/9781098133900/")]
    [InlineData("https://learning.oreilly.com/")]
    public void ShouldSkipHostFallback_OReillyLearning(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(HostFallbackPolicy.ShouldSkipHostFallback(url!));
    }

    [Theory]
    [InlineData("https://aeondesktop.github.io/")]
    [InlineData("https://octocat.github.io/some-project/")]
    [InlineData("https://github.io/")]
    public void ShouldSkipHostFallback_GitHubPages(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(HostFallbackPolicy.ShouldSkipHostFallback(url!));
    }

    [Theory]
    [InlineData("https://blog.jetbrains.com/rust/2026/08/10/rewriting-in-rust/", true)]
    [InlineData("https://blogs.oracle.com/java/post/some-post", true)]
    [InlineData("https://www.blog.example.org/post", true)]
    [InlineData("https://someone.blogspot.com/2026/01/post.html", true)]
    [InlineData("https://someone.wordpress.com/2026/01/02/post/", true)]
    [InlineData("https://wordpress.com/", true)]
    [InlineData("https://myblog.example.org/post", false)]
    [InlineData("https://notwordpress.com/post", false)]
    public void ShouldSkipHostFallback_Blogs(string raw, bool expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, HostFallbackPolicy.ShouldSkipHostFallback(url!));
    }

    [Theory]
    [InlineData("https://mail.google.com/mail/u/0/#inbox")]
    [InlineData("https://mail.google.com/mail/u/0/")]
    [InlineData("https://calendar.google.com/calendar/u/0/r")]
    public void IsNoIdentityHost_WebAppShells(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(HostFallbackPolicy.IsNoIdentityHost(url!));
        // Web-app shells also imply host-fallback skip — they should never
        // reach pass 2 either.
        Assert.True(HostFallbackPolicy.ShouldSkipHostFallback(url!));
    }

    [Theory]
    [InlineData("https://search.crossref.org/?from_ui=&q=distributed+business+process+execution")]
    [InlineData("https://search.crossref.org/")]
    public void IsSearchEngineHost_CrossrefSearch(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(HostFallbackPolicy.IsSearchEngineHost(url!));
        Assert.True(HostFallbackPolicy.ShouldSkipHostFallback(url!));
    }

    [Theory]
    [InlineData("https://www.crossref.org/documentation/")]
    [InlineData("https://api.crossref.org/works/10.1000/xyz")]
    public void IsSearchEngineHost_FalseForOtherCrossrefHosts(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.False(HostFallbackPolicy.IsSearchEngineHost(url!));
    }

    [Theory]
    [InlineData("https://example.com/foo")]
    [InlineData("https://github.com/octocat/anchorhub")]
    [InlineData("https://docs.google.com/document/d/abc/edit")]
    public void IsNoIdentityHost_FalseForNormalHosts(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.False(HostFallbackPolicy.IsNoIdentityHost(url!));
    }
}
