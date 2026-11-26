using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AnchorHub.Core;

/// <summary>An item of another application (a task, a category) that links to a OneNote page.</summary>
public sealed record Backlink(string Note, string Label, string Url, bool Done);

/// <summary>The validated stdin request of <c>ankd onenote-backlinks-sync</c>.</summary>
public sealed record BacklinkSyncRequest(string Source, string Name, IReadOnlyList<Backlink> Links);

/// <summary>The parts of an <c>onenote:</c> page link that find the page again.</summary>
public sealed record OneNoteLinkParts(string SectionPath, string? SectionGuid, string? PageGuid)
{
    private static readonly Regex SectionId = new(@"section-id=\{?([0-9a-fA-F-]{36})\}?", RegexOptions.CultureInvariant);
    private static readonly Regex PageId = new(@"page-id=\{?([0-9a-fA-F-]{36})\}?", RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits <c>onenote:https://d.docs.live.net/…/Section.one#Title&amp;section-id={…}&amp;page-id={…}&amp;end</c>
    /// into the section path (unescaped) and the lowercase GUIDs. Null for anything that is not an
    /// <c>onenote:</c> link.
    /// </summary>
    public static OneNoteLinkParts? Parse(string note)
    {
        var link = note.Trim();
        if (!link.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) return null;
        link = link["onenote:".Length..];
        var hash = link.IndexOf('#');
        var path = hash < 0 ? link : link[..hash];
        var fragment = hash < 0 ? "" : link[hash..];
        string? Guid(Regex r) => r.Match(fragment) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
        return new OneNoteLinkParts(Uri.UnescapeDataString(path), Guid(SectionId), Guid(PageId));
    }
}

/// <summary>What has to be sent to OneNote to bring the backlink line of a page up to date.</summary>
/// <param name="UpdateXml">A page XML for <c>UpdatePageContent</c>, or null.</param>
/// <param name="DeleteObjectId">The object ID of the line to remove with <c>DeletePageContent</c>, or null.</param>
public sealed record BacklinkEdit(string? UpdateXml, string? DeleteObjectId)
{
    public static readonly BacklinkEdit None = new(null, null);
}

/// <summary>A page whose backlink line has to change: <see cref="Html"/> null removes the line.</summary>
public sealed record BacklinkWork(string Key, string Note, string? Html);

/// <summary>The links each source last sent, and the line last written to each page.</summary>
public sealed class BacklinkState
{
    public Dictionary<string, BacklinkSource> Sources { get; set; } = new();
    public Dictionary<string, BacklinkPage> Pages { get; set; } = new();
}

public sealed class BacklinkSource
{
    public string Name { get; set; } = "";
    public List<Backlink> Links { get; set; } = [];
}

public sealed class BacklinkPage
{
    public string Note { get; set; } = "";
    public string? PageId { get; set; }
    public string Html { get; set; } = "";
}

/// <summary>
/// The decisions behind the backlink line at the top of a OneNote page: one line of links back to
/// the tasks and categories of other applications that link to the page. Each application sends
/// its complete set; a link it no longer sends disappears from the page. Everything except the COM
/// calls lives here, so it is unit tested.
/// </summary>
// [impl->req~ank.onenote.backlinks~1]
public static class OneNoteBacklinks
{
    public const string Namespace = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    /// <summary>The name of the <c>one:Meta</c> that marks the line as AnchorHub's own.</summary>
    public const string Marker = "anchorhub-backlinks";

    private static readonly XNamespace One = Namespace;
    private static readonly Regex SourceId = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant);

    /// <summary>The page a link names: its page GUID, else the whole link (a link without a page-id).</summary>
    public static string Key(string note) =>
        OneNoteLinkParts.Parse(note)?.PageGuid ?? note.Trim();

