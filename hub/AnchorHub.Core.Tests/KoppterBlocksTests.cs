using System.Text.Json.Nodes;
using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

// [utest->req~ank.outlook.blocks-own~1]
public class KoppterBlocksOwnTests
{
    [Theory]
    [InlineData("b1", "koppter", false, 0, true)]
    [InlineData("b1", "Work; Koppter", false, 0, true)]
    [InlineData("b1", "Work, KOPPTER ,Home", false, 0, true)]
    [InlineData("b1", " koppter ", false, 0, true)]
    [InlineData(null, "koppter", false, 0, false)]   // category without property
    [InlineData("", "koppter", false, 0, false)]     // property empty
    [InlineData("  ", "koppter", false, 0, false)]   // property blank
    [InlineData("b1", null, false, 0, false)]        // property without category
    [InlineData("b1", "", false, 0, false)]
    [InlineData("b1", "Work", false, 0, false)]
    [InlineData("b1", "koppter-old", false, 0, false)]
    [InlineData("b1", "my koppter", false, 0, false)]
    [InlineData("b1", "koppter", true, 0, false)]    // recurring with both markers
    [InlineData("b1", "koppter", false, 1, false)]   // meeting with both markers
    [InlineData("b1", "koppter", false, 3, false)]   // received meeting
    [InlineData("b1", "koppter", false, -1, false)]  // unknown meeting status
    public void IsOwn(string? blockId, string? categories, bool recurring, int meetingStatus, bool expected) =>
        Assert.Equal(expected, KoppterBlocks.IsOwn(blockId, categories, recurring, meetingStatus));

    [Fact]
    public void SubjectAndBodyNeverCount()
    {
        var item = new CalendarItem("E1", null, "Work", false, 0, T("2026-11-18T09:00"), T("2026-11-18T10:00"), "Write paper [koppter]");
        Assert.False(KoppterBlocks.IsOwn(item));
    }

    internal static DateTime T(string s) => KoppterBlocks.ParseTime(s)!.Value;
}

// [utest->req~ank.outlook.blocks-sync~1]
public class KoppterBlocksPlanTests
{
    private static readonly DateTime From = T("2026-11-18T00:00");
    private static readonly DateTime To = T("2026-11-25T00:00");

    private static DateTime T(string s) => KoppterBlocksOwnTests.T(s);

    private static CalendarItem Own(string entryId, string blockId, string start, string end, string subject,
        string categories = "koppter", bool recurring = false, int meeting = 0) =>
        new(entryId, blockId, categories, recurring, meeting, T(start), T(end), subject);

    private static KoppterBlock Block(string id, string start, string end, string subject) =>
        new(id, T(start), T(end), subject, "");

    [Fact]
    public void Unchanged_IsKept()
    {
        var plan = KoppterBlocks.Plan(
            [Own("E1", "b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write")], From, To,
            [Block("b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write")]);
        Assert.Empty(plan.ToDelete);
        Assert.Empty(plan.ToAdd);
        Assert.Equal("E1", Assert.Single(plan.Kept).EntryId);
    }

