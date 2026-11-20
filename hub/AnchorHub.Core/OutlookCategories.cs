namespace AnchorHub.Core;

/// <summary>
/// The category list of an Outlook item (its <c>Categories</c> string) and the
/// colors of Outlook's categories. Entries are separated by commas or semicolons
/// and compared trimmed and case-insensitively.
/// </summary>
public static class OutlookCategories
{
    /// <summary>The category that marks an appointment as SkedPal-style "Sideline": shown, but planned as free.</summary>
    public const string Sideline = "Sideline";

    private static readonly char[] Separators = [',', ';'];

    public static IReadOnlyList<string> Split(string? categories) =>
        string.IsNullOrEmpty(categories)
            ? []
            : categories.Split(Separators).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

    public static bool Contains(string? categories, string name) =>
        Split(categories).Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The list with <paramref name="name"/> appended, or <paramref name="categories"/>
    /// itself when it already contains the name. The separator already used in the
    /// list is kept; a list without one gets <paramref name="separator"/>.
    /// </summary>
    // [impl->req~ank.outlook.sideline~1]
    public static string Add(string? categories, string name, char separator = ',')
    {
        if (Contains(categories, name)) return categories!;
        var entries = Split(categories).Append(name);
        return string.Join(SeparatorOf(categories, separator) + " ", entries);
    }

    /// <summary>
    /// The list without any entry equal to <paramref name="name"/>, or
    /// <paramref name="categories"/> itself (null becomes "") when there is none.
    /// </summary>
    // [impl->req~ank.outlook.sideline~1]
    public static string Remove(string? categories, string name)
    {
        if (!Contains(categories, name)) return categories ?? "";
        var entries = Split(categories).Where(c => !string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        return string.Join(SeparatorOf(categories, ',') + " ", entries);
    }

    /// <summary>The list with the <c>Sideline</c> category added (on) or removed (off).</summary>
    // [impl->req~ank.outlook.sideline~1]
    public static string SetSideline(string? categories, bool on, char separator = ',') =>
        on ? Add(categories, Sideline, separator) : Remove(categories, Sideline);

    /// <summary>
    /// The session's categories as the COM script lists them,
    /// <c>[{"name": "...", "color": n}]</c>, by name (case-insensitive; the first wins).
    /// </summary>
    public static IReadOnlyDictionary<string, int> ParseColors(System.Text.Json.Nodes.JsonNode? raw)
    {
        var nodes = raw switch
        {
            System.Text.Json.Nodes.JsonArray a => a.OfType<System.Text.Json.Nodes.JsonObject>(),
            System.Text.Json.Nodes.JsonObject o => [o],
            _ => [],
        };
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            if ((string?)n["name"] is not { Length: > 0 } name) continue;
            if (n["color"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out int color)) result.TryAdd(name.Trim(), color);
        }
        return result;
    }

    private static char SeparatorOf(string? categories, char fallback)
    {
        var i = categories?.IndexOfAny(Separators) ?? -1;
        return i < 0 ? fallback : categories![i];
    }

    /// <summary>
    /// The first category of the list that is not one of AnchorHub's marks
    /// (<c>koppter</c>, <c>Sideline</c>), or null.
    /// </summary>
    public static string? FirstUserCategory(string? categories) =>
        Split(categories).FirstOrDefault(c =>
            !string.Equals(c, KoppterBlocks.Category, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(c, Sideline, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The display color of an item as Outlook shows it in the calendar: the color of
    /// its first user category, looked up in the session's category list
    /// (<paramref name="colors"/>: name → <c>OlCategoryColor</c>). Null when there is no
    /// such category, it is not in the list, or its color is none.
    /// </summary>
    // [impl->req~ank.outlook.busy~2]
    public static string? DisplayColor(string? categories, IReadOnlyDictionary<string, int> colors) =>
        FirstUserCategory(categories) is { } c && colors.TryGetValue(c, out var color) ? ColorHex(color) : null;

    // The swatches of the category color picker of classic desktop Outlook
    // (2007 to 2016 and Microsoft 365), as reproduced in add-in and EWS
    // references. Microsoft documents only the indexes of OlCategoryColor
    // (learn.microsoft.com/office/vba/api/outlook.olcategorycolor), not the RGB values.
    private static readonly string[] Preset =
    [
        "#E7A1A2", // 1 Red
        "#F9BA89", // 2 Orange
        "#F7DD8F", // 3 Peach
        "#FCFA90", // 4 Yellow
        "#78D168", // 5 Green
        "#9FDCC9", // 6 Teal
        "#C6D2B0", // 7 Olive
        "#9DB7E8", // 8 Blue
        "#B5A1E2", // 9 Purple
        "#DAAEC2", // 10 Maroon
        "#DAD9DC", // 11 Steel
        "#6B7994", // 12 DarkSteel
        "#BFBFBF", // 13 Gray
        "#6F6F6F", // 14 DarkGray
        "#4F4F4F", // 15 Black
        "#C11A25", // 16 DarkRed
        "#E2620D", // 17 DarkOrange
        "#C79930", // 18 DarkPeach
        "#B9B300", // 19 DarkYellow
        "#368F2B", // 20 DarkGreen
        "#329B7A", // 21 DarkTeal
        "#778B45", // 22 DarkOlive
        "#2858A5", // 23 DarkBlue
        "#5C3FA3", // 24 DarkPurple
        "#93446B", // 25 DarkMaroon
    ];

    /// <summary><c>OlCategoryColor</c> 1..25 as <c>#RRGGBB</c>; null for 0 (none) and unknown values.</summary>
    // [impl->req~ank.outlook.busy~2]
    public static string? ColorHex(int olCategoryColor) =>
        olCategoryColor is >= 1 and <= 25 ? Preset[olCategoryColor - 1] : null;
}
