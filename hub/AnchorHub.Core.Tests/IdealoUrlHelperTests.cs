using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class IdealoUrlHelperTests
{
    [Theory]
    [InlineData(
        "https://www.idealo.de/preisvergleich/OffersOfProduct/202837261_-mx-anywhere-3s-grafit-logitech.html",
        "mx anywhere 3s grafit logitech")]
    [InlineData(
        "https://www.idealo.de/preisvergleich/OffersOfProduct/12345_simple-name.html",
        "simple name")]
    [InlineData(
        "https://www.idealo.at/preisvergleich/OffersOfProduct/999_-foo-bar.html",
        "foo bar")]
    public void ExtractProductName_KnownProduct(string raw, string expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, IdealoUrlHelper.ExtractProductName(url!));
    }

    [Theory]
    [InlineData("https://www.idealo.de/")]                                          // root
    [InlineData("https://www.idealo.de/preisvergleich/MainSearchProductCategory.html?q=foo")] // search page
    [InlineData("https://www.idealo.de/preisvergleich/Hersteller/logitech.html")]   // manufacturer
    [InlineData("https://example.com/preisvergleich/OffersOfProduct/1_-foo.html")]  // wrong host
    public void ExtractProductName_NullForUnsupportedShape(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(IdealoUrlHelper.ExtractProductName(url!));
    }
}
