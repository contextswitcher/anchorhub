using System.Text.Json;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Tests;

// Exercises the contextSwitcherLookup action through MessageHandler with a
// null OneNote backend, proving it is independent of the COM bridge. Uses the
// ANCHORHUB_CS_TASKS_DIR override; no other test touches that env var.
public sealed class ContextSwitcherLookupActionTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previousEnv;

    public ContextSwitcherLookupActionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anchorhub-cs-action-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previousEnv = Environment.GetEnvironmentVariable("ANCHORHUB_CS_TASKS_DIR");
        Environment.SetEnvironmentVariable("ANCHORHUB_CS_TASKS_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ANCHORHUB_CS_TASKS_DIR", _previousEnv);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static JsonElement Msg(string url) =>
        JsonSerializer.SerializeToElement(new { action = "contextSwitcherLookup", requestId = "r1", url });

    private static async Task<JsonElement> Handle(JsonElement msg)
    {
        // Null backend → OneNote actions would DISCONNECT; this action must not.
        using var handler = new MessageHandler(backend: null);
        var reply = await handler.HandleAsync(msg, CancellationToken.None);
        return JsonSerializer.SerializeToElement(reply);
    }

    [Fact]
    public async Task Reports_found_task_with_a_null_onenote_backend()
    {
        File.WriteAllText(Path.Combine(_dir, "fix-npe.md"),
            "---\ntitle: Fix NPE\nstatus: active\nbrowser:\n  urls:\n    - https://github.com/JabRef/jabref/pull/12345\n---\n# Notes\n");

        var reply = await Handle(Msg("https://github.com/JabRef/jabref/pull/12345"));

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.True(reply.GetProperty("found").GetBoolean());
        var task = reply.GetProperty("task");
        Assert.Equal("fix-npe", task.GetProperty("taskId").GetString());
        Assert.Equal("Fix NPE", task.GetProperty("title").GetString());
        Assert.Equal("active", task.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Reports_not_found_for_an_unknown_url()
    {
        var reply = await Handle(Msg("https://example.org/nothing"));

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.False(reply.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Missing_url_is_a_bad_request()
    {
        var msg = JsonSerializer.SerializeToElement(new { action = "contextSwitcherLookup", requestId = "r1" });

        var reply = await Handle(msg);

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("BAD_REQUEST", reply.GetProperty("error").GetProperty("code").GetString());
    }
}