    [Theory]
    [InlineData("2026-11-18T09:30", "2026-11-18T10:00", "Write")]
    [InlineData("2026-11-18T09:00", "2026-11-18T10:30", "Write")]
    [InlineData("2026-11-18T09:00", "2026-11-18T10:00", "Read")]
    public void Changed_IsDeletedAndAdded(string start, string end, string subject)
    {
        var plan = KoppterBlocks.Plan(
            [Own("E1", "b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write")], From, To,
            [Block("b1", start, end, subject)]);
        Assert.Equal("E1", Assert.Single(plan.ToDelete).EntryId);
        Assert.Equal("b1", Assert.Single(plan.ToAdd).Id);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void NoLongerWanted_IsDeleted()
    {
        var plan = KoppterBlocks.Plan(
            [Own("E1", "gone", "2026-11-18T09:00", "2026-11-18T10:00", "Write")], From, To, []);
        Assert.Equal("E1", Assert.Single(plan.ToDelete).EntryId);
        Assert.Empty(plan.ToAdd);
    }

    [Fact]
    public void NewBlock_IsAdded()
    {
        var plan = KoppterBlocks.Plan([], From, To, [Block("b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write")]);
        Assert.Empty(plan.ToDelete);
        Assert.Equal("b1", Assert.Single(plan.ToAdd).Id);
    }

    [Fact]
    public void ForeignItems_AreNeverDeleted()
    {
        CalendarItem[] foreign =
        [
            Own("C1", null!, "2026-11-18T09:00", "2026-11-18T10:00", "Write"),                        // no property
            Own("C2", "", "2026-11-18T09:00", "2026-11-18T10:00", "Write"),                           // empty property
            Own("C3", "b9", "2026-11-18T09:00", "2026-11-18T10:00", "Write", categories: "Work"),     // no category
            Own("C4", "b9", "2026-11-18T09:00", "2026-11-18T10:00", "Write", recurring: true),        // recurring
            Own("C5", "b9", "2026-11-18T09:00", "2026-11-18T10:00", "Write", meeting: 1),             // meeting
            new("C6", null, null, false, 0, T("2026-11-18T09:00"), T("2026-11-18T10:00"), "Write [koppter]"),
        ];
        var plan = KoppterBlocks.Plan(foreign, From, To, []);
        Assert.Empty(plan.ToDelete);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void ForeignItemWithWantedId_IsIgnoredAndBlockAdded()
    {
        var plan = KoppterBlocks.Plan(
            [Own("C1", "b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write", meeting: 1)], From, To,
            [Block("b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write")]);
        Assert.Empty(plan.ToDelete);
        Assert.Equal("b1", Assert.Single(plan.ToAdd).Id);
    }

    [Theory]
    [InlineData("2026-11-17T23:59")]
    [InlineData("2026-11-25T00:00")]
    public void OwnItemsOutsideWindow_AreLeftAlone(string start)
    {
        var plan = KoppterBlocks.Plan(
            [Own("E1", "old", start, "2026-11-26T01:00", "Write")], From, To, []);
        Assert.Empty(plan.ToDelete);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void DuplicateOwnItems_FirstKeptRestDeleted()
    {
        var plan = KoppterBlocks.Plan(
        [
            Own("E2", "b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write"),
            Own("E1", "b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write"),
        ], From, To, [Block("b1", "2026-11-18T09:00", "2026-11-18T10:00", "Write")]);
        Assert.Equal("E1", Assert.Single(plan.Kept).EntryId);
        Assert.Equal("E2", Assert.Single(plan.ToDelete).EntryId);
        Assert.Empty(plan.ToAdd);
    }
}

// [utest->req~ank.outlook.blocks-sync~1]
public class KoppterBlocksRequestTests
{
    private const string Valid = """
        {"calendar": null, "from": "2026-11-18T00:00", "to": "2026-11-25T00:00",
         "blocks": [{"id": "b1", "start": "2026-11-18T09:00", "end": "2026-11-18T10:00", "subject": "Write", "body": "Über"}]}
        """;

    [Fact]
    public void Valid_IsParsed()
    {
        var req = KoppterBlocks.ParseSyncRequest(Valid);
        Assert.Null(req.Calendar);
        Assert.Equal(new DateTime(2026, 11, 18), req.From);
        Assert.Equal(new DateTime(2026, 11, 25), req.To);
        var b = Assert.Single(req.Blocks);
        Assert.Equal(new KoppterBlock("b1", new DateTime(2026, 11, 18, 9, 0, 0), new DateTime(2026, 11, 18, 10, 0, 0), "Write", "Über"), b);
    }

    [Fact]
    public void CalendarNameAndMissingBody_AreAccepted()
    {
        var req = KoppterBlocks.ParseSyncRequest("""
            {"calendar": "Work", "from": "2026-11-18T00:00", "to": "2026-11-19T00:00",
             "blocks": [{"id": "b1", "start": "2026-11-18T09:00", "end": "2026-11-18T10:00", "subject": "Write"}]}
            """);
        Assert.Equal("Work", req.Calendar);
        Assert.Equal("", Assert.Single(req.Blocks).Body);
    }

    [Fact]
    public void EmptyBlocks_AreAccepted() =>
        Assert.Empty(KoppterBlocks.ParseSyncRequest("""{"calendar": null, "from": "2026-11-18T00:00", "to": "2026-11-19T00:00", "blocks": []}""").Blocks);

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"from": "2026-11-18T00:00", "to": "2026-11-19T00:00"}""")]
    [InlineData("""{"from": "2026-11-19T00:00", "to": "2026-11-19T00:00", "blocks": []}""")]
    [InlineData("""{"from": "2026-11-20T00:00", "to": "2026-11-19T00:00", "blocks": []}""")]
    [InlineData("""{"from": "2026-11-18", "to": "2026-11-19T00:00", "blocks": []}""")]
    [InlineData("""{"from": "2026-11-18T00:00:00", "to": "2026-11-19T00:00", "blocks": []}""")]
    [InlineData("""{"calendar": "", "from": "2026-11-18T00:00", "to": "2026-11-19T00:00", "blocks": []}""")]
    [InlineData("""{"calendar": 3, "from": "2026-11-18T00:00", "to": "2026-11-19T00:00", "blocks": []}""")]
    public void InvalidFrame_IsRejected(string json) =>
        Assert.Throws<FormatException>(() => KoppterBlocks.ParseSyncRequest(json));

    [Theory]
    [InlineData("""{"start": "2026-11-18T09:00", "end": "2026-11-18T10:00", "subject": "A"}""")]               // no id
    [InlineData("""{"id": "", "start": "2026-11-18T09:00", "end": "2026-11-18T10:00", "subject": "A"}""")]     // empty id
    [InlineData("""{"id": " ", "start": "2026-11-18T09:00", "end": "2026-11-18T10:00", "subject": "A"}""")]
    [InlineData("""{"id": 7, "start": "2026-11-18T09:00", "end": "2026-11-18T10:00", "subject": "A"}""")]
    [InlineData("""{"id": "x", "start": "2026-11-18T10:00", "end": "2026-11-18T10:00", "subject": "A"}""")]    // start = end
    [InlineData("""{"id": "x", "start": "2026-11-18T11:00", "end": "2026-11-18T10:00", "subject": "A"}""")]    // start > end
    [InlineData("""{"id": "x", "start": "2026-11-17T09:00", "end": "2026-11-18T10:00", "subject": "A"}""")]    // before from
    [InlineData("""{"id": "x", "start": "2026-11-19T00:00", "end": "2026-11-19T10:00", "subject": "A"}""")]    // at to
    [InlineData("""{"id": "x", "start": "2026-11-18T09:00", "end": "2026-11-18T10:00"}""")]                    // no subject
    [InlineData("""{"id": "x", "start": "2026-11-18T09:00", "end": "2026-11-18T10:00", "subject": "A", "body": 1}""")]
    [InlineData(""" "x" """)]
    public void InvalidBlock_RejectsWholeRequest(string badBlock) =>
        Assert.Throws<FormatException>(() => KoppterBlocks.ParseSyncRequest(
            $$"""
            {"calendar": null, "from": "2026-11-18T00:00", "to": "2026-11-19T00:00", "blocks": [
              {"id": "ok", "start": "2026-11-18T08:00", "end": "2026-11-18T09:00", "subject": "Fine"},
              {{badBlock}}]}
            """));

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        var ex = Assert.Throws<FormatException>(() => KoppterBlocks.ParseSyncRequest("""
            {"calendar": null, "from": "2026-11-18T00:00", "to": "2026-11-19T00:00", "blocks": [
              {"id": "b1", "start": "2026-11-18T08:00", "end": "2026-11-18T09:00", "subject": "A"},
              {"id": "b1", "start": "2026-11-18T10:00", "end": "2026-11-18T11:00", "subject": "B"}]}
            """));
        Assert.Contains("more than once", ex.Message);
    }
}

// [utest->req~ank.outlook.busy~2]
public class KoppterBlocksBusyTests
{
    private static DateTime T(string s) => KoppterBlocksOwnTests.T(s);

    [Fact]
    public void Busy_OverlapsFiltersAndMaps()
    {
        CalendarItem[] items =
        [
            new("E3", null, "Sideline; Work", true, 0, T("2026-11-18T14:00"), T("2026-11-18T15:00"), "Series", BusyStatus: 4),
            new("E1", null, "Work", false, 1, T("2026-11-17T23:00"), T("2026-11-18T01:00"), "Night", BusyStatus: 2),
            new("E2", null, null, false, 0, T("2026-11-18T00:00"), T("2026-11-19T00:00"), "Holiday", AllDay: true, BusyStatus: 3),
            new("K1", "b1", "koppter", false, 0, T("2026-11-18T09:00"), T("2026-11-18T10:00"), "Own block", BusyStatus: 1),
            new("K2", null, null, false, 0, T("2026-11-18T11:00"), T("2026-11-18T12:00"), "Old [koppter]", BusyStatus: 1),
            new("X1", null, null, false, 0, T("2026-11-17T08:00"), T("2026-11-18T00:00"), "Ends at from"),
            new("X2", null, null, false, 0, T("2026-11-19T00:00"), T("2026-11-19T01:00"), "Starts at to"),
            new("F1", null, null, false, 0, T("2026-11-18T16:00"), T("2026-11-18T17:00"), "Free", BusyStatus: 0),
            new("T1", null, null, false, 0, T("2026-11-18T17:00"), T("2026-11-18T18:00"), "", BusyStatus: 1),
        ];
        var colors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Work"] = 8 };
        var busy = KoppterBlocks.Busy(items, T("2026-11-18T00:00"), T("2026-11-19T00:00"), colors);
        Assert.Equal(
            """[{"start":"2026-11-17T23:00","end":"2026-11-18T01:00","allDay":false,"busyStatus":"busy","subject":"Night","entryId":"E1","recurring":false,"sideline":false,"color":"#9DB7E8"},""" +
            """{"start":"2026-11-18T00:00","end":"2026-11-19T00:00","allDay":true,"busyStatus":"oof","subject":"Holiday","entryId":"E2","recurring":false,"sideline":false,"color":null},""" +
            """{"start":"2026-11-18T14:00","end":"2026-11-18T15:00","allDay":false,"busyStatus":"elsewhere","subject":"Series","entryId":"E3","recurring":true,"sideline":true,"color":"#9DB7E8"},""" +
            """{"start":"2026-11-18T16:00","end":"2026-11-18T17:00","allDay":false,"busyStatus":"free","subject":"Free","entryId":"F1","recurring":false,"sideline":false,"color":null},""" +
            """{"start":"2026-11-18T17:00","end":"2026-11-18T18:00","allDay":false,"busyStatus":"tentative","subject":"","entryId":"T1","recurring":false,"sideline":false,"color":null}]""",
            busy.ToJsonString());
    }

    [Fact]
    public void ParseItems_ReadsScriptOutputAndDefaultsToNotOwn()
    {
        var items = KoppterBlocks.ParseItems(JsonNode.Parse("""
            [{"entryId":"E1","blockId":"b1","categories":"koppter","isRecurring":false,"meetingStatus":0,
              "start":"2026-11-18T09:00","end":"2026-11-18T10:00","subject":"Write","allDay":false,"busyStatus":1},
             {"entryId":"E2","blockId":"b2","categories":"koppter","start":"2026-11-18T09:00","end":"2026-11-18T10:00"},
             {"entryId":"E3","start":"bad","end":"2026-11-18T10:00"}]
            """));
        Assert.Equal(2, items.Count);
        Assert.True(KoppterBlocks.IsOwn(items[0]));
        Assert.Equal(1, items[0].BusyStatus);
        Assert.False(KoppterBlocks.IsOwn(items[1]));   // missing isRecurring / meetingStatus never count as own
    }

    [Fact]
    public void ParseItems_SingleObject() =>
        Assert.Single(KoppterBlocks.ParseItems(JsonNode.Parse(
            """{"entryId":"E1","start":"2026-11-18T09:00","end":"2026-11-18T10:00"}""")));
}
