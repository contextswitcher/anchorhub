namespace AnchorHub.Core;

public sealed record NormalizedUrl(string Normalized, string Host);

public static class UrlNormalizer
{
    public static NormalizedUrl? TryNormalize(string raw)
    {
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var host = uri.Host.ToLowerInvariant();

        // Omit port when it is the scheme default.
        var isDefaultPort = (uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80)
                         || (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443);
        var portPart = (!uri.IsDefaultPort && !isDefaultPort)
            ? $":{uri.Port}"
            : string.Empty;

        // Preserve "/" for root path; strip trailing slash otherwise.
        var path = uri.AbsolutePath;
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');
        if (path.Length == 0)
            path = "/";

        var normalized = $"{uri.Scheme}://{host}{portPart}{path}";
        return new NormalizedUrl(normalized, host);
    }
}
