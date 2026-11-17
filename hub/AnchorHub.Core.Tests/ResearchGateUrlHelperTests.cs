using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class ResearchGateUrlHelperTests
{
    [Theory]
    [InlineData("https://www.researchgate.net/profile/David-Benavides-7",       "David Benavides")]
    [InlineData("https://www.researchgate.net/profile/David-Benavides",         "David Benavides")]
    [InlineData("https://researchgate.net/profile/Mary-Anne-Smith-12",          "Mary Anne Smith")]
    [InlineData("https://www.researchgate.net/profile/Foo-2",                   "Foo")]
    public void ExtractPersonName_KnownProfile(string raw, string expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, ResearchGateUrlHelper.ExtractPersonName(url!));
    }

    [Theory]
    [InlineData("https://www.researchgate.net/")]                                       // root
    [InlineData("https://www.researchgate.net/lab/Diverso-Lab-International-Computing")] // lab page
    [InlineData("https://www.researchgate.net/publication/12345_Foo")]                  // publication
    [InlineData("https://example.com/profile/David-Benavides-7")]                       // wrong host
    public void ExtractPersonName_NullForUnsupportedShape(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(ResearchGateUrlHelper.ExtractPersonName(url!));
    }
}
