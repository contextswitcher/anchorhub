using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace AnchorHub.Core;

/// <summary>
/// Reads the full-text index the OneMore add-in keeps in <c>OneMore.db</c>
/// (an FTS5 trigram index over paragraph text, see OneMore's TextIndexDB.sql
/// and PageIdentityDB.sql). Read-only; OneMore owns the file. Used only
/// while indexing is enabled in OneMore: <c>&lt;IndexSheet&gt;&lt;enabled&gt;</c>
/// in its Settings.xml, else <paramref name="installedDefault"/> (OneMore's
/// installer choice), as OneMore's own IndexConsumer decides.
/// </summary>
public sealed class OneMoreIndex(string path, string settingsPath, Func<bool> installedDefault)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneMore", "OneMore.db");

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OneMore", "Settings.xml");

    /// <summary>Whether OneMore's search indexing is turned on (re-read each call,
    /// so toggling it in OneMore takes effect without restarting the daemon).</summary>
    public bool IsEnabled()
    {
        try
        {
            var value = XElement.Load(settingsPath).Element("IndexSheet")?.Element("enabled")?.Value;
            if (bool.TryParse(value, out var enabled)) return enabled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // no settings file yet, or mid-write: fall through to the installer default
        }
        return installedDefault();
    }

    private const string Sql = """
        SELECT DISTINCT p.pageID
        FROM index_fts f
        JOIN index_text t    ON t.textID  = f.rowid
        JOIN identity_page p ON p.pageKey = t.pageKey
        WHERE index_fts MATCH @match AND p.missingSince IS NULL
        """;

    /// <summary>
    /// Returns the OneNote page IDs whose text contains <paramref name="query"/>
    /// (case-insensitive substring). Empty when the index is absent, unreadable,
    /// disabled in OneMore, or cannot answer (trigram index needs at least 3
    /// characters) — callers
    /// then fall back to OneNote's own search.
    /// </summary>
    public IReadOnlyList<string> FindPageIds(string query)
    {
        if (query.Length < 3 || !File.Exists(path) || !IsEnabled()) return [];
        try
        {
            using var con = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode       = SqliteOpenMode.ReadOnly,
            }.ToString());
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = Sql;
            cmd.Parameters.AddWithValue("@match", "\"" + query.Replace("\"", "\"\"") + "\"");
            var ids = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }
        catch (SqliteException)
        {
            return []; // no index tables (feature never enabled), locked, or schema changed
        }
    }
}
