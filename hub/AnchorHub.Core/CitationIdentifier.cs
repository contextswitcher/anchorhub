namespace AnchorHub.Core;

/// <summary>
/// A typed identifier of the form <c>scheme:value</c>, e.g. <c>doi:10.1000/foo</c>.
/// Scheme is one of doi, arxiv, isbn, url and is normalised to lowercase.
/// </summary>
public sealed record CitationIdentifier(string Scheme, string Value)
{
    public static readonly IReadOnlySet<string> AllowedSchemes =
        new HashSet<string>(StringComparer.Ordinal) { "doi", "arxiv", "isbn", "url" };

    public static bool TryParse(string raw, out CitationIdentifier? identifier, out string? error)
    {
        identifier = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "identifier value is empty";
            return false;
        }

        var trimmed = raw.Trim();
        // Split on first colon so url:https://... keeps its scheme separator intact.
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            error = $"identifier '{trimmed}' is not in '<scheme>:<value>' form";
            return false;
        }

        var scheme = trimmed[..colon].Trim().ToLowerInvariant();
        var value  = trimmed[(colon + 1)..].Trim();

        if (!AllowedSchemes.Contains(scheme))
        {
            error = $"identifier scheme '{scheme}' is not one of: "
                  + string.Join(", ", AllowedSchemes.OrderBy(s => s));
            return false;
        }

        if (value.Length == 0)
        {
            error = $"identifier '{trimmed}' has empty value";
            return false;
        }

        identifier = new CitationIdentifier(scheme, value);
        return true;
    }

    public override string ToString() => $"{Scheme}:{Value}";
}