    /// <summary>
    /// Parses <c>{"source": "koppter", "name": "koppter", "links": [{"note", "label", "url", "done"}]}</c>.
    /// The request is rejected as a whole when it is not of this form.
    /// </summary>
    public static BacklinkSyncRequest ParseSyncRequest(string json)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("not a JSON object"); }
        catch (JsonException e) { throw new FormatException(e.Message); }

        var source = Text(root, "source");
        if (!SourceId.IsMatch(source)) throw new FormatException("source must match [a-z0-9-]+");
        var name = root["name"] is null ? source : Text(root, "name");
        if (root["links"] is not JsonArray array) throw new FormatException("links must be an array");

        var links = new List<Backlink>();
        foreach (var node in array)
        {
            if (node is not JsonObject o) throw new FormatException("a link must be an object");
            var note = Text(o, "note");
            if (OneNoteLinkParts.Parse(note) is null) throw new FormatException($"not an onenote: link: {note}");
            var url = Text(o, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new FormatException($"not an absolute URL: {url}");
            var done = o["done"] switch
            {
                null => false,
                JsonValue v when v.TryGetValue(out bool b) => b,
                _ => throw new FormatException("done must be a boolean"),
            };
            links.Add(new Backlink(note.Trim(), Text(o, "label"), url, done));
        }
        return new BacklinkSyncRequest(source, name, links);
    }

    private static string Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : throw new FormatException($"{key} must be a non-empty string");

    /// <summary>
    /// The HTML of the line for the links of all sources to one page, or null when there are none.
    /// Sources are ordered by name, links by open before done, then by label, so that the same set
    /// gives the same line.
    /// </summary>
    public static string? Render(IEnumerable<(string Name, Backlink Link)> links)
    {
        var parts = links
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Link.Done)
            .ThenBy(l => l.Link.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(l => l.Link.Url, StringComparer.Ordinal)
            .Select(l =>
            {
                var text = WebUtility.HtmlEncode($"{l.Name}: {l.Link.Label}");
                if (l.Link.Done) text = $"<span style='text-decoration:line-through'>{text}</span>";
                return $"<a href=\"{WebUtility.HtmlEncode(l.Link.Url)}\">{text}</a>";
            })
            .Distinct()
            .ToList();
        return parts.Count == 0 ? null : "↩ " + string.Join(" · ", parts);
    }

    /// <summary>
    /// Replaces the links of <paramref name="request"/>'s source in <paramref name="state"/> and returns
    /// the pages whose line differs from the one last written. With <paramref name="all"/>, every page
    /// known to the state is returned, which rewrites lines that were edited or deleted by hand.
    /// </summary>
    public static List<BacklinkWork> Plan(BacklinkState state, BacklinkSyncRequest request, bool all = false)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (state.Sources.TryGetValue(request.Source, out var old))
            keys.UnionWith(old.Links.Select(l => Key(l.Note)));
        keys.UnionWith(request.Links.Select(l => Key(l.Note)));
        if (all)
        {
            keys.UnionWith(state.Pages.Keys);
            keys.UnionWith(state.Sources.Values.SelectMany(s => s.Links).Select(l => Key(l.Note)));
        }

        state.Sources[request.Source] = new BacklinkSource { Name = request.Name, Links = request.Links.ToList() };

        var work = new List<BacklinkWork>();
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            var linked = state.Sources.Values
                .SelectMany(s => s.Links.Where(l => Key(l.Note) == key).Select(l => (s.Name, Link: l)))
                .ToList();
            var html = Render(linked);
            state.Pages.TryGetValue(key, out var page);
            if (!all && (page?.Html ?? (string?)null) == html) continue;
            if (html is null && page is null) continue;
            var note = linked.Count > 0 ? linked[0].Link.Note : page!.Note;
            work.Add(new BacklinkWork(key, note, html));
        }
        return work;
    }

    /// <summary>Records in <paramref name="state"/> that <paramref name="work"/> was written to <paramref name="pageId"/>.</summary>
    public static void Done(BacklinkState state, BacklinkWork work, string? pageId)
    {
        if (work.Html is null) state.Pages.Remove(work.Key);
        else state.Pages[work.Key] = new BacklinkPage { Note = work.Note, PageId = pageId, Html = work.Html };
    }

    /// <summary>
    /// Computes the change to a page (its XML from <c>GetPageContent</c>) that makes its backlink line
    /// <paramref name="html"/>, or removes the line when <paramref name="html"/> is null. A new line
    /// becomes the first paragraph of the topmost outline that is not one of OneMore's own (those carry
    /// a <c>one:Meta</c>); a page without such an outline gets a new one. Nothing else on the page is
    /// touched: the update carries only the page ID and the one outline that holds the line.
    /// </summary>
    public static BacklinkEdit Edit(string pageXml, string? html)
    {
        var doc = XDocument.Parse(pageXml);
        var page = doc.Root ?? throw new FormatException("empty page XML");
        var existing = page.Descendants(One + "OE").FirstOrDefault(IsMarked);

        if (html is null)
        {
            var id = existing?.Attribute("objectID")?.Value;
            return id is null ? BacklinkEdit.None : new BacklinkEdit(null, id);
        }

        XElement outline;
        if (existing is not null)
        {
            outline = existing.Ancestors(One + "Outline").First();
            existing.Elements(One + "T").Remove();
            existing.Add(new XElement(One + "T", new XCData(html)));
        }
        else
        {
            var line = new XElement(One + "OE",
                new XElement(One + "Meta", new XAttribute("name", Marker), new XAttribute("content", "1")),
                new XElement(One + "T", new XCData(html)));
            outline = page.Elements(One + "Outline")
                .Where(o => !o.Elements(One + "Meta").Any() && o.Element(One + "OEChildren") is not null)
                .OrderBy(Top)
                .FirstOrDefault()
                ?? new XElement(One + "Outline", new XElement(One + "OEChildren"));
            outline.Element(One + "OEChildren")!.AddFirst(line);
        }

        var update = new XElement(One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", Namespace),
            new XAttribute("ID", page.Attribute("ID")?.Value ?? throw new FormatException("page without ID")),
            new XElement(outline));
        return new BacklinkEdit(update.ToString(SaveOptions.DisableFormatting), null);
    }

    private static bool IsMarked(XElement oe) =>
        oe.Elements(One + "Meta").Any(m => m.Attribute("name")?.Value == Marker);

    private static double Top(XElement outline) =>
        double.TryParse(outline.Element(One + "Position")?.Attribute("y")?.Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? y
            : double.MaxValue;
}
