using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

// [utest->req~ank.outlook.capture~1]
// [utest->req~ank.outlook.open~1]
// [utest->req~ank.outlook.complete~1]
public class OutlookAnchorTests
{
    [Fact]
    public void RoundTrip()
    {
        var a = new OutlookAnchor("00000000ABCD", "0000STORE", "Q4 plan & review / ü");
        Assert.Equal(a, OutlookAnchor.TryParse(a.ToUri()));
    }

    [Fact]
    public void ToUri_MatchesGrammar()
    {
        Assert.Equal("anchor://outlook/message/AB12?x-store=CD34&title=Hi%20there&v=1",
            new OutlookAnchor("AB12", "CD34", "Hi there").ToUri());
    }

    [Fact]
    public void TryParse_WithoutQuery()
    {
        Assert.Equal(new OutlookAnchor("AB12", null, null),
            OutlookAnchor.TryParse("anchor://outlook/message/AB12"));
    }

    // koppter stores links without x-store; open and complete then search all stores.
    [Fact]
    public void TryParse_TitleWithoutStore()
    {
        Assert.Equal(new OutlookAnchor("AB12", null, "Hi there"),
            OutlookAnchor.TryParse("anchor://outlook/message/AB12?title=Hi%20there&v=1"));
    }

    [Theory]
    [InlineData("anchor://onenote/page/AB12")]
    [InlineData("anchor://outlook/message/")]
    [InlineData("AB12")]
    public void TryParse_RejectsOthers(string uri) => Assert.Null(OutlookAnchor.TryParse(uri));
}
