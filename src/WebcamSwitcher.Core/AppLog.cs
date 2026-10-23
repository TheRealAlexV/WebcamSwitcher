namespace WebcamSwitcher.Core;

/// <summary>Minimal timestamped file logger for diagnosing pipeline/apply hangs.</summary>
public static class AppLog
{
    private static readonly object Lock = new();

    /// <summary>
    /// Test hook: redirects the log to a scratch file so a harness never writes
    /// into the running app's log. Null in production.
    /// </summary>
    internal static string? PathOverride { get; set; }

    public static void Write(string message)
    {
        try
        {
            var path = PathOverride ?? Path.Combine(Path.GetTempPath(), "WebcamSwitcher.apply.log");
            lock (Lock)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
