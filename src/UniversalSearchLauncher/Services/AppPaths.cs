namespace UniversalSearchLauncher.Services;

public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DatabaseFile => Path.Combine(Root, "data", "index.db");
    public static string ApplicationCacheFile => Path.Combine(Root, "data", "apps.json");
    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string LogFile => Path.Combine(LogsDirectory, "app.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveRoot()
    {
        // Development, portable and automated-test override.
        var overridePath = Environment.GetEnvironmentVariable("USL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            try
            {
                return Path.GetFullPath(overridePath);
            }
            catch
            {
                // Fall back to the normal per-user location.
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniversalSearchLauncher");
    }
}
