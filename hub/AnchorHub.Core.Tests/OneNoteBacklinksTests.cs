using System.Xml.Linq;
using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

// [utest->req~ank.onenote.backlinks~2]
public class OneNoteBacklinksTests
{
    private const string PageGuid = "0123abcd-0000-4000-8000-000000000001";
    private const string Note =
        "onenote:https://d.docs.live.net/abc/Documents/My%20Notes/Projects.one#Fix%20NPE&section-id={AAAABBBB-0000-4000-8000-000000000002}&page-id={0123ABCD-0000-4000-8000-000000000001}&end";
    private static readonly XNamespace One = OneNoteBacklinks.Namespace;
    private static readonly string Png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2 });

    [Fact]
    public void ParsesSectionPathAndGuids()
    {
        var parts = OneNoteLinkParts.Parse(Note)!;
        Assert.Equal("https://d.docs.live.net/abc/Documents/My Notes/Projects.one", parts.SectionPath);
        Assert.Equal("aaaabbbb-0000-4000-8000-000000000002", parts.SectionGuid);
        Assert.Equal(PageGuid, parts.PageGuid);
        Assert.Null(OneNoteLinkParts.Parse("https://onedrive.live.com/x"));
    }

    [Fact]
    public void KeyIsThePageGuidElseTheLink()
    {
        Assert.Equal(PageGuid, OneNoteBacklinks.Key(Note));
        Assert.Equal(OneNoteBacklinks.Key(Note), OneNoteBacklinks.Key(Note.Replace("Fix%20NPE", "Renamed")));
        Assert.Equal("onenote:https://x/S.one", OneNoteBacklinks.Key(" onenote:https://x/S.one "));
    }

    [Fact]
    public void ParsesRequest()
    {
        var req = OneNoteBacklinks.ParseSyncRequest($$"""
            {"source":"koppter","links":[{"note":"{{Note}}","label":"Call","url":"koppter://task?x=1","done":true},
                                         {"note":"{{Note}}","label":"Two","url":"koppter://task?x=2"}]}
            """);
        Assert.Equal("koppter", req.Source);
        Assert.Equal("koppter", req.Name);
        Assert.Null(req.Icon);
        Assert.True(req.Links[0].Done);
        Assert.False(req.Links[1].Done);
    }

    [Theory]
    [InlineData("""{"source":"Bad Source","links":[]}""")]
    [InlineData("""{"source":"k","links":{}}""")]
    [InlineData("""{"source":"k","links":[{"note":"https://x","label":"a","url":"k://t"}]}""")]
    [InlineData("""{"source":"k","links":[{"note":"onenote:https://x/S.one","label":"","url":"k://t"}]}""")]
    [InlineData("""{"source":"k","links":[{"note":"onenote:https://x/S.one","label":"a","url":"relative"}]}""")]
    [InlineData("""{"source":"k","links":[{"note":"onenote:https://x/S.one","label":"a","url":"k://t","done":"yes"}]}""")]
    [InlineData("""{"source":"k","icon":"aGVsbG8=","links":[]}""")]
    [InlineData("""{"source":"k","icon":"not base64!","links":[]}""")]
    [InlineData("not json")]
    public void RejectsInvalidRequest(string json) =>
        Assert.Throws<FormatException>(() => OneNoteBacklinks.ParseSyncRequest(json));

    [Fact]
    public void ParsesPngIcon() =>
        Assert.Equal(Png, OneNoteBacklinks.ParseSyncRequest($$"""{"source":"k","icon":"{{Png}}","links":[]}""").Icon);

    [Fact]
    public void LineHasOneRowPerSourceWithIconOrName()
    {
        var koppter = new BacklinkSource { Name = "koppter" };
        var cs = new BacklinkSource { Name = "ContextSwitcher", Icon = Png };
        var line = OneNoteBacklinks.Line([
            (koppter, new Backlink(Note, "Call <dentist>", "koppter://task?a=1&b=2", Done: true)),
            (koppter, new Backlink(Note, "Buy", "koppter://task?a=3", Done: false)),
            (cs, new Backlink(Note, "Fix NPE", "contextswitcher://task/fix", Done: false)),
        ])!;
        Assert.Equal(OneNoteBacklinks.Marker, line.Element(One + "Meta")!.Attribute("name")!.Value);
        var table = line.Element(One + "Table")!;
        Assert.Equal("false", table.Attribute("bordersVisible")!.Value);
        var rows = table.Elements(One + "Row").ToList();
        Assert.Equal(2, rows.Count);

        var csCells = rows[0].Elements(One + "Cell").ToList();
        Assert.Equal(Png, csCells[0].Descendants(One + "Data").Single().Value);
        Assert.Equal("<a href=\"contextswitcher://task/fix\">Fix NPE</a>", csCells[1].Descendants(One + "T").Single().Value);

        var kCells = rows[1].Elements(One + "Cell").ToList();
        Assert.Equal("koppter", kCells[0].Descendants(One + "T").Single().Value);
        Assert.Equal(
            "<a href=\"koppter://task?a=3\">Buy</a> · " +
            "<a href=\"koppter://task?a=1&amp;b=2\"><span style='text-decoration:line-through'>Call &lt;dentist&gt;</span></a>",
            kCells[1].Descendants(One + "T").Single().Value);

        Assert.Null(OneNoteBacklinks.Line([]));
    }

    private static BacklinkSyncRequest Request(string source, params Backlink[] links) => new(source, source, null, links);

    private static void Apply(BacklinkState state, BacklinkSyncRequest request)
    {
        foreach (var w in OneNoteBacklinks.Plan(state, request))
            OneNoteBacklinks.Done(state, w, "{page}");
    }

    [Fact]
    public void PlanWritesNewPagesAndSkipsUnchanged()
    {
        var state = new BacklinkState();
        var link = new Backlink(Note, "A", "koppter://t", false);
        var single = Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter", link)));
        Assert.Equal(PageGuid, single.Key);
        Assert.NotNull(single.Line);
        OneNoteBacklinks.Done(state, single, "{page}");

        Assert.Empty(OneNoteBacklinks.Plan(state, Request("koppter", link)));
        Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter", link), all: true));
    }

    [Fact]
    public void PlanRemovesLineWhenTheLastLinkGoes()
    {
        var state = new BacklinkState();
        Apply(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false)));

        var work = Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter")));
        Assert.Null(work.Line);
        Assert.Equal(Note, work.Note);
        OneNoteBacklinks.Done(state, work, null);
        Assert.Empty(state.Pages);
    }

    [Fact]
    public void PlanKeepsTheLinksOfOtherSources()
    {
        var state = new BacklinkState();
        Apply(state, Request("contextswitcher", new Backlink(Note, "Task", "contextswitcher://task/t", false)));
        Apply(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false)));

        var work = Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter")));
        Assert.Contains("contextswitcher://task/t", work.Line);
        Assert.DoesNotContain("koppter://t", work.Line);
    }

    [Fact]
    public void PlanRewritesWhenATaskIsDone()
    {
        var state = new BacklinkState();
        Apply(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false)));
        var work = Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", true))));
        Assert.Contains("line-through", work.Line);
    }

    [Fact]
    public void PlanRewritesWhenTheIconChanges()
    {
        var state = new BacklinkState();
        var link = new Backlink(Note, "A", "koppter://t", false);
        Apply(state, Request("koppter", link));
        Assert.Single(OneNoteBacklinks.Plan(state, new BacklinkSyncRequest("koppter", "koppter", Png, [link])));
    }

    private static string PageXml(string body) =>
        $$"""<one:Page xmlns:one="{{OneNoteBacklinks.Namespace}}" ID="{P}"><one:Title><one:OE objectID="t"><one:T><![CDATA[Title]]></one:T></one:OE></one:Title>{{body}}</one:Page>""";

    private static string LineXml(string text) =>
        OneNoteBacklinks.Line([(new BacklinkSource { Name = "k" }, new Backlink(Note, text, "k://t", false))])!.ToString();

    private static string LinkText(XElement line) => line.Descendants(One + "T").Last().Value;

    [Fact]
    public void EditInsertsLineIntoTopmostPlainOutline()
    {
        var xml = PageXml("""
            <one:Outline objectID="low"><one:Position x="36" y="300"/><one:OEChildren><one:OE objectID="l1"><one:T>low</one:T></one:OE></one:OEChildren></one:Outline>
            <one:Outline objectID="bank"><one:Position x="36" y="40"/><one:Meta name="omTaggingBank" content="1"/><one:OEChildren><one:OE><one:T>tags</one:T></one:OE></one:OEChildren></one:Outline>
            <one:Outline objectID="top"><one:Position x="36" y="86"/><one:OEChildren><one:OE objectID="t1"><one:T>first</one:T></one:OE></one:OEChildren></one:Outline>
            """);
        var edit = OneNoteBacklinks.Edit(xml, LineXml("x"));
        Assert.Null(edit.DeleteObjectId);
        var update = XElement.Parse(edit.UpdateXml!);
        Assert.Equal("{P}", update.Attribute("ID")!.Value);
        var outline = Assert.Single(update.Elements(One + "Outline"));
        Assert.Equal("top", outline.Attribute("objectID")!.Value);
        var oes = outline.Element(One + "OEChildren")!.Elements(One + "OE").ToList();
        Assert.Equal(2, oes.Count);
        Assert.Equal(OneNoteBacklinks.Marker, oes[0].Element(One + "Meta")!.Attribute("name")!.Value);
        Assert.Contains(">x<", LinkText(oes[0]));
        Assert.Equal("t1", oes[1].Attribute("objectID")!.Value);
    }

    [Fact]
    public void EditReplacesExistingLine()
    {
        var xml = PageXml($"""
            <one:Outline objectID="o"><one:OEChildren>
              <one:OE objectID="b"><one:Meta name="{OneNoteBacklinks.Marker}" content="1"/><one:T><![CDATA[old]]></one:T></one:OE>
              <one:OE objectID="t1"><one:T>first</one:T></one:OE>
            </one:OEChildren></one:Outline>
            """);
        var outline = XElement.Parse(OneNoteBacklinks.Edit(xml, LineXml("new")).UpdateXml!).Element(One + "Outline")!;
        var oes = outline.Element(One + "OEChildren")!.Elements(One + "OE").ToList();
        Assert.Equal(2, oes.Count);
        Assert.Null(oes[0].Attribute("objectID"));
        Assert.Contains(">new<", LinkText(oes[0]));
        Assert.Equal("t1", oes[1].Attribute("objectID")!.Value);
    }

    [Fact]
    public void EditRemovesLineByLeavingItOutOfItsOutline()
    {
        var xml = PageXml($"""<one:Outline objectID="o"><one:OEChildren><one:OE objectID="b"><one:Meta name="{OneNoteBacklinks.Marker}" content="1"/><one:T>x</one:T></one:OE><one:OE objectID="t1"><one:T>first</one:T></one:OE></one:OEChildren></one:Outline>""");
        var edit = OneNoteBacklinks.Edit(xml, null);
        Assert.Null(edit.DeleteObjectId);
        var oe = XElement.Parse(edit.UpdateXml!).Descendants(One + "OE").Single();
        Assert.Equal("t1", oe.Attribute("objectID")!.Value);
    }

    [Fact]
    public void EditDeletesOutlineThatHeldOnlyTheLineOrDoesNothing()
    {
        var marked = PageXml($"""<one:Outline objectID="o"><one:OEChildren><one:OE objectID="b"><one:Meta name="{OneNoteBacklinks.Marker}" content="1"/><one:T>x</one:T></one:OE></one:OEChildren></one:Outline>""");
        Assert.Equal(new BacklinkEdit(null, "o"), OneNoteBacklinks.Edit(marked, null));
        Assert.Equal(BacklinkEdit.None, OneNoteBacklinks.Edit(PageXml(""), null));
    }

    [Fact]
    public void EditCreatesOutlineOnEmptyPage()
    {
        var update = XElement.Parse(OneNoteBacklinks.Edit(PageXml(""), LineXml("x")).UpdateXml!);
        Assert.Contains(">x<", update.Descendants(One + "T").Last().Value);
    }
}
