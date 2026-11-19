using System.Text.Json;
using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private Settings _current;

    public SettingsService()
    {
        _current = Load();
    }

    public Settings Current
    {
        get
        {
            lock (_sync)
            {
                return _current.Clone();
            }
        }
    }

    public string FilePath => AppPaths.SettingsFile;

    public bool Save(Settings settings)
    {
        settings.Normalize();
        lock (_sync)
        {
            try
            {
                AppPaths.EnsureCreated();
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                var temporary = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, AppPaths.SettingsFile, true);
                _current = settings.Clone();
                AppLog.Info("Настройки сохранены.");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить настройки.", ex);
                return false;
            }
        }
    }

    private static Settings Load()
    {
        try
        {
            AppPaths.EnsureCreated();
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return Settings.CreateDefault();
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
            if (settings is null)
            {
                return Settings.CreateDefault();
            }

            if (settings.IndexedFolders.Count == 0)
            {
                settings.IndexedFolders = Settings.GetDefaultFolders();
            }

            if (settings.Exclusions.Count == 0)
            {
                settings.Exclusions = Settings.GetDefaultExclusions();
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось загрузить настройки; используются значения по умолчанию.", ex);
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", true);
                }
            }
            catch
            {
                // Best effort.
            }

            return Settings.CreateDefault();
        }
    }
}
