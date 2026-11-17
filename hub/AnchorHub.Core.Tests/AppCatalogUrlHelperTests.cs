using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class AppCatalogUrlHelperTests
{
    [Theory]
    [InlineData("https://alternativeto.net/software/logseq/",       "logseq")]
    [InlineData("https://alternativeto.net/software/logseq",        "logseq")]
    [InlineData("https://www.alternativeto.net/software/obsidian/", "obsidian")]
    [InlineData("https://noteapps.info/apps/logseq",                "logseq")]
    [InlineData("https://noteapps.info/apps/notion/",               "notion")]
    [InlineData("https://addons.mozilla.org/en-GB/firefox/addon/web-highlights-pdf-web-highlig/", "web-highlights-pdf-web-highlig")]
    [InlineData("https://addons.mozilla.org/de/firefox/addon/ublock-origin/", "ublock-origin")]
    public void ExtractAppSlug_KnownCatalog(string raw, string expectedSlug)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expectedSlug, AppCatalogUrlHelper.ExtractAppSlug(url!));
    }

    [Theory]
    [InlineData("https://alternativeto.net/")]                // root
    [InlineData("https://alternativeto.net/news/2024-01-01")] // wrong marker segment
    [InlineData("https://noteapps.info/about")]               // wrong marker segment
    [InlineData("https://capterra.com/software/logseq/")]     // unknown catalog
    [InlineData("https://example.com/software/logseq")]       // unknown catalog
    [InlineData("https://addons.mozilla.org/en-GB/firefox/")] // marker not before last segment
    public void ExtractAppSlug_ReturnsNull(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(AppCatalogUrlHelper.ExtractAppSlug(url!));
    }
}
