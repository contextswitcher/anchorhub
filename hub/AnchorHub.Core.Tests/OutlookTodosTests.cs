using System.Text.Json.Nodes;
using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

// [utest->req~ank.outlook.todos~1]
public class OutlookTodosTests
{
    [Theory]
    [InlineData("2026-11-20", "2026-11-20")]
    [InlineData("4501-01-01", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Date_MapsOutlookNoDateToNull(string? raw, string? expected) =>
        Assert.Equal(expected, OutlookTodos.Date(raw));

    [Fact]
    public void Timestamp_MapsOutlookNoDateToNull() =>
        Assert.Null(OutlookTodos.Timestamp("4501-01-01T00:00:00.0000000"));

    [Fact]
    public void Shape_MailWithAllFields()
    {
        var raw = JsonNode.Parse("""
            [{"entryId":"AB12","storeId":"CD34","store":"Work","kind":"mail","subject":"Hi there",
              "due":"2026-11-20","start":"4501-01-01","received":"2026-11-17T09:30:00.0000000","sender":"Ann"}]
            """);
        var shaped = OutlookTodos.Shape(raw);
        Assert.Equal(
            ["entryId", "storeId", "store", "kind", "subject", "due", "start", "received", "sender", "anchor"],
            shaped[0]!.AsObject().Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[{"entryId":"AB12","storeId":"CD34","store":"Work","kind":"mail","subject":"Hi there","due":"2026-11-20","start":null,"received":"2026-11-17T09:30:00.0000000","sender":"Ann","anchor":"anchor://outlook/message/AB12?x-store=CD34&title=Hi%20there&v=1"}]"""), shaped));
    }

    [Fact]
    public void Shape_SingleObjectCountsAsOneItem()
    {
        var shaped = OutlookTodos.Shape(JsonNode.Parse("""{"entryId":"AB12","kind":"task","subject":"Call"}"""));
        var item = Assert.Single(shaped)!.AsObject();
        Assert.Equal("task", (string?)item["kind"]);
        Assert.Null(item["due"]);
        Assert.Null(item["sender"]);
        Assert.Equal("anchor://outlook/message/AB12?title=Call&v=1", (string?)item["anchor"]);
    }

    [Fact]
    public void Shape_UnknownKindIsOther_AndItemsWithoutEntryIdAreLeftOut()
    {
        var shaped = OutlookTodos.Shape(JsonNode.Parse("""[{"entryId":"AB12","kind":"contact"},{"subject":"x"}]"""));
        Assert.Equal("other", (string?)Assert.Single(shaped)!["kind"]);
    }

    [Fact]
    public void Shape_EmptyAndNull()
    {
        Assert.Empty(OutlookTodos.Shape(JsonNode.Parse("[]")));
        Assert.Empty(OutlookTodos.Shape(null));
    }
}
