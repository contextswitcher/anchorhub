using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class UrlNormalizerTests
{
    // ── Table examples from §6 ───────────────────────────────────────────────

    [Fact]
    public void DesignExample_QueryAndFragment()
    {
        var r = UrlNormalizer.TryNormalize("https://Example.com/Foo/?utm_source=x#bar");
        Assert.NotNull(r);
        Assert.Equal("https://example.com/Foo", r!.Normalized);
        Assert.Equal("example.com", r.Host);
    }

    [Fact]
    public void DesignExample_DefaultHttpPort()
    {
        var r = UrlNormalizer.TryNormalize("http://example.com:80/");
        Assert.NotNull(r);
        Assert.Equal("http://example.com/", r!.Normalized);
        Assert.Equal("example.com", r.Host);
    }

    [Fact]
    public void DesignExample_NoPath()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com");
        Assert.NotNull(r);
        Assert.Equal("https://example.com/", r!.Normalized);
        Assert.Equal("example.com", r.Host);
    }

    // ── Host normalisation ────────────────────────────────────────────────────

    [Fact]
    public void Host_LowercasedInNormalized()
    {
        var r = UrlNormalizer.TryNormalize("https://UPPER.EXAMPLE.COM/path");
        Assert.NotNull(r);
        Assert.Equal("upper.example.com", r!.Host);
        Assert.StartsWith("https://upper.example.com", r.Normalized);
    }

    // ── Port handling ────────────────────────────────────────────────────────

    [Fact]
    public void DefaultHttpsPort_Dropped()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com:443/page");
        Assert.NotNull(r);
        Assert.Equal("https://example.com/page", r!.Normalized);
    }

    [Fact]
    public void NonDefaultPort_Retained()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com:8443/page");
        Assert.NotNull(r);
        Assert.Equal("https://example.com:8443/page", r!.Normalized);
    }

    [Fact]
    public void DefaultHttpPort80_Dropped()
    {
        var r = UrlNormalizer.TryNormalize("http://example.com:80/path");
        Assert.NotNull(r);
        Assert.Equal("http://example.com/path", r!.Normalized);
    }

    // ── Path handling ────────────────────────────────────────────────────────

    [Fact]
    public void Path_CasePreserved()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com/CaseSensitive/Path");
        Assert.NotNull(r);
        Assert.Contains("/CaseSensitive/Path", r!.Normalized);
    }

    [Fact]
    public void TrailingSlash_StrippedFromDeepPath()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com/a/b/");
        Assert.NotNull(r);
        Assert.Equal("https://example.com/a/b", r!.Normalized);
    }

    [Fact]
    public void RootPath_SlashPreserved()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com/");
        Assert.NotNull(r);
        Assert.Equal("https://example.com/", r!.Normalized);
    }

    // ── Fragment and query ────────────────────────────────────────────────────

    [Fact]
    public void Fragment_Dropped()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com/page#section");
        Assert.NotNull(r);
        Assert.DoesNotContain("#", r!.Normalized);
    }

    [Fact]
    public void Query_Dropped()
    {
        var r = UrlNormalizer.TryNormalize("https://example.com/page?foo=bar&baz=1");
        Assert.NotNull(r);
        Assert.DoesNotContain("?", r!.Normalized);
    }

    // ── IDN hosts ────────────────────────────────────────────────────────────

    [Fact]
    public void IdnHost_Accepted()
    {
        // System.Uri normalises IDN to punycode on Windows.
        var r = UrlNormalizer.TryNormalize("https://münchen.de/page");
        Assert.NotNull(r);
        Assert.NotNull(r!.Host);
        Assert.False(string.IsNullOrEmpty(r.Normalized));
    }

    // ── Userinfo ─────────────────────────────────────────────────────────────

    [Fact]
    public void UserinfoInUrl_StillNormalized()
    {
        // System.Uri strips userinfo from the Authority used in AbsoluteUri but
        // we build our own string, so verify the host is extracted correctly.
        var r = UrlNormalizer.TryNormalize("https://user:pass@example.com/path");
        Assert.NotNull(r);
        Assert.Equal("example.com", r!.Host);
        Assert.DoesNotContain("user", r.Normalized);
    }

    // ── Unsupported / malformed ───────────────────────────────────────────────

    [Fact]
    public void FileScheme_ReturnsNull()
    {
        Assert.Null(UrlNormalizer.TryNormalize("file:///C:/foo/bar.txt"));
    }

    [Fact]
    public void MailtoScheme_ReturnsNull()
    {
        Assert.Null(UrlNormalizer.TryNormalize("mailto:someone@example.com"));
    }

    [Fact]
    public void Malformed_ReturnsNull()
    {
        Assert.Null(UrlNormalizer.TryNormalize("not a url at all"));
    }

    [Fact]
    public void Empty_ReturnsNull()
    {
        Assert.Null(UrlNormalizer.TryNormalize(string.Empty));
    }

    [Fact]
    public void Null_ReturnsNull()
    {
        Assert.Null(UrlNormalizer.TryNormalize(null!));
    }

    [Fact]
    public void RelativeUrl_ReturnsNull()
    {
        Assert.Null(UrlNormalizer.TryNormalize("/relative/path"));
    }
}
