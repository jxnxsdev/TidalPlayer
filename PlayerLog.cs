using System.Text;

namespace TidalMpvPlayer;

internal static class PlayerLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TidalMpvPlayer",
        "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "player.log");

    public static string FilePath => LogPath;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                using var writer = new StreamWriter(LogPath, append: true, new UTF8Encoding(false));
                writer.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
                writer.Write(" [");
                writer.Write(level);
                writer.Write("] ");
                writer.WriteLine(message);
                if (exception is not null)
                    writer.WriteLine(exception);
            }
        }
        catch
        {
            // Logging must never prevent playback or protocol handling.
        }
    }
}
