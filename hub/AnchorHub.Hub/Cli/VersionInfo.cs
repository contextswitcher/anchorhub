using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnchorHub.Hub.Cli;

/// <summary>
/// What <c>ankd version</c> prints. A caller such as koppter checks the list of commands rather than
/// the version, so that a local build without a release version is judged by what it can do.
/// </summary>
// [impl->req~ank.cli.version~1]
internal static class VersionInfo
{
    /// <summary>The CLI commands of the Windows build; the other builds have none of them.</summary>
    public static readonly string[] WindowsCommands =
    [
        "lookup", "lookup-name", "search-wsi",
        "outlook-capture", "outlook-open", "outlook-todos", "outlook-complete",
        "outlook-blocks-sync", "outlook-blocks-clear", "outlook-busy", "outlook-sideline",
        "onenote-backlinks-sync",
    ];

    /// <summary>The informational version: the CalVer of a release, <c>1.0.0</c> for a local build, each with <c>+commit</c> when the SDK knows it.</summary>
    public static string Version =>
        typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string Json(string version, IEnumerable<string> commands) =>
        new JsonObject
        {
            ["version"] = version,
            ["commands"] = new JsonArray(commands.Prepend("version").Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        }.ToJsonString(Readable);

    // The default encoder escapes the plus sign before the commit, which makes the line hard to read.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
