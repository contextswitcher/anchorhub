using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AnchorHub.Core;

/// <summary>An item of another application (a task, a category) that links to a OneNote page.</summary>
public sealed record Backlink(string Note, string Label, string Url, bool Done);

/// <summary>The validated stdin request of <c>ankd onenote-backlinks-sync</c>.</summary>
/// <param name="Icon">The application's icon as a Base64 PNG, or null.</param>
public sealed record BacklinkSyncRequest(string Source, string Name, string? Icon, IReadOnlyList<Backlink> Links);

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
/// <param name="DeleteObjectId">An outline to remove with <c>DeletePageContent</c> (it held only the line), or null.</param>
public sealed record BacklinkEdit(string? UpdateXml, string? DeleteObjectId)
{
    public static readonly BacklinkEdit None = new(null, null);
}

/// <summary>A page whose backlink line has to change: <see cref="Line"/> (the paragraph XML) null removes it.</summary>
public sealed record BacklinkWork(string Key, string Note, string? Line);

/// <summary>The links each source last sent, and the line last written to each page.</summary>
public sealed class BacklinkState
{
    public Dictionary<string, BacklinkSource> Sources { get; set; } = new();
    public Dictionary<string, BacklinkPage> Pages { get; set; } = new();
}

public sealed class BacklinkSource
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public List<Backlink> Links { get; set; } = [];
}

public sealed class BacklinkPage
{
    public string Note { get; set; } = "";
    public string? PageId { get; set; }

    /// <summary>SHA-256 of the line last written, so that an unchanged line is not written again.</summary>
    public string? Hash { get; set; }
}

/// <summary>
/// The decisions behind the backlink line at the top of a OneNote page: a borderless table with one
/// row per application — its icon, then its links back to the tasks and categories that link to the
/// page. Each application sends its complete set; a link it no longer sends disappears from the
/// page. Everything except the COM calls lives here, so it is unit tested.
/// </summary>
// [impl->req~ank.onenote.backlinks~2]
public static class OneNoteBacklinks
{
    public const string Namespace = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    /// <summary>The name of the <c>one:Meta</c> that marks the outline (the block) as AnchorHub's own.</summary>
    public const string Marker = "anchorhub-backlinks";

    /// <summary>The size of an icon on the page, in points.</summary>
    public const double IconSize = 12;

    /// <summary>The width of the column with the links, in points; longer rows wrap.</summary>
    public const double LinksWidth = 320;

    private const int MaxIconBytes = 64 * 1024;
    private static readonly XNamespace One = Namespace;
    private static readonly Regex SourceId = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The page a link names: its page GUID, else the whole link (a link without a page-id).</summary>
    public static string Key(string note) =>
        OneNoteLinkParts.Parse(note)?.PageGuid ?? note.Trim();

