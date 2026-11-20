using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnchorHub.Core;

/// <summary>A time block that koppter wants in the Outlook calendar.</summary>
public sealed record KoppterBlock(string Id, DateTime Start, DateTime End, string Subject, string Body);

/// <summary>The validated stdin request of <c>ankd outlook-blocks-sync</c>.</summary>
public sealed record KoppterSyncRequest(string? Calendar, DateTime From, DateTime To, IReadOnlyList<KoppterBlock> Blocks);

/// <summary>A calendar item as the Outlook COM script lists it.</summary>
public sealed record CalendarItem(
    string EntryId,
    string? BlockId,
    string? Categories,
    bool IsRecurring,
    int MeetingStatus,
    DateTime Start,
    DateTime End,
    string Subject,
    bool AllDay = false,
    int BusyStatus = 2);

/// <summary>What a sync does: items to delete, blocks to create, own items that stay.</summary>
public sealed record KoppterSyncPlan(
    IReadOnlyList<CalendarItem> ToDelete,
    IReadOnlyList<KoppterBlock> ToAdd,
    IReadOnlyList<CalendarItem> Kept);

/// <summary>
/// The decisions behind the koppter time blocks in the Outlook calendar.
/// Everything that decides whether an item may be deleted lives here, so it
/// is unit tested; the COM script only lists items and carries out the plan.
/// </summary>
public static class KoppterBlocks
{
    public const string Category = "koppter";
    public const string PropertyName = "KoppterBlockId";
    public const string SubjectSuffix = " [koppter]";
    public const string TimeFormat = "yyyy-MM-ddTHH:mm";
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// True when the category list (separated by commas or semicolons) contains
    /// <c>koppter</c>, compared trimmed and case-insensitively.
    /// </summary>
    public static bool HasCategory(string? categories) =>
        !string.IsNullOrEmpty(categories) &&
        categories.Split([',', ';']).Any(c => string.Equals(c.Trim(), Category, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An item belongs to koppter only when it carries both markers — a non-blank
    /// <c>KoppterBlockId</c> user property and the <c>koppter</c> category — and is
    /// neither recurring nor a meeting. Subject and body never count.
    /// </summary>
    // [impl->req~ank.outlook.blocks-own~1]
    public static bool IsOwn(string? blockId, string? categories, bool isRecurring, int meetingStatus) =>
        !string.IsNullOrWhiteSpace(blockId) && HasCategory(categories) && !isRecurring && meetingStatus == 0;

    public static bool IsOwn(CalendarItem item) =>
        IsOwn(item.BlockId, item.Categories, item.IsRecurring, item.MeetingStatus);

    /// <summary>
    /// Own items in [from, to) whose block id is wanted with the same start, end and
    /// subject are kept (the first one per id); all other own items in the window are
    /// deleted; wanted blocks without a kept item are added. Items that are not own,
    /// or start outside the window, are never in the result.
    /// </summary>
    // [impl->req~ank.outlook.blocks-sync~1]
    public static KoppterSyncPlan Plan(IEnumerable<CalendarItem> existing, DateTime from, DateTime to, IReadOnlyList<KoppterBlock> wanted)
    {
        var byId = new Dictionary<string, KoppterBlock>(StringComparer.Ordinal);
        foreach (var b in wanted) byId.TryAdd(b.Id, b);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var toDelete = new List<CalendarItem>();
        var kept = new List<CalendarItem>();
        var own = existing
            .Where(i => IsOwn(i) && i.Start >= from && i.Start < to)
            .OrderBy(i => i.Start).ThenBy(i => i.EntryId, StringComparer.Ordinal);
        foreach (var item in own)
        {
            if (byId.TryGetValue(item.BlockId!, out var b) && !matched.Contains(b.Id) &&
                item.Start == b.Start && item.End == b.End && item.Subject == b.Subject)
            {
                matched.Add(b.Id);
                kept.Add(item);
            }
            else toDelete.Add(item);
        }
        var toAdd = wanted.Where(b => !matched.Contains(b.Id)).ToList();
        return new KoppterSyncPlan(toDelete, toAdd, kept);
    }

    /// <summary>
    /// Parses and validates the sync request. Throws <see cref="FormatException"/>
    /// for the whole request when any part is invalid, so nothing is changed.
    /// </summary>
    // [impl->req~ank.outlook.blocks-sync~1]
    public static KoppterSyncRequest ParseSyncRequest(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new FormatException($"not valid JSON: {ex.Message}"); }
        if (root is not JsonObject o) throw new FormatException("the request is not a JSON object");

        string? calendar = o["calendar"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) => s,
            _ => throw new FormatException("calendar must be null or a non-empty string"),
        };
        var from = Time(o["from"], "from");
        var to = Time(o["to"], "to");
        if (from >= to) throw new FormatException("from must be before to");
        if (o["blocks"] is not JsonArray arr) throw new FormatException("blocks must be an array");

        var blocks = new List<KoppterBlock>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < arr.Count; i++)
        {
            var where = $"blocks[{i}]";
            if (arr[i] is not JsonObject b) throw new FormatException($"{where} is not an object");
            var id = Text(b["id"], $"{where}.id", required: true);
            if (string.IsNullOrWhiteSpace(id)) throw new FormatException($"{where}.id is empty");
            if (!ids.Add(id)) throw new FormatException($"{where}.id \"{id}\" occurs more than once");
            var start = Time(b["start"], $"{where}.start");
            var end = Time(b["end"], $"{where}.end");
            if (start >= end) throw new FormatException($"{where}: start must be before end");
            if (start < from || start >= to) throw new FormatException($"{where}: start lies outside [from, to)");
            blocks.Add(new KoppterBlock(id, start, end,
                Text(b["subject"], $"{where}.subject", required: true),
                Text(b["body"], $"{where}.body", required: false)));
        }
        return new KoppterSyncRequest(calendar, from, to, blocks);
    }

    /// <summary>A <c>yyyy-MM-ddTHH:mm</c> wall-clock time, or null when it has another form.</summary>
    public static DateTime? ParseTime(string? value) =>
        DateTime.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>A <c>yyyy-MM-dd</c> date (at 00:00), or null when it has another form.</summary>
    public static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public static string FormatTime(DateTime t) => t.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTime Time(JsonNode? node, string name) =>
        node is JsonValue v && v.TryGetValue(out string? s) && ParseTime(s) is { } t
            ? t
            : throw new FormatException($"{name} must be a string of the form {TimeFormat}");

    private static string Text(JsonNode? node, string name, bool required) => node switch
    {
        null when !required => "",
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => throw new FormatException($"{name} must be a string"),
    };

    /// <summary>
    /// The calendar items that the COM script listed, parsed; a single object
    /// (PowerShell's rendering of a one-element list) counts as one item.
    /// Entries without EntryID or with unreadable times are dropped.
    /// </summary>
    public static IReadOnlyList<CalendarItem> ParseItems(JsonNode? raw)
    {
        var nodes = raw switch
        {
            JsonArray a => a.OfType<JsonObject>(),
            JsonObject o => [o],
            _ => [],
        };
        var result = new List<CalendarItem>();
        foreach (var n in nodes)
        {
            var entryId = (string?)n["entryId"];
            if (ParseTime((string?)n["start"]) is not { } start || ParseTime((string?)n["end"]) is not { } end) continue;
            result.Add(new CalendarItem(
                entryId ?? "",
                (string?)n["blockId"],
                (string?)n["categories"],
                n["isRecurring"] is JsonValue r && r.TryGetValue(out bool rec) ? rec : true,
                n["meetingStatus"] is JsonValue m && m.TryGetValue(out int ms) ? ms : -1,
                start, end,
                (string?)n["subject"] ?? "",
                n["allDay"] is JsonValue d && d.TryGetValue(out bool ad) && ad,
                n["busyStatus"] is JsonValue s && s.TryGetValue(out int bs) ? bs : 2));
        }
        return result;
    }

    /// <summary>Outlook's <c>OlBusyStatus</c> as the name koppter reads.</summary>
    public static string BusyStatusName(int status) => status switch
    {
        0 => "free",
        1 => "tentative",
        3 => "oof",
        4 => "elsewhere",
        _ => "busy",
    };

    /// <summary>
    /// The items overlapping [from, to), ordered by start, without koppter's own
    /// blocks and without items whose subject ends with <c> [koppter]</c>.
    /// <paramref name="colors"/> maps the session's category names to their
    /// <c>OlCategoryColor</c>.
    /// </summary>
    // [impl->req~ank.outlook.busy~2]
    public static JsonArray Busy(IEnumerable<CalendarItem> items, DateTime from, DateTime to,
        IReadOnlyDictionary<string, int>? colors = null)
    {
        colors ??= new Dictionary<string, int>();
        var result = new JsonArray();
        foreach (var i in items
                     .Where(i => i.Start < to && i.End > from && !IsOwn(i) &&
                                 !i.Subject.EndsWith(SubjectSuffix, StringComparison.Ordinal))
                     .OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            result.Add(new JsonObject
            {
                ["start"] = FormatTime(i.Start),
                ["end"] = FormatTime(i.End),
                ["allDay"] = i.AllDay,
                ["busyStatus"] = BusyStatusName(i.BusyStatus),
                ["subject"] = i.Subject,
                // An occurrence of a series carries the EntryID of the series master.
                ["entryId"] = i.EntryId,
                ["recurring"] = i.IsRecurring,
                ["sideline"] = OutlookCategories.Contains(i.Categories, OutlookCategories.Sideline),
                ["color"] = OutlookCategories.DisplayColor(i.Categories, colors),
            });
        }
        return result;
    }
}
