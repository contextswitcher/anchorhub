using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class RedditUrlHelperTests
{
    [Theory]
    [InlineData("https://www.reddit.com/r/docker/comments/1hck6x7/is_it_possible_to_configure_docker_to_use_a/",
                "reddit.com/r/docker")]
    [InlineData("https://www.reddit.com/r/ErgoMechKeyboards/",        "reddit.com/r/ErgoMechKeyboards")]
    [InlineData("https://reddit.com/r/docker",                        "reddit.com/r/docker")]
    [InlineData("https://old.reddit.com/r/docker/",                   "reddit.com/r/docker")]
    [InlineData("https://new.reddit.com/r/docker/top/",               "reddit.com/r/docker")]
    [InlineData("https://www.reddit.com/u/spez/",                     "reddit.com/u/spez")]
    [InlineData("https://www.reddit.com/u/spez/comments/",            "reddit.com/u/spez")]
    public void ExtractTerm_HostAnchoredSubredditOrUser(string raw, string expected)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Equal(expected, RedditUrlHelper.ExtractTerm(url!));
    }

    [Theory]
    [InlineData("https://www.reddit.com/")]                  // root
    [InlineData("https://www.reddit.com/r/")]                // /r/ with no subreddit
    [InlineData("https://www.reddit.com/popular/")]          // not /r/ or /u/
    [InlineData("https://www.reddit.com/settings/account")]  // generic page
    [InlineData("https://example.com/r/docker")]             // wrong host
    public void ExtractTerm_ReturnsNullForNonSubredditOrUser(string raw)
    {
        var url = UrlNormalizer.TryNormalize(raw);
        Assert.NotNull(url);
        Assert.Null(RedditUrlHelper.ExtractTerm(url!));
    }
}
