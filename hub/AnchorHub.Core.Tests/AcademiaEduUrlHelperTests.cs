using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class AcademiaEduUrlHelperTests
{
    [Theory]
    [InlineData("https://aguapedia.academia.edu/FranciscoBenitez",       "Francisco Benitez")]
    [InlineData("https://harvard.academia.edu/JaneSmith",                "Jane Smith")]
    [InlineData("https://www.academia.edu/JohnFitzgeraldKennedy",        "John Fitzgerald Kennedy")]
    public void ExtractPersonName_KnownProfile(string raw, string expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, AcademiaEduUrlHelper.ExtractPersonName(url!));
    }

    [Theory]
    [InlineData("https://aguapedia.academia.edu/")]                         // root
    [InlineData("https://aguapedia.academia.edu/Documents/foo")]            // multi-segment path
    [InlineData("https://aguapedia.academia.edu/SEARCH")]                   // all-uppercase, not a CamelCase name
    [InlineData("https://example.com/FranciscoBenitez")]                    // wrong host
    public void ExtractPersonName_NullForUnsupportedShape(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(AcademiaEduUrlHelper.ExtractPersonName(url!));
    }
}
