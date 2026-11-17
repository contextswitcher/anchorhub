namespace AnchorHub.Hub.ContextSwitcher;

/// Resolves where ContextSwitcher keeps its task files, following the same
/// rules as the ContextSwitcher app (config.AppSettings): the `tasksDir` from
/// `~/.contextswitcher/settings.yaml`, falling back to `~/.contextswitcher/
/// tasks` when the file is absent or unreadable. `ANCHORHUB_CS_TASKS_DIR`
/// overrides both (tests, and users with a non-standard layout).
internal static class ContextSwitcherPaths
{
    private const string EnvOverride = "ANCHORHUB_CS_TASKS_DIR";

    public static string ResolveTasksDirectory()
    {
        var env = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".contextswitcher");

        return TryReadTasksDir(Path.Combine(configDir, "settings.yaml"))
            ?? Path.Combine(configDir, "tasks");
    }

    // settings.yaml is a flat map (tasksDir / wsPort / wsToken). A one-key
    // line scan is enough and keeps YAML parsing out of path resolution.
    private static string? TryReadTasksDir(string settingsFile)
    {
        try
        {
            if (!File.Exists(settingsFile)) return null;
            foreach (var raw in File.ReadAllLines(settingsFile))
            {
                var line = raw.Trim();
                if (!line.StartsWith("tasksDir:", StringComparison.Ordinal)) continue;
                var value = line["tasksDir:".Length..].Trim().Trim('"', '\'');
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch
        {
            // Unreadable / malformed settings.yaml → fall back to the default.
        }
        return null;
    }
}
