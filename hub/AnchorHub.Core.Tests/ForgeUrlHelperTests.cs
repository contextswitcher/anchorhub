using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class ForgeUrlHelperTests
{
    [Theory]
    [InlineData("https://github.com/hakimel/reveal.js",        "github.com",  "hakimel",   "reveal.js")]
    [InlineData("https://www.github.com/hakimel/reveal.js/",   "github.com",  "hakimel",   "reveal.js")]
    [InlineData("https://github.com/hakimel",                  "github.com",  "hakimel",   null)]
    [InlineData("https://gitlab.com/gitlab-org/gitlab",        "gitlab.com",  "gitlab-org", "gitlab")]
    [InlineData("https://www.gitlab.com/gitlab-org/",          "gitlab.com",  "gitlab-org", null)]
    [InlineData("https://codeberg.org/forgejo/forgejo",        "codeberg.org", "forgejo",  "forgejo")]
    [InlineData("https://codeberg.org/forgejo",                "codeberg.org", "forgejo",  null)]
    public void ExtractTerms_KnownForge(string raw, string expectedHost, string expectedOrg, string? expectedProject)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        var terms = ForgeUrlHelper.ExtractTerms(url!);
        Assert.NotNull(terms);
        Assert.Equal(expectedHost,    terms!.Value.Host);
        Assert.Equal(expectedOrg,     terms.Value.Org);
        Assert.Equal(expectedProject, terms.Value.Project);
    }

    [Theory]
    [InlineData("https://gist.github.com/octocat/abc123")]    // gist is handled separately
    [InlineData("https://bitbucket.org/atlassian/python")]   // unknown forge
    [InlineData("https://example.com/foo/bar")]              // unrelated host
    public void ExtractTerms_ReturnsNullForUnknownForge(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(ForgeUrlHelper.ExtractTerms(url!));
    }

    [Theory]
    [InlineData("https://github.com/topics/git-analysis",      "github.com")]
    [InlineData("https://github.com/marketplace/actions/foo",  "github.com")]
    [InlineData("https://github.com/search?q=foo",             "github.com")]
    [InlineData("https://gitlab.com/explore/projects",         "gitlab.com")]
    [InlineData("https://codeberg.org/explore/repos",          "codeberg.org")]
    public void ExtractTerms_ReservedFirstSegmentBlocksShortening(string raw, string expectedHost)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        var terms = ForgeUrlHelper.ExtractTerms(url!);
        Assert.NotNull(terms);
        Assert.Equal(expectedHost, terms!.Value.Host);
        Assert.Null(terms.Value.Org);
        Assert.Null(terms.Value.Project);
    }

    [Theory]
    [InlineData("https://github.com/jabref/jabref/issues/5558")]                // issue
    [InlineData("https://github.com/jabref/jabref/issues/5558#issuecomment-1")] // issue + fragment
    [InlineData("https://github.com/jabref/jabref/pull/1234")]                  // PR
    [InlineData("https://github.com/jabref/jabref/commit/abc123def")]           // commit
    [InlineData("https://github.com/jabref/jabref/blob/main/src/Main.java")]    // source file (not README)
    [InlineData("https://github.com/jabref/jabref/blob/main/docs/index.md")]    // doc file (not at root)
    [InlineData("https://github.com/jabref/jabref/tree/main/src")]              // tree under default branch
    [InlineData("https://github.com/jabref/jabref/wiki/Contributing")]          // wiki page (not Home)
    [InlineData("https://github.com/jabref/jabref/discussions/123")]            // discussion
    [InlineData("https://github.com/jabref/jabref/issues")]                     // issue list
    [InlineData("https://gitlab.com/gitlab-org/gitlab/-/issues/123")]           // gitlab issue
    [InlineData("https://gitlab.com/gitlab-org/gitlab/-/blob/main/src/foo.rb")] // gitlab source file
    [InlineData("https://codeberg.org/forgejo/forgejo/issues/42")]              // codeberg issue
    public void IsRepoSubpath_DeepLink_ReturnsTrue(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(ForgeUrlHelper.IsRepoSubpath(url!));
    }

    [Theory]
    [InlineData("https://github.com/hakimel/reveal.js")]                        // repo root
    [InlineData("https://github.com/hakimel/reveal.js/")]                       // repo root + slash
    [InlineData("https://github.com/hakimel")]                                  // org root
    [InlineData("https://github.com/topics/git-analysis")]                      // reserved first segment
    [InlineData("https://github.com/search?q=foo")]                             // reserved
    [InlineData("https://gist.github.com/octocat/abc123")]                       // gist (different host)
    [InlineData("https://example.com/foo/bar/baz")]                             // not a forge
    [InlineData("https://github.com/JuliusBrussee/caveman/blob/main/README.md")] // README at repo root
    [InlineData("https://github.com/JuliusBrussee/caveman/blob/main/Readme")]    // README, no extension, mixed case
    [InlineData("https://github.com/jabref/jabref/blob/main/README.adoc")]       // README, .adoc
    [InlineData("https://github.com/jabref/jabref/tree/main")]                   // default-branch tree root
    [InlineData("https://github.com/jabref/jabref/wiki")]                        // wiki landing
    [InlineData("https://github.com/jabref/jabref/wiki/Home")]                   // wiki Home (case-insensitive)
    [InlineData("https://gitlab.com/gitlab-org/gitlab/-/blob/main/README.md")]   // gitlab README via /-/
    [InlineData("https://gitlab.com/gitlab-org/gitlab/-/tree/main")]             // gitlab default tree
    public void IsRepoSubpath_NonDeepLink_ReturnsFalse(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.False(ForgeUrlHelper.IsRepoSubpath(url!));
    }

    [Theory]
    [InlineData("https://github.com/JuliusBrussee/caveman/blob/main/README.md")]
    [InlineData("https://github.com/jabref/jabref/blob/main/README")]
    [InlineData("https://github.com/jabref/jabref/blob/main/readme.rst")]
    [InlineData("https://github.com/jabref/jabref/tree/main")]
    [InlineData("https://github.com/jabref/jabref/wiki")]
    [InlineData("https://github.com/jabref/jabref/wiki/Home")]
    [InlineData("https://gitlab.com/gitlab-org/gitlab/-/blob/main/README.md")]
    [InlineData("https://gitlab.com/gitlab-org/gitlab/-/tree/main")]
    public void IsRepoIdentityPath_IdentityLink_ReturnsTrue(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.True(ForgeUrlHelper.IsRepoIdentityPath(url!));
    }

    [Theory]
    [InlineData("https://github.com/hakimel/reveal.js")]                          // repo root — not deep
    [InlineData("https://github.com/jabref/jabref/issues/5558")]                  // issue
    [InlineData("https://github.com/jabref/jabref/blob/main/src/Main.java")]      // non-README file
    [InlineData("https://github.com/jabref/jabref/blob/main/docs/README.md")]     // README in subdir, not root
    [InlineData("https://github.com/jabref/jabref/tree/main/src")]                // tree below root
    [InlineData("https://github.com/jabref/jabref/wiki/Contributing")]            // wiki page other than Home
    [InlineData("https://example.com/foo/bar/blob/main/README.md")]               // not a recognised forge
    public void IsRepoIdentityPath_NonIdentity_ReturnsFalse(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.False(ForgeUrlHelper.IsRepoIdentityPath(url!));
    }

    [Theory]
    [InlineData("reveal.js")]   // distinctive — has a dot, 9 chars
    [InlineData("vulhub")]      // distinctive — 6 chars, uncommon word
    [InlineData("jabref")]      // distinctive — 6 chars
    [InlineData("forgejo")]     // distinctive — 7 chars
    public void IsDistinctiveProjectName_Distinctive_ReturnsTrue(string project)
    {
        Assert.True(ForgeUrlHelper.IsDistinctiveProjectName(project));
    }

    [Theory]
    [InlineData(null)]          // no project segment
    [InlineData("")]            // empty
    [InlineData("vale")]        // 4 chars — the reported false-match case
    [InlineData("core")]        // 4 chars, common
    [InlineData("api")]         // 3 chars
    [InlineData("cli")]         // 3 chars
    [InlineData("utils")]       // 5 chars but common word
    [InlineData("tools")]       // common word
    [InlineData("EXAMPLES")]    // common word, case-insensitive
    public void IsDistinctiveProjectName_GenericOrShort_ReturnsFalse(string? project)
    {
        Assert.False(ForgeUrlHelper.IsDistinctiveProjectName(project));
    }

    [Theory]
    [InlineData("https://gist.github.com/octocat/abc123",  "octocat")]
    [InlineData("https://gist.github.com/octocat/",        "octocat")]
    [InlineData("https://gist.github.com/octocat",         "octocat")]
    public void ExtractGistUser_KnownGist(string raw, string expectedUser)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expectedUser, ForgeUrlHelper.ExtractGistUser(url!));
    }

    [Theory]
    [InlineData("https://github.com/hakimel/reveal.js")]   // not gist host
    [InlineData("https://gist.github.com/")]               // no user
    [InlineData("https://example.com/foo")]
    public void ExtractGistUser_ReturnsNullForNonGist(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(ForgeUrlHelper.ExtractGistUser(url!));
    }
}
