using System.Text.Json;

namespace AnchorHub.Hub.Http;

/// <summary>
/// Writes (and removes) AnchorHub's discovery JSON under JabRef's config
/// directory so JabRef's BrowserExtensionFulltextFetcher can find this
/// provider. Schema: see <c>docs/requirements/browser-extension-fulltext.md</c>.
///
/// [impl-&gt;req~bxf.discovery-dir~1]
/// [impl-&gt;req~bxf.discovery-schema~1]
/// </summary>
internal static class JabRefProviderRegistration
{
    public const string ProviderName  = "anchorhub";
    private const string DisplayName  = "AnchorHub";
    private const int    ProtocolVer  = 1;

    /// <summary>
    /// Absolute path of the discovery file
    /// (<c>anchorhub.json</c> under JabRef's fulltext-providers dir).
    /// </summary>
    public static string DiscoveryFilePath
    {
        get
        {
            string baseDir;
            if (OperatingSystem.IsWindows())
            {
                // %APPDATA%\JabRef\fulltext-providers\
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "JabRef", "fulltext-providers");
            }
            else if (OperatingSystem.IsMacOS())
            {
                // ~/Library/Application Support/JabRef/fulltext-providers/
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                baseDir = Path.Combine(home, "Library", "Application Support",
                                       "JabRef", "fulltext-providers");
            }
            else
            {
                // Linux + others: $XDG_CONFIG_HOME/jabref/fulltext-providers/
                // (default ~/.config/jabref/fulltext-providers/).
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(xdg))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    xdg = Path.Combine(home, ".config");
                }
                baseDir = Path.Combine(xdg, "jabref", "fulltext-providers");
            }

            return Path.Combine(baseDir, $"{ProviderName}.json");
        }
    }

    /// <summary>
    /// Writes the discovery file. Creates the parent directory if needed.
    /// Overwrites any prior copy (port may change between runs).
    /// </summary>
    public static void Write(int port, string tokenFilePath)
    {
        var path = DiscoveryFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new
        {
            name            = ProviderName,
            displayName     = DisplayName,
            port,
            tokenFile       = tokenFilePath,
            protocolVersion = ProtocolVer,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Best-effort delete of the discovery file. Used on graceful shutdown.
    /// </summary>
    public static void Remove()
    {
        try
        {
            var path = DiscoveryFilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
