namespace Weir.Core.Configuration;

/// <summary>
/// Where Weir keeps its files, resolved in this order so existing installs keep finding their data:
/// <list type="bullet">
/// <item><c>WEIR_HOME</c>, else <c>%PROGRAMDATA%\Weir</c> on Windows, else <c>$XDG_DATA_HOME/weir</c>, else <c>~/.local/share/weir</c>;</item>
/// <item><c>{home}/data/weir.sqlite3</c> unless <c>WEIR_DB_PATH</c>;</item>
/// <item><c>{home}/backups</c>, <c>{home}/logs</c>, <c>{home}/temp</c> unless <c>WEIR_BACKUP_DIR</c>, <c>WEIR_LOG_DIR</c>, <c>WEIR_TEMP_DIR</c>.</item>
/// </list>
/// Relative overrides are taken relative to the home directory. Every path is absolute.
/// </summary>
public sealed record RuntimePaths(string Home, string DbPath, string BackupDir, string LogDir, string TempDir)
{
    /// <summary>The single JSON-lines log file the Logs screen reads (<c>{log_dir}/weir.log</c>).</summary>
    public const string LogFileName = "weir.log";

    public static RuntimePaths Resolve(RuntimeEnvironment runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var home = ValueParsing.ResolveVariable("WEIR_HOME", ResolveHome(runtime), runtime);
        return new RuntimePaths(
            home,
            ResolveUnderHome(runtime, home, "WEIR_DB_PATH", "data", "weir.sqlite3"),
            ResolveUnderHome(runtime, home, "WEIR_BACKUP_DIR", "backups"),
            ResolveUnderHome(runtime, home, "WEIR_LOG_DIR", "logs"),
            ResolveUnderHome(runtime, home, "WEIR_TEMP_DIR", "temp"));
    }

    /// <summary>The OS default home when <c>WEIR_HOME</c> is unset.</summary>
    public static string DefaultHome(RuntimeEnvironment runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.IsWindows)
        {
            var programData = (string.IsNullOrEmpty(runtime.Get("PROGRAMDATA")) ? @"C:\ProgramData" : runtime.Get("PROGRAMDATA")!).Trim();
            return Path.Join(programData, "Weir");
        }

        var xdg = (runtime.Get("XDG_DATA_HOME") ?? string.Empty).Trim();
        return xdg.Length > 0
            ? Path.Join(xdg, "weir")
            : Path.Join(runtime.UserHomeDirectory, ".local", "share", "weir");
    }

    private static string ResolveHome(RuntimeEnvironment runtime)
    {
        var overrideValue = (runtime.Get("WEIR_HOME") ?? string.Empty).Trim();
        return overrideValue.Length > 0 ? ValueParsing.ExpandUser(overrideValue, runtime) : DefaultHome(runtime);
    }

    private static string ResolveUnderHome(RuntimeEnvironment runtime, string home, string variable, params string[] defaultSegments)
    {
        var overrideValue = (runtime.Get(variable) ?? string.Empty).Trim();
        if (overrideValue.Length == 0)
        {
            return ValueParsing.ResolveVariable(variable, Path.Join([home, .. defaultSegments]), runtime);
        }

        var expanded = ValueParsing.ExpandUser(overrideValue, runtime);
        return ValueParsing.ResolveVariable(variable, Path.IsPathFullyQualified(expanded) ? expanded : Path.Join(home, expanded), runtime);
    }
}
