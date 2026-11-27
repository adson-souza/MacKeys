using System.Collections.Concurrent;

namespace MacKeys;

/// <summary>
/// Small rolling log at %APPDATA%\MacKeys\debug.log. Write() only queues the line: the file is written on a
/// background thread, so logging from the keyboard hook never costs disk time.
/// </summary>
internal static class DebugLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 1000);

    static DebugLog()
    {
        new Thread(Writer) { IsBackground = true, Name = "MacKeys log", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public static string FilePath => Path.Combine(Settings.Folder, "debug.log");

    public static void Write(string message) =>
        Queue.TryAdd($"{DateTime.Now:HH:mm:ss.fff} {message}"); // drops lines rather than ever blocking

    private static void Writer()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) file.Delete();
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never break the app.
            }
        }
    }
}
