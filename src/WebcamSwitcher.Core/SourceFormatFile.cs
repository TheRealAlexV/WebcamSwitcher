using System.IO;

namespace WebcamSwitcher.Core;

/// <summary>
/// Writes the output format the native virtual-camera source reads at startup
/// (see WebcamSwitcher.Source/SourceFormat.cpp). Stored machine-wide under
/// %ProgramData% so the Frame Server (LOCAL SERVICE, session 0) can read it.
/// </summary>
public static class SourceFormatFile
{
    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WebcamSwitcher", "format.ini");

    public static void Write(int width, int height, int fps)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, $"width={width}\nheight={height}\nfps={fps}\n");
        }
        catch
        {
            // Best effort; the source falls back to defaults if the file is absent.
        }
    }
}
