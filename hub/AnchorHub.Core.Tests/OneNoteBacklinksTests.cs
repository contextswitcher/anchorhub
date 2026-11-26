using System.Xml.Linq;
using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

// [utest->req~ank.onenote.backlinks~1]
public class OneNoteBacklinksTests
{
    private const string PageGuid = "0123abcd-0000-4000-8000-000000000001";
    private const string Note =
        "onenote:https://d.docs.live.net/abc/Documents/My%20Notes/Projects.one#Fix%20NPE&section-id={AAAABBBB-0000-4000-8000-000000000002}&page-id={0123ABCD-0000-4000-8000-000000000001}&end";
    private static readonly XNamespace One = OneNoteBacklinks.Namespace;

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
    [InlineData("not json")]
    public void RejectsInvalidRequest(string json) =>
        Assert.Throws<FormatException>(() => OneNoteBacklinks.ParseSyncRequest(json));

    [Fact]
    public void RendersSortedEscapedAndStruckThrough()
    {
        var html = OneNoteBacklinks.Render([
            ("koppter", new Backlink(Note, "Call <dentist>", "koppter://task?a=1&b=2", Done: true)),
            ("ContextSwitcher", new Backlink(Note, "Fix NPE", "contextswitcher://task/fix", Done: false)),
        ]);
        Assert.Equal(
            "↩ <a href=\"contextswitcher://task/fix\">ContextSwitcher: Fix NPE</a> · " +
            "<a href=\"koppter://task?a=1&amp;b=2\"><span style='text-decoration:line-through'>koppter: Call &lt;dentist&gt;</span></a>",
            html);
        Assert.Null(OneNoteBacklinks.Render([]));
    }

    private static BacklinkSyncRequest Request(string source, params Backlink[] links) => new(source, source, links);

    [Fact]
    public void PlanWritesNewPagesAndSkipsUnchanged()
    {
        var state = new BacklinkState();
        var work = OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false)));
        var single = Assert.Single(work);
        Assert.Equal(PageGuid, single.Key);
        Assert.NotNull(single.Html);
        OneNoteBacklinks.Done(state, single, "{page}");

        Assert.Empty(OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false))));
        Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false)), all: true));
    }

    [Fact]
    public void PlanRemovesLineWhenTheLastLinkGoes()
    {
        var state = new BacklinkState();
        foreach (var w in OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false))))
            OneNoteBacklinks.Done(state, w, "{page}");

        var work = Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter")));
        Assert.Null(work.Html);
        Assert.Equal(Note, work.Note);
        OneNoteBacklinks.Done(state, work, null);
        Assert.Empty(state.Pages);
    }

    [Fact]
    public void PlanKeepsTheLinksOfOtherSources()
    {
        var state = new BacklinkState();
        foreach (var w in OneNoteBacklinks.Plan(state, Request("contextswitcher", new Backlink(Note, "Task", "contextswitcher://task/t", false))))
            OneNoteBacklinks.Done(state, w, "{page}");
        foreach (var w in OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false))))
            OneNoteBacklinks.Done(state, w, "{page}");

        var work = Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter")));
        Assert.Contains("contextswitcher: Task", work.Html);
        Assert.DoesNotContain("koppter", work.Html);
    }

    [Fact]
    public void PlanRewritesWhenATaskIsDone()
    {
        var state = new BacklinkState();
        foreach (var w in OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", false))))
            OneNoteBacklinks.Done(state, w, "{page}");
        var work = Assert.Single(OneNoteBacklinks.Plan(state, Request("koppter", new Backlink(Note, "A", "koppter://t", true))));
        Assert.Contains("line-through", work.Html);
    }

    private static string PageXml(string body) =>
        $$"""<one:Page xmlns:one="{{OneNoteBacklinks.Namespace}}" ID="{P}"><one:Title><one:OE objectID="t"><one:T><![CDATA[Title]]></one:T></one:OE></one:Title>{{body}}</one:Page>""";

    [Fact]
    public void EditInsertsLineIntoTopmostPlainOutline()
    {
        var xml = PageXml("""
            <one:Outline objectID="low"><one:Position x="36" y="300"/><one:OEChildren><one:OE objectID="l1"><one:T>low</one:T></one:OE></one:OEChildren></one:Outline>
            <one:Outline objectID="bank"><one:Position x="36" y="40"/><one:Meta name="omTaggingBank" content="1"/><one:OEChildren><one:OE><one:T>tags</one:T></one:OE></one:OEChildren></one:Outline>
            <one:Outline objectID="top"><one:Position x="36" y="86"/><one:OEChildren><one:OE objectID="t1"><one:T>first</one:T></one:OE></one:OEChildren></one:Outline>
            """);
        var edit = OneNoteBacklinks.Edit(xml, "↩ x");
        Assert.Null(edit.DeleteObjectId);
        var update = XElement.Parse(edit.UpdateXml!);
        Assert.Equal("{P}", update.Attribute("ID")!.Value);
        var outline = Assert.Single(update.Elements(One + "Outline"));
        Assert.Equal("top", outline.Attribute("objectID")!.Value);
        var oes = outline.Element(One + "OEChildren")!.Elements(One + "OE").ToList();
        Assert.Equal(2, oes.Count);
        Assert.Equal(OneNoteBacklinks.Marker, oes[0].Element(One + "Meta")!.Attribute("name")!.Value);
        Assert.Equal("↩ x", oes[0].Element(One + "T")!.Value);
        Assert.Equal("t1", oes[1].Attribute("objectID")!.Value);
    }

    [Fact]
    public void EditReplacesExistingLineInPlace()
    {
        var xml = PageXml($"""
            <one:Outline objectID="o"><one:OEChildren>
              <one:OE objectID="b"><one:Meta name="{OneNoteBacklinks.Marker}" content="1"/><one:T><![CDATA[old]]></one:T></one:OE>
              <one:OE objectID="t1"><one:T>first</one:T></one:OE>
            </one:OEChildren></one:Outline>
            """);
        var outline = XElement.Parse(OneNoteBacklinks.Edit(xml, "new").UpdateXml!).Element(One + "Outline")!;
        var line = outline.Descendants(One + "OE").First();
        Assert.Equal("b", line.Attribute("objectID")!.Value);
        Assert.Equal("new", Assert.Single(line.Elements(One + "T")).Value);
        Assert.Equal(2, outline.Descendants(One + "OE").Count());
    }

    [Fact]
    public void EditDeletesLineOrDoesNothing()
    {
        var marked = PageXml($"""<one:Outline><one:OEChildren><one:OE objectID="b"><one:Meta name="{OneNoteBacklinks.Marker}" content="1"/><one:T>x</one:T></one:OE></one:OEChildren></one:Outline>""");
        Assert.Equal(new BacklinkEdit(null, "b"), OneNoteBacklinks.Edit(marked, null));
        Assert.Equal(BacklinkEdit.None, OneNoteBacklinks.Edit(PageXml(""), null));
    }

    [Fact]
    public void EditCreatesOutlineOnEmptyPage()
    {
        var update = XElement.Parse(OneNoteBacklinks.Edit(PageXml(""), "x").UpdateXml!);
        Assert.Equal("x", update.Descendants(One + "T").Single().Value);
    }
}
