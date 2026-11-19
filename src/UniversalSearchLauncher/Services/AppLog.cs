using System.Text;

namespace UniversalSearchLauncher.Services;

public static class AppLog
{
    private static readonly object Sync = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            AppPaths.EnsureCreated();
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);

            if (exception is not null)
            {
                line.AppendLine().Append(exception);
            }

            line.AppendLine();

            lock (Sync)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 2 * 1024 * 1024)
                {
                    var backup = AppPaths.LogFile + ".old";
                    try
                    {
                        File.Delete(backup);
                        File.Move(AppPaths.LogFile, backup);
                    }
                    catch
                    {
                        // Logging must never take down the app.
                    }
                }

                File.AppendAllText(AppPaths.LogFile, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is best-effort only.
        }
    }
}
