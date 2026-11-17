using System.Web;

namespace AnchorHub.Core;

/// <summary>
/// A link to one Outlook item:
/// <c>anchor://outlook/message/&lt;EntryID&gt;?x-store=&lt;StoreID&gt;&amp;title=&lt;subject&gt;&amp;v=1</c>.
/// The EntryID survives moves between folders of the same store; the StoreID
/// lets Outlook resolve it without searching every open store.
/// </summary>
// ponytail: EntryID breaks when a mail moves to another store (PST ↔ mailbox);
// add the Internet Message-ID as x-mid and a search fallback when that bites.
public sealed record OutlookAnchor(string EntryId, string? StoreId, string? Title)
{
    private const string Prefix = "anchor://outlook/message/";

    public string ToUri()
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(StoreId)) query.Add("x-store=" + Uri.EscapeDataString(StoreId));
        if (!string.IsNullOrEmpty(Title))   query.Add("title="   + Uri.EscapeDataString(Title));
        query.Add("v=1");
        return Prefix + Uri.EscapeDataString(EntryId) + "?" + string.Join('&', query);
    }

    public static OutlookAnchor? TryParse(string uri)
    {
        if (!uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri[Prefix.Length..];
        var q    = rest.IndexOf('?');
        var id   = Uri.UnescapeDataString(q < 0 ? rest : rest[..q]);
        if (id.Length == 0) return null;
        var query = HttpUtility.ParseQueryString(q < 0 ? "" : rest[(q + 1)..]);
        return new OutlookAnchor(id, query["x-store"], query["title"]);
    }
}
