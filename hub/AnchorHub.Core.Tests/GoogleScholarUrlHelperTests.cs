using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class GoogleScholarUrlHelperTests
{
    [Theory]
    [InlineData("https://scholar.google.com/citations?user=KDWpQIwAAAAJ&hl=en",
                "scholar.google.com/citations?user=KDWpQIwAAAAJ")]
    [InlineData("https://scholar.google.com/citations?user=F02JdKUAAAAJ",
                "scholar.google.com/citations?user=F02JdKUAAAAJ")]
    [InlineData("https://scholar.google.de/citations?hl=de&user=KDWpQIwAAAAJ",
                "scholar.google.com/citations?user=KDWpQIwAAAAJ")]
    [InlineData("https://scholar.google.co.uk/citations?user=KDWpQIwAAAAJ&hl=en",
                "scholar.google.com/citations?user=KDWpQIwAAAAJ")]
    [InlineData("HTTPS://Scholar.Google.COM/citations?user=KDWpQIwAAAAJ",
                "scholar.google.com/citations?user=KDWpQIwAAAAJ")]
    public void CanonicalProfileTerm_ProfileUrl_PreservesUserIdDropsLocale(string raw, string expected)
    {
        Assert.Equal(expected, GoogleScholarUrlHelper.CanonicalProfileTerm(raw));
    }

    [Theory]
    [InlineData("https://scholar.google.com/")]                                  // root
    [InlineData("https://scholar.google.com/citations")]                         // no user= param
    [InlineData("https://scholar.google.com/citations?hl=en")]                   // user= missing, hl present
    [InlineData("https://scholar.google.com/scholar?q=quantum")]                 // search page
    [InlineData("https://scholar.google.com/scholar_lookup?title=Foo")]          // lookup endpoint
    [InlineData("https://www.google.com/citations?user=KDWpQIwAAAAJ")]           // wrong subdomain
    [InlineData("https://example.com/citations?user=KDWpQIwAAAAJ")]              // wrong host
    [InlineData("ftp://scholar.google.com/citations?user=KDWpQIwAAAAJ")]         // wrong scheme
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void CanonicalProfileTerm_NonProfileOrInvalid_ReturnsNull(string? raw)
    {
        Assert.Null(GoogleScholarUrlHelper.CanonicalProfileTerm(raw));
    }

    [Theory]
    [InlineData("https://scholar.google.com/scholar?q=quantum")]                  // paper search
    [InlineData("https://scholar.google.de/scholar?cluster=12345")]               // versions cluster
    [InlineData("https://scholar.google.com/citations?view_op=view_citation&user=KDWpQIwAAAAJ&citation_for_view=KDWpQIwAAAAJ:abc")]
    [InlineData("https://scholar.google.com/citations?mauthors=label:software_engineering")]
    [InlineData("https://scholar.google.com/")]                                   // root
    [InlineData("https://scholar.google.com/citations")]                          // no user=
    public void IsNoIdentityUrl_NonProfileScholarPaths_True(string raw)
    {
        Assert.True(GoogleScholarUrlHelper.IsNoIdentityUrl(raw));
    }

    [Theory]
    [InlineData("https://scholar.google.com/citations?user=KDWpQIwAAAAJ")]
    [InlineData("https://scholar.google.com/citations?user=KDWpQIwAAAAJ&hl=en")]
    [InlineData("https://scholar.google.de/citations?hl=de&user=KDWpQIwAAAAJ")]
    [InlineData("https://www.google.com/")]                                      // non-Scholar
    [InlineData("https://example.com/foo")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNoIdentityUrl_ProfileOrUnrelated_False(string? raw)
    {
        Assert.False(GoogleScholarUrlHelper.IsNoIdentityUrl(raw));
    }
}
