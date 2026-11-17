using AnchorHub.Core;
using Xunit;

namespace AnchorHub.Core.Tests;

public class CitationPageParserTests
{
    private const string Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    private static string Cell(string text) =>
        $"<one:Cell><one:OEChildren><one:OE><one:T><![CDATA[{text}]]></one:T></one:OE></one:OEChildren></one:Cell>";

    private static string Table(params (string Label, string Value)[] rows)
    {
        var rowsXml = string.Concat(rows.Select(r =>
            $"<one:Row>{Cell(r.Label)}{Cell(r.Value)}</one:Row>"));
        return $"""
          <one:Outline><one:OEChildren><one:OE>
            <one:Table>
              <one:Columns>
                <one:Column index="0" width="100"/>
                <one:Column index="1" width="200"/>
              </one:Columns>
              {rowsXml}
            </one:Table>
          </one:OE></one:OEChildren></one:Outline>
          """;
    }

    private static string Page(string body, string title = "Test Page") => $"""
        <one:Page xmlns:one="{Ns}">
          <one:Title>
            <one:OE><one:T><![CDATA[{title}]]></one:T></one:OE>
          </one:Title>
          {body}
        </one:Page>
        """;

    [Fact]
    public void HappyPath_TitleAndCitationKeyAndIdentifier()
    {
        var xml = Page(Table(
            ("citationkey", "Smith2024"),
            ("identifier",  "doi:10.1000/foo")), "An Important Paper");

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Valid, r.Status);
        Assert.Empty(r.Errors);
        Assert.NotNull(r.Page);
        Assert.Equal("An Important Paper", r.Page!.Title);
        Assert.Equal("Smith2024", r.Page.CitationKey);
        Assert.Single(r.Page.Identifiers);
        Assert.Equal("doi", r.Page.Primary.Scheme);
        Assert.Equal("10.1000/foo", r.Page.Primary.Value);
    }

    [Fact]
    public void MultipleIdentifiers_FirstIsPrimary()
    {
        var xml = Page(Table(
            ("citationkey", "Knuth1968"),
            ("identifier",  "isbn:978-0-201-89683-1"),
            ("identifier",  "doi:10.5555/foo")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Valid, r.Status);
        Assert.Equal(2, r.Page!.Identifiers.Count);
        Assert.Equal("isbn", r.Page.Primary.Scheme);
        Assert.Equal("doi",  r.Page.Identifiers[1].Scheme);
    }

    [Fact]
    public void NotACitationPage_NoTable()
    {
        var xml = Page($"""
          <one:Outline xmlns:one="{Ns}"><one:OEChildren><one:OE>
            <one:T><![CDATA[just notes]]></one:T>
          </one:OE></one:OEChildren></one:Outline>
          """);

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.NotACitationPage, r.Status);
        Assert.Null(r.Page);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void NotACitationPage_TwoColTableWithoutMarkerLabels()
    {
        // 2-col table with unrelated labels should not match.
        var xml = Page(Table(
            ("Author", "Knuth"),
            ("Year",   "1968")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.NotACitationPage, r.Status);
    }

    [Fact]
    public void Invalid_HasCitationKeyButNoIdentifier()
    {
        var xml = Page(Table(("citationkey", "Smith2024")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Invalid, r.Status);
        Assert.Contains(r.Errors, e => e.Contains("identifier"));
    }

    [Fact]
    public void Invalid_HasIdentifierButNoCitationKey()
    {
        var xml = Page(Table(("identifier", "doi:10.1/x")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Invalid, r.Status);
        Assert.Contains(r.Errors, e => e.Contains("citationkey"));
    }

    [Fact]
    public void Invalid_BadIdentifierScheme()
    {
        var xml = Page(Table(
            ("citationkey", "Smith2024"),
            ("identifier",  "nope:foo")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Invalid, r.Status);
        Assert.Contains(r.Errors, e => e.Contains("scheme"));
    }

    [Fact]
    public void Invalid_BadIdentifierFormat()
    {
        var xml = Page(Table(
            ("citationkey", "Smith2024"),
            ("identifier",  "no-colon-here")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Invalid, r.Status);
    }

    [Fact]
    public void Invalid_DuplicateCitationKey()
    {
        var xml = Page(Table(
            ("citationkey", "Smith2024"),
            ("citationkey", "Smith2025"),
            ("identifier",  "doi:10.1/x")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Invalid, r.Status);
        Assert.Contains(r.Errors, e => e.Contains("multiple"));
    }

    [Theory]
    [InlineData("Citationkey")]
    [InlineData("CITATIONKEY")]
    [InlineData(" citationkey ")]
    [InlineData("citationkey:")]
    public void LabelsAreNormalised(string label)
    {
        var xml = Page(Table(
            (label,        "Smith2024"),
            ("identifier", "doi:10.1/x")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Valid, r.Status);
        Assert.Equal("Smith2024", r.Page!.CitationKey);
    }

    [Fact]
    public void HtmlMarkupInCellsIsStripped()
    {
        // OneNote often wraps cell text in style spans; the parser must see plain text.
        var xml = Page(Table(
            ("<span style=\"font-weight:bold\">citationkey</span>", "Smith2024"),
            ("identifier", "doi:<span>10.1/x</span>")));

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Valid, r.Status);
        Assert.Equal("Smith2024",  r.Page!.CitationKey);
        Assert.Equal("10.1/x",     r.Page.Primary.Value);
    }

    [Fact]
    public void MetadataTable_FoundEvenWhenNotFirst()
    {
        // A non-citation 2-col table appears first; the parser must skip it.
        var head = Table(("Section", "Notes"), ("Author", "Knuth"));
        var meta = Table(
            ("citationkey", "Knuth1968"),
            ("identifier",  "isbn:978-0-201-89683-1"));

        var xml = Page(head + meta);

        var r = CitationPageParser.Parse(xml);

        Assert.Equal(CitationPageStatus.Valid, r.Status);
        Assert.Equal("Knuth1968", r.Page!.CitationKey);
    }
}
