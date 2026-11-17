using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class GoogleBooksUrlHelperTests
{
    [Theory]
    [InlineData("https://books.google.de/books?id=vJ8pAQAAMAAJ",             "vJ8pAQAAMAAJ")]
    [InlineData("https://books.google.com/books?id=vJ8pAQAAMAAJ&hl=en",      "vJ8pAQAAMAAJ")]
    [InlineData("https://books.google.de/books?hl=de&id=vJ8pAQAAMAAJ&dq=x",  "vJ8pAQAAMAAJ")]
    [InlineData("HTTPS://Books.Google.COM/books?id=vJ8pAQAAMAAJ",            "vJ8pAQAAMAAJ")]
    public void CanonicalBookTerm_VolumeUrl_ReturnsIdAcrossLocales(string raw, string expected)
    {
        Assert.Equal(expected, GoogleBooksUrlHelper.CanonicalBookTerm(raw));
    }

    [Theory]
    [InlineData("https://books.google.de/books")]                    // no id=
    [InlineData("https://books.google.com/books?hl=en")]             // id= missing, hl present
    [InlineData("https://books.google.com/")]                        // root, no /books
    [InlineData("https://www.google.com/books?id=vJ8pAQAAMAAJ")]     // wrong subdomain
    [InlineData("https://example.com/books?id=vJ8pAQAAMAAJ")]        // wrong host
    [InlineData("ftp://books.google.com/books?id=vJ8pAQAAMAAJ")]     // wrong scheme
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void CanonicalBookTerm_NonVolumeOrInvalid_ReturnsNull(string? raw)
    {
        Assert.Null(GoogleBooksUrlHelper.CanonicalBookTerm(raw));
    }

    [Theory]
    [InlineData("https://books.google.de/books")]                    // bare /books path — the false-match case
    [InlineData("https://books.google.com/books?hl=en&q=quality")]   // search under /books, no id
    [InlineData("https://books.google.com/")]                        // root
    [InlineData("https://books.google.de/books/about")]              // about page, no id
    public void IsNoIdentityUrl_BooksWithoutId_True(string raw)
    {
        Assert.True(GoogleBooksUrlHelper.IsNoIdentityUrl(raw));
    }

    [Theory]
    [InlineData("https://books.google.de/books?id=vJ8pAQAAMAAJ")]    // volume URL has identity
    [InlineData("https://books.google.com/books?id=vJ8pAQAAMAAJ&hl=en")]
    [InlineData("https://scholar.google.com/citations?user=KDWpQIwAAAAJ")] // non-Books host
    [InlineData("https://example.com/foo")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNoIdentityUrl_VolumeOrUnrelated_False(string? raw)
    {
        Assert.False(GoogleBooksUrlHelper.IsNoIdentityUrl(raw));
    }
}
