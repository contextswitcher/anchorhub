using System.Text.Json;
using AnchorHub.Hub.Http;

namespace AnchorHub.Hub.Tests;

public class JabRefProviderRegistrationTests
{
    [Fact]
    public void Write_then_remove_round_trips_through_discovery_path()
    {
        // Don't make assumptions about the actual filesystem location of
        // JabRef's discovery dir on the test machine — instead, verify
        // that Write/Remove operate on a single file path round-trip.
        var path = JabRefProviderRegistration.DiscoveryFilePath;

        // Ensure clean slate (don't clobber an existing real install).
        var preExisting = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            JabRefProviderRegistration.Write(17893, "C:\\fake\\token.txt");
            Assert.True(File.Exists(path));

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("anchorhub", root.GetProperty("name").GetString());
            Assert.Equal("AnchorHub", root.GetProperty("displayName").GetString());
            Assert.Equal(17893,       root.GetProperty("port").GetInt32());
            Assert.Equal(1,           root.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("C:\\fake\\token.txt",
                         root.GetProperty("tokenFile").GetString());

            JabRefProviderRegistration.Remove();
            Assert.False(File.Exists(path));
        }
        finally
        {
            // Restore any pre-existing content the test displaced.
            if (preExisting is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, preExisting);
            }
        }
    }

    [Fact]
    public void Discovery_path_contains_expected_segments()
    {
        var path = JabRefProviderRegistration.DiscoveryFilePath;
        Assert.EndsWith("anchorhub.json", path);
        Assert.Contains("fulltext-providers", path);
        // JabRef dir name capitalisation differs by OS but the substring
        // (case-insensitive) is present in all platforms.
        Assert.Contains("jabref", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_is_idempotent_when_file_absent()
    {
        // Should not throw even when there's nothing to delete.
        JabRefProviderRegistration.Remove();
        JabRefProviderRegistration.Remove();
    }
}
