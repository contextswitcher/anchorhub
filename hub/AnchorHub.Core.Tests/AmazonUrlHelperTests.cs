using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class AmazonUrlHelperTests
{
    [Theory]
    [InlineData(
        "https://www.amazon.com/Production-Workflow-Techniques-Frank-Leymann/dp/0130217530",
        "Production Workflow Techniques Frank Leymann")]
    [InlineData(
        "https://www.amazon.com/Production-Workflow-Techniques-Frank-Leymann/dp/0130217530/ref=foo",
        "Production Workflow Techniques Frank Leymann")]
    [InlineData(
        "https://www.amazon.de/Programmieren-lernen-Stefan-Mueller/dp/3527715432/",
        "Programmieren lernen Stefan Mueller")]
    [InlineData(
        "https://amazon.co.uk/Some-Book-Title/dp/1234567890",
        "Some Book Title")]
    public void ExtractProductTitle_KnownPatterns(string raw, string expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, AmazonUrlHelper.ExtractProductTitle(url!));
    }

    [Theory]
    [InlineData("https://www.amazon.com/")]                         // root
    [InlineData("https://www.amazon.com/dp/0130217530")]            // no title slug before /dp/
    [InlineData("https://www.amazon.com/Some-Title/")]              // no /dp/
    [InlineData("https://example.com/Some-Title/dp/0130217530")]    // wrong host
    [InlineData("https://amazonaws.com/foo/dp/bar")]                // amazonaws ≠ amazon
    public void ExtractProductTitle_ReturnsNull(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(AmazonUrlHelper.ExtractProductTitle(url!));
    }
}
