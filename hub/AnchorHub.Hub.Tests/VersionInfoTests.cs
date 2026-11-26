using System.Text.Json;
using AnchorHub.Hub.Cli;

namespace AnchorHub.Hub.Tests;

// [utest->req~ank.cli.version~1]
public class VersionInfoTests
{
    [Fact]
    public void Json_carries_the_version_and_the_commands_with_version_first()
    {
        using var doc = JsonDocument.Parse(VersionInfo.Json("2026-11-24+abc", ["outlook-busy", "outlook-sideline"]));
        var root = doc.RootElement;
        Assert.Equal("2026-11-24+abc", root.GetProperty("version").GetString());
        Assert.Equal(
            ["version", "outlook-busy", "outlook-sideline"],
            root.GetProperty("commands").EnumerateArray().Select(c => c.GetString()!).ToArray());
    }

    [Fact]
    public void Windows_commands_include_those_koppter_calls()
    {
        foreach (var command in new[] { "outlook-todos", "outlook-open", "outlook-complete", "outlook-blocks-sync", "outlook-blocks-clear", "outlook-busy", "outlook-sideline" })
            Assert.Contains(command, VersionInfo.WindowsCommands);
    }

    [Fact]
    public void Version_is_not_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(VersionInfo.Version));
    }
}