    /// <summary>
    /// Parses <c>{"source": "koppter", "name": "koppter", "icon": "&lt;Base64 PNG&gt;", "links": [{"note", "label", "url", "done"}]}</c>.
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
        var icon = root["icon"] is null ? null : Text(root, "icon");
        if (icon is not null) CheckIcon(icon);
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
        return new BacklinkSyncRequest(source, name, icon, links);
    }

    private static void CheckIcon(string icon)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(icon); }
        catch (FormatException) { throw new FormatException("icon must be Base64"); }
        if (bytes.Length > MaxIconBytes) throw new FormatException($"icon must be at most {MaxIconBytes} bytes");
        if (!bytes.AsSpan().StartsWith(PngSignature)) throw new FormatException("icon must be a PNG");
    }

    private static string Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : throw new FormatException($"{key} must be a non-empty string");

    /// <summary>
    /// The paragraph that carries the links of all sources to one page, or null when there are none:
    /// a borderless table with one row per source (ordered by name) — the source's icon, or its name
    /// when it sent none, then its links (open before done, then by label; done ones struck through).
    /// </summary>
    public static XElement? Line(IEnumerable<(BacklinkSource Source, Backlink Link)> links)
    {
        var rows = links
            .GroupBy(l => l.Source)
            .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => Row(g.Key, g.Select(l => l.Link)))
            .ToList();
        if (rows.Count == 0) return null;
        return new XElement(One + "OE",
            new XElement(One + "Table", new XAttribute("bordersVisible", "false"),
                new XElement(One + "Columns",
                    new XElement(One + "Column", new XAttribute("index", 0), new XAttribute("width", IconSize + 8), new XAttribute("isLocked", "true")),
                    new XElement(One + "Column", new XAttribute("index", 1), new XAttribute("width", LinksWidth), new XAttribute("isLocked", "true"))),
                rows));
    }

    private static XElement Row(BacklinkSource source, IEnumerable<Backlink> links)
    {
        var html = string.Join(" · ", links
            .OrderBy(l => l.Done)
            .ThenBy(l => l.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(l => l.Url, StringComparer.Ordinal)
            .Select(l =>
            {
                var text = WebUtility.HtmlEncode(l.Label);
                if (l.Done) text = $"<span style='text-decoration:line-through'>{text}</span>";
                return $"<a href=\"{WebUtility.HtmlEncode(l.Url)}\">{text}</a>";
            })
            .Distinct());
        var badge = source.Icon is null
            ? new XElement(One + "T", new XCData(WebUtility.HtmlEncode(source.Name)))
            : new XElement(One + "Image", new XAttribute("format", "png"),
                new XElement(One + "Size", new XAttribute("width", IconSize), new XAttribute("height", IconSize), new XAttribute("isSetByUser", "true")),
                new XElement(One + "Data", source.Icon));
        // OneNote keeps a column at least about 37 pt wide; aligned right, the icon sits next to its links.
        return new XElement(One + "Row",
            Cell(badge, "right"),
            Cell(new XElement(One + "T", new XCData(html)), "left"));
    }

    private static XElement Cell(XElement content, string alignment) =>
        new(One + "Cell", new XElement(One + "OEChildren",
            new XElement(One + "OE", new XAttribute("alignment", alignment), content)));

    /// <summary>
    /// Replaces the links of <paramref name="request"/>'s source in <paramref name="state"/> and returns
    /// the pages whose line differs from the one last written. With <paramref name="all"/>, every page
    /// known to the state is returned, which rewrites lines that were edited or deleted by hand.
    /// </summary>
    public static List<BacklinkWork> Plan(BacklinkState state, BacklinkSyncRequest request, bool all = false)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (state.Sources.TryGetValue(request.Source, out var old))
        {
            keys.UnionWith(old.Links.Select(l => Key(l.Note)));
            // A new icon or name changes every line of the source.
            if (old.Icon != request.Icon || old.Name != request.Name) keys.UnionWith(request.Links.Select(l => Key(l.Note)));
        }
        keys.UnionWith(request.Links.Select(l => Key(l.Note)));
        if (all)
        {
            keys.UnionWith(state.Pages.Keys);
            keys.UnionWith(state.Sources.Values.SelectMany(s => s.Links).Select(l => Key(l.Note)));
        }

        state.Sources[request.Source] = new BacklinkSource { Name = request.Name, Icon = request.Icon, Links = request.Links.ToList() };

        var work = new List<BacklinkWork>();
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            var linked = state.Sources.Values
                .SelectMany(s => s.Links.Where(l => Key(l.Note) == key).Select(l => (Source: s, Link: l)))
                .ToList();
            var line = Line(linked)?.ToString(SaveOptions.DisableFormatting);
            state.Pages.TryGetValue(key, out var page);
            if (line is null && page is null) continue;
            if (!all && page?.Hash == Hash(line)) continue;
            var note = linked.Count > 0 ? linked[0].Link.Note : page!.Note;
            work.Add(new BacklinkWork(key, note, line));
        }
        return work;
    }

    private static string? Hash(string? line) =>
        line is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));

    /// <summary>Records in <paramref name="state"/> that <paramref name="work"/> was written to <paramref name="pageId"/>.</summary>
    public static void Done(BacklinkState state, BacklinkWork work, string? pageId)
    {
        if (work.Line is null) state.Pages.Remove(work.Key);
        else state.Pages[work.Key] = new BacklinkPage { Note = work.Note, PageId = pageId, Hash = Hash(work.Line) };
    }

    /// <summary>
    /// Computes the change to a page (its XML from <c>GetPageContent</c>) that makes its backlink
    /// block hold <paramref name="line"/>, or removes the block when <paramref name="line"/> is null.
    /// The block is an outline of its own, marked with the <c>one:Meta</c> <see cref="Marker"/>, so that
    /// the content of the page never moves. A new block stands at the height of the title, to the right
    /// of it; an existing one keeps its position (the user may have moved it) and gets new content. The
    /// update carries only the page ID and that outline: OneNote leaves the outlines a page update does
    /// not name as they are.
    /// </summary>
    public static BacklinkEdit Edit(string pageXml, string? line)
    {
        var doc = XDocument.Parse(pageXml);
        var page = doc.Root ?? throw new FormatException("empty page XML");
        var existing = page.Elements(One + "Outline").FirstOrDefault(IsMarked);

        if (line is null)
        {
            var id = existing?.Attribute("objectID")?.Value;
            return id is null ? BacklinkEdit.None : new BacklinkEdit(null, id);
        }

        var outline = new XElement(One + "Outline");
        if (existing?.Attribute("objectID") is { } objectId) outline.Add(new XAttribute(objectId));
        outline.Add(
            existing?.Element(One + "Position") is { } position
                ? new XElement(position)
                : new XElement(One + "Position",
                    new XAttribute("x", Fmt(BlockLeft(page))), new XAttribute("y", Fmt(BlockTop)), new XAttribute("z", 0)),
            new XElement(One + "Meta", new XAttribute("name", Marker), new XAttribute("content", "1")),
            new XElement(One + "OEChildren", XElement.Parse(line)));

        var update = new XElement(One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", Namespace),
            new XAttribute("ID", page.Attribute("ID")?.Value ?? throw new FormatException("page without ID")),
            outline);
        return new BacklinkEdit(update.ToString(SaveOptions.DisableFormatting), null);
    }

    /// <summary>The top of a new block: the height of the page title.</summary>
    public const double BlockTop = 36;

    /// <summary>
    /// The left edge of a new block: right of the title, estimated from its length (the page XML does
    /// not tell the width of the title), at least 360 pt and at most 720 pt from the left.
    /// </summary>
    public static double BlockLeft(XElement page)
    {
        var title = string.Concat(page.Element(One + "Title")?.Descendants(One + "T").Select(t => t.Value) ?? []);
        var text = WebUtility.HtmlDecode(Regex.Replace(title, "<[^>]*>", ""));
        return Math.Clamp(36 + text.Length * 11 + 48, 360, 720);
    }

    private static string Fmt(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsMarked(XElement outline) =>
        outline.Elements(One + "Meta").Any(m => m.Attribute("name")?.Value == Marker);
}
