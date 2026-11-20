using System.Text.Json.Nodes;
using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

// [utest->req~ank.outlook.sideline~1]
public class OutlookCategoriesSidelineTests
{
    [Theory]
    [InlineData("", "Sideline")]
    [InlineData(null, "Sideline")]
    [InlineData("Work", "Work, Sideline")]
    [InlineData("Work; Home", "Work; Home; Sideline")]
    [InlineData("Work, Home", "Work, Home, Sideline")]
    [InlineData("sideline", "sideline")]                 // already there: unchanged
    [InlineData("Work; SIDELINE ;Home", "Work; SIDELINE ;Home")]
    public void On(string? categories, string expected) =>
        Assert.Equal(expected, OutlookCategories.SetSideline(categories, on: true));

    [Fact]
    public void On_UsesTheGivenSeparatorOnlyWhenTheListHasNone()
    {
        Assert.Equal("Work; Sideline", OutlookCategories.SetSideline("Work", on: true, separator: ';'));
        Assert.Equal("Work, Home, Sideline", OutlookCategories.SetSideline("Work, Home", on: true, separator: ';'));
    }

    [Theory]
    [InlineData("Work; Sideline", "Work")]
    [InlineData("Sideline", "")]
    [InlineData("sideline, Work, Home", "Work, Home")]
    [InlineData("Work; SIDELINE; Home; Sideline", "Work; Home")]
    [InlineData("Work, Home", "Work, Home")]             // absent: unchanged
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Sidelines, Work", "Sidelines, Work")]   // only the exact name counts
    public void Off(string? categories, string expected) =>
        Assert.Equal(expected, OutlookCategories.SetSideline(categories, on: false));

    [Theory]
    [InlineData("Work; Sideline", true)]
    [InlineData(" sideline ", true)]
    [InlineData("Work,SIDELINE", true)]
    [InlineData("Sidelined", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Contains(string? categories, bool expected) =>
        Assert.Equal(expected, OutlookCategories.Contains(categories, OutlookCategories.Sideline));
}

// [utest->req~ank.outlook.busy~2]
public class OutlookCategoriesColorTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "#E7A1A2")]
    [InlineData(8, "#9DB7E8")]
    [InlineData(15, "#4F4F4F")]
    [InlineData(25, "#93446B")]
    [InlineData(26, null)]
    [InlineData(-1, null)]
    public void ColorHex(int color, string? expected) =>
        Assert.Equal(expected, OutlookCategories.ColorHex(color));

    [Fact]
    public void ColorHex_AllPresetsAreDistinctRgb()
    {
        var all = Enumerable.Range(1, 25).Select(OutlookCategories.ColorHex).ToList();
        Assert.All(all, c => Assert.Matches("^#[0-9A-F]{6}$", c!));
        Assert.Equal(25, all.Distinct().Count());
    }

    private static readonly IReadOnlyDictionary<string, int> Colors =
        OutlookCategories.ParseColors(JsonNode.Parse(
            """[{"name":"Work","color":8},{"name":"None","color":0},{"name":"koppter","color":1},{"name":"Sideline","color":5},{"name":"work","color":3}]"""));

    [Theory]
    [InlineData("Work", "#9DB7E8")]
    [InlineData("work; Home", "#9DB7E8")]               // case-insensitive; first entry of the list wins
    [InlineData("koppter, Sideline, Work", "#9DB7E8")]  // our marks are skipped
    [InlineData("Home, Work", null)]                    // first user category is unknown
    [InlineData("None, Work", null)]                    // first user category has no color
    [InlineData("koppter", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DisplayColor(string? categories, string? expected) =>
        Assert.Equal(expected, OutlookCategories.DisplayColor(categories, Colors));
}
