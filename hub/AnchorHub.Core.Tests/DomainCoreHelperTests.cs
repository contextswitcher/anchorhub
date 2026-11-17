using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class DomainCoreHelperTests
{
    [Theory]
    [InlineData("slipshow.org",         "slipshow")]
    [InlineData("docs.slipshow.org",    "slipshow")]
    [InlineData("www.slipshow.org",     "slipshow")]
    [InlineData("blog.docs.example.io", "example")]
    [InlineData("example.com",          "example")]
    public void ExtractDomainCore_KnownPatterns(string host, string expected)
    {
        Assert.Equal(expected, DomainCoreHelper.ExtractDomainCore(host));
    }

    [Theory]
    [InlineData("")]                   // empty
    [InlineData("localhost")]          // no dot
    [InlineData("a.b")]                // core "a" too short
    [InlineData("foo.co.uk")]          // compound TLD: core would be "co" (filtered)
    [InlineData("foo.com.au")]         // compound TLD: core would be "com" (filtered)
    [InlineData("subdomain.foo.co.uk")]// compound TLD: core would be "co"
    public void ExtractDomainCore_ReturnsNull(string host)
    {
        Assert.Null(DomainCoreHelper.ExtractDomainCore(host));
    }
}
