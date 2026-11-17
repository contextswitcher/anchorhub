using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class LinkedInUrlHelperTests
{
    [Theory]
    [InlineData("https://www.linkedin.com/in/julia-hertel-2014a81ab/",  "Julia Hertel")]
    [InlineData("https://linkedin.com/in/julia-hertel/",                "Julia Hertel")]
    [InlineData("https://www.linkedin.com/in/john-doe-jr-1234abc",      "John Doe Jr")]
    public void ExtractPersonName_KnownProfile(string raw, string expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, LinkedInUrlHelper.ExtractPersonName(url!));
    }

    [Theory]
    [InlineData("https://www.linkedin.com/")]                                   // root
    [InlineData("https://www.linkedin.com/company/tragai/")]                    // not /in/
    [InlineData("https://example.com/in/foo")]                                  // wrong host
    public void ExtractPersonName_ReturnsNullForNonProfile(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(LinkedInUrlHelper.ExtractPersonName(url!));
    }

    [Theory]
    [InlineData("https://www.linkedin.com/company/tragai/posts/?feedView=all",
                "https://www.linkedin.com/company/tragai", "tragai")]
    [InlineData("https://www.linkedin.com/company/tragai/",
                "https://www.linkedin.com/company/tragai", "tragai")]
    [InlineData("https://linkedin.com/company/anthropic",
                "https://www.linkedin.com/company/anthropic", "anthropic")]
    public void ExtractCompanyTerms_KnownCompany(string raw, string expectedUrl, string expectedSlug)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        var terms = LinkedInUrlHelper.ExtractCompanyTerms(url!);
        Assert.NotNull(terms);
        Assert.Equal(expectedUrl,  terms!.Value.CompanyUrl);
        Assert.Equal(expectedSlug, terms.Value.Slug);
    }

    [Theory]
    [InlineData("https://www.linkedin.com/in/julia-hertel/")]   // person, not company
    [InlineData("https://www.linkedin.com/company/")]           // /company/ with no slug
    [InlineData("https://www.linkedin.com/jobs/view/123")]      // jobs, not company
    [InlineData("https://example.com/company/foo")]             // wrong host
    public void ExtractCompanyTerms_ReturnsNullForNonCompany(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(LinkedInUrlHelper.ExtractCompanyTerms(url!));
    }
}
