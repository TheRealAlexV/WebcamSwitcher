namespace WebcamSwitcher.Core;

/// <summary>Minimal timestamped file logger for diagnosing pipeline/apply hangs.</summary>
public static class AppLog
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "WebcamSwitcher.apply.log");
            lock (Lock)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
