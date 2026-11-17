using AnchorHub.Core;
using Microsoft.Data.Sqlite;

namespace AnchorHub.Core.Tests;

public class OneMoreIndexTests
{
    [Fact]
    public void FindsPagesByCaseInsensitiveSubstringAndSkipsMissingPages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onemore-{Guid.NewGuid():N}.db");
        using (var con = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            // Subset of OneMore's TextIndexDB.sql / PageIdentityDB.sql.
            cmd.CommandText = """
                CREATE TABLE identity_page (pageKey INTEGER PRIMARY KEY, pageID TEXT NOT NULL, missingSince TEXT);
                CREATE TABLE index_text (textID INTEGER PRIMARY KEY AUTOINCREMENT, pageKey INTEGER NOT NULL, text TEXT NOT NULL);
                CREATE VIRTUAL TABLE index_fts USING fts5(text, content='index_text', content_rowid='textID', tokenize='trigram');
                CREATE TRIGGER index_text_ai AFTER INSERT ON index_text BEGIN INSERT INTO index_fts(rowid, text) VALUES (new.textID, new.text); END;
                INSERT INTO identity_page VALUES (1, '{A}', NULL), (2, '{B}', NULL), (3, '{GONE}', '2026-01-01');
                INSERT INTO index_text (pageKey, text) VALUES
                  (1, 'see https://github.com/Foo/bar for details'),
                  (1, 'again https://github.com/foo/bar'),
                  (2, 'nothing here'),
                  (3, 'https://github.com/foo/bar');
                """;
            cmd.ExecuteNonQuery();
        }

        var settings = path + ".xml";
        File.WriteAllText(settings, "<settings><IndexSheet><enabled>True</enabled></IndexSheet></settings>");
        var index = new OneMoreIndex(path, settings, () => false);
        Assert.Equal(["{A}"], index.FindPageIds("https://github.com/foo/bar"));
        Assert.Empty(index.FindPageIds("ab"));
        Assert.Empty(index.FindPageIds("say \"hi\""));
        Assert.Empty(new OneMoreIndex(path + ".absent", settings, () => true).FindPageIds("github"));

        File.WriteAllText(settings, "<settings><IndexSheet><enabled>false</enabled></IndexSheet></settings>");
        Assert.Empty(new OneMoreIndex(path, settings, () => true).FindPageIds("github"));
    }

    [Fact]
    public void EnabledFallsBackToInstallerDefaultWithoutExplicitSetting()
    {
        var settings = Path.Combine(Path.GetTempPath(), $"onemore-{Guid.NewGuid():N}.xml");
        Assert.True(new OneMoreIndex("x.db", settings, () => true).IsEnabled());   // no file
        File.WriteAllText(settings, "<settings><GeneralSheet/></settings>");
        Assert.False(new OneMoreIndex("x.db", settings, () => false).IsEnabled());
        Assert.True(new OneMoreIndex("x.db", settings, () => true).IsEnabled());
    }
}
