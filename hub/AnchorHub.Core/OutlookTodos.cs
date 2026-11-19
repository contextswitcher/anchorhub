using System.Text.Json.Nodes;

namespace AnchorHub.Core;

/// <summary>
/// Shapes the raw To-Do List items that the Outlook COM script returns into
/// the JSON array that <c>ankd outlook-todos</c> prints.
/// </summary>
// [impl->req~ank.outlook.todos~1]
public static class OutlookTodos
{
    // Outlook stores "no date" as 4501-01-01 in every date property.
    private const string NoDate = "4501-01-01";

    /// <summary>A <c>yyyy-MM-dd</c> date, or null for Outlook's "no date" and empty values.</summary>
    public static string? Date(string? value) =>
        string.IsNullOrEmpty(value) || value.StartsWith(NoDate, StringComparison.Ordinal) ? null : value[..Math.Min(10, value.Length)];

    /// <summary>An ISO 8601 timestamp, or null for Outlook's "no date" and empty values.</summary>
    public static string? Timestamp(string? value) =>
        string.IsNullOrEmpty(value) || value.StartsWith(NoDate, StringComparison.Ordinal) ? null : value;

    /// <summary>
    /// The output array: one object per raw item with the fields entryId, storeId,
    /// store, kind, subject, due, start, received, sender and anchor, in this order.
    /// A single object (PowerShell's rendering of a one-element list) counts as one item.
    /// </summary>
    public static JsonArray Shape(JsonNode? raw)
    {
        var items = raw switch
        {
            JsonArray a  => a.OfType<JsonObject>(),
            JsonObject o => [o],
            _            => [],
        };
        var result = new JsonArray();
        foreach (var item in items)
        {
            var entryId = (string?)item["entryId"];
            if (string.IsNullOrEmpty(entryId)) continue;
            var storeId = (string?)item["storeId"];
            var subject = (string?)item["subject"] ?? "";
            var kind    = (string?)item["kind"];
            result.Add(new JsonObject
            {
                ["entryId"]  = entryId,
                ["storeId"]  = storeId,
                ["store"]    = (string?)item["store"],
                ["kind"]     = kind is "mail" or "task" ? kind : "other",
                ["subject"]  = subject,
                ["due"]      = Date((string?)item["due"]),
                ["start"]    = Date((string?)item["start"]),
                ["received"] = Timestamp((string?)item["received"]),
                ["sender"]   = string.IsNullOrEmpty((string?)item["sender"]) ? null : (string?)item["sender"],
                ["anchor"]   = new OutlookAnchor(entryId, storeId, subject).ToUri(),
            });
        }
        return result;
    }
}
