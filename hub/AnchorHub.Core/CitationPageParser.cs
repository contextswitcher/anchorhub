using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AnchorHub.Core;

public enum CitationPageStatus
{
    /// <summary>No metadata table found — page is not a citation page.</summary>
    NotACitationPage,

    /// <summary>Metadata table found and all required rows are present and well-formed.</summary>
    Valid,

    /// <summary>Metadata table found but one or more validation errors prevent parsing.</summary>
    Invalid,
}

public sealed record CitationPageParseResult(
    CitationPageStatus Status,
    CitationPage? Page,
    IReadOnlyList<string> Errors);

/// <summary>
/// Parses a OneNote page's XML (as returned by <c>OneNote.Application.GetPageContent</c>)
/// to extract citation metadata. Pure — no COM, no I/O — fully unit-testable.
/// </summary>
public static class CitationPageParser
{
    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);

    public static CitationPageParseResult Parse(string xml)
    {
        var doc  = XDocument.Parse(xml);
        var root = doc.Root ?? throw new ArgumentException("XML has no root element", nameof(xml));
        var ns   = root.Name.Namespace;

        var title     = ReadTitle(root, ns);
        var metaTable = FindMetadataTable(root, ns);

        if (metaTable is null)
            return new(CitationPageStatus.NotACitationPage, null, Array.Empty<string>());

        string? citationKey = null;
        var identifiers     = new List<CitationIdentifier>();
        var errors          = new List<string>();
        var citationKeySeen = 0;

        foreach (var row in metaTable.Elements(ns + "Row"))
        {
            var cells = row.Elements(ns + "Cell").ToList();
            if (cells.Count < 2) continue;

            var label    = NormalizeLabel(ReadCellText(cells[0], ns));
            var rawValue = ReadCellText(cells[1], ns);

            if (label == "citationkey")
            {
                citationKeySeen++;
                if (citationKeySeen > 1)
                    errors.Add("multiple 'citationkey' rows; expected exactly one");
                else if (string.IsNullOrWhiteSpace(rawValue))
                    errors.Add("'citationkey' row has empty value");
                else
                    citationKey = rawValue.Trim();
            }
            else if (label == "identifier")
            {
                if (CitationIdentifier.TryParse(rawValue, out var id, out var err))
                    identifiers.Add(id!);
                else
                    errors.Add($"'identifier' row: {err}");
            }
        }

        if (citationKey is null && citationKeySeen == 0)
            errors.Add("missing required 'citationkey' row");
        if (identifiers.Count == 0)
            errors.Add("missing required 'identifier' row");

        if (errors.Count > 0)
            return new(CitationPageStatus.Invalid, null, errors);

        return new(
            CitationPageStatus.Valid,
            new CitationPage(title, citationKey!, identifiers),
            Array.Empty<string>());
    }

    private static string ReadTitle(XElement root, XNamespace ns)
    {
        var titleEl = root.Element(ns + "Title");
        if (titleEl is null) return string.Empty;
        var text = string.Concat(titleEl.Descendants(ns + "T").Select(t => t.Value));
        return StripHtml(text).Trim();
    }

    // First two-column table that contains either a citationkey or identifier
    // row. Both labels mark the page as a citation page — using only one of the
    // two would silently hide the missing-row validation error.
    private static XElement? FindMetadataTable(XElement root, XNamespace ns)
    {
        foreach (var table in root.Descendants(ns + "Table"))
        {
            var columns = table.Element(ns + "Columns")?.Elements(ns + "Column").ToList();
            if (columns is null || columns.Count != 2) continue;

            foreach (var row in table.Elements(ns + "Row"))
            {
                var firstCell = row.Elements(ns + "Cell").FirstOrDefault();
                var label     = NormalizeLabel(ReadCellText(firstCell, ns));
                if (label == "citationkey" || label == "identifier")
                    return table;
            }
        }
        return null;
    }

    private static string ReadCellText(XElement? cell, XNamespace ns)
    {
        if (cell is null) return string.Empty;
        var text = string.Concat(cell.Descendants(ns + "T").Select(t => t.Value));
        return StripHtml(text).Trim();
    }

    private static string NormalizeLabel(string raw)
    {
        var t = raw.Trim();
        if (t.EndsWith(':')) t = t[..^1].TrimEnd();
        return t.ToLowerInvariant();
    }

    // OneNote stores cell content with inline HTML (bold, italic, colour spans)
    // inside the CDATA of <one:T>. Strip tags so labels and values match plainly.
    private static string StripHtml(string s) => HtmlTag.Replace(s, string.Empty);
}
