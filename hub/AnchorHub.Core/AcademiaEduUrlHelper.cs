using System.Text.RegularExpressions;

namespace AnchorHub.Core;

/// <summary>
/// Extracts the researcher's name from Academia.edu profile URLs of the form
/// <c>&lt;institution&gt;.academia.edu/&lt;CamelCaseName&gt;</c>. The path is a
/// single CamelCase token; spaces are inserted at lowercase→uppercase
/// boundaries (Unicode-aware), so e.g. <c>FranciscoBenitez</c> →
/// <c>"Francisco Benitez"</c>, <c>JoséGarcía</c> → <c>"José García"</c>.
/// </summary>
public static class AcademiaEduUrlHelper
{
    // Split before any uppercase letter that follows a lowercase letter (or
    // digit), Unicode-aware so accented letters work too.
    private static readonly Regex CamelCaseSplit =
        new(@"(?<=[\p{Ll}0-9])(?=\p{Lu})", RegexOptions.Compiled);

    public static string? ExtractPersonName(NormalizedUrl url)
    {
        if (!url.Host.EndsWith(".academia.edu", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(url.Host, "academia.edu", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Profile path is a single segment under the institution subdomain.
        // Reserved paths (e.g. /Documents, /search) are skipped — they begin
        // with a slug we don't want to parse as a name. Heuristic: the slug
        // must contain at least one lowercase letter (a real CamelCase token).
        if (segments.Length != 1) return null;
        var slug = segments[0].Trim();
        if (slug.Length == 0) return null;
        if (!slug.Any(char.IsLower)) return null;

        var name = CamelCaseSplit.Replace(slug, " ").Trim();
        return name.Length == 0 ? null : name;
    }
}
