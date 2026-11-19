using Microsoft.Win32;

namespace UniversalSearchLauncher.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Universal Search Launcher";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось прочитать автозапуск.", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (!IsEnabled())
                {
                    return true;
                }

                using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (existing is null)
                {
                    return true;
                }

                existing.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Info("Автозапуск отключён.");
                return true;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
            AppLog.Info("Автозапуск включён.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось изменить автозапуск.", ex);
            return false;
        }
    }
}
