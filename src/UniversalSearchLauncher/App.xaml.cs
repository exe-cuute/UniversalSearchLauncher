using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UniversalSearchLauncher.Core.Models;
using UniversalSearchLauncher.Services;
using UniversalSearchLauncher.ViewModels;
using UniversalSearchLauncher.Views;
using Application = System.Windows.Application;

namespace UniversalSearchLauncher;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private SettingsService? _settingsService;
    private ThemeService? _themeService;
    private StorageService? _storage;
    private UsageService? _usage;
    private AppIndexService? _appIndex;
    private FileIndexService? _fileIndex;
    private SearchService? _search;
    private IconService? _icons;
    private HotkeyService? _hotkey;
    private TrayService? _tray;
    private LauncherWindow? _launcher;
    private LauncherViewModel? _launcherViewModel;
    private SettingsWindow? _settingsWindow;
    private bool _hotkeyInitialized;
    private bool _exiting;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Необработанное исключение процесса.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Необработанное исключение задачи.", args.Exception);
            args.SetObserved();
        };

        AppPaths.EnsureCreated();
        AppLog.Info("Запуск Universal Search Launcher " + UpdateService.CurrentVersionDisplay);

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.NotifyExistingInstance();
            Shutdown();
            return;
        }

        _settingsService = new SettingsService();
        _themeService = new ThemeService();
        _themeService.Apply(_settingsService.Current.Theme);

        _storage = new StorageService();
        _usage = new UsageService(_storage);
        _appIndex = new AppIndexService();
        _fileIndex = new FileIndexService(_storage);
        _search = new SearchService(_appIndex, _fileIndex, _usage);
        _icons = new IconService();
        _hotkey = new HotkeyService();

        _hotkey.Pressed += (_, _) =>
        {
            if (_settingsWindow is not null || _exiting)
            {
                return;
            }

            _launcher?.ToggleLauncher();
        };

        _launcherViewModel = new LauncherViewModel(
            _settingsService,
            _storage,
            _search,
            _usage,
            _appIndex,
            _fileIndex,
            _icons);

        _launcherViewModel.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(OpenSettings);

        _launcher = new LauncherWindow(_launcherViewModel, _settingsService);
        MainWindow = _launcher;
        _launcher.WindowHandleCreated += (_, _) => InitializeHotkey();

        // Create the HWND without showing the window so the global hotkey and
        // single-instance message handler are available immediately.
        new WindowInteropHelper(_launcher).EnsureHandle();

        CreateTray();
        StartBackgroundIndexing();

        var settings = _settingsService.Current;
        var isFirstRun = !settings.FirstRunCompleted;
        if (isFirstRun)
        {
            settings.FirstRunCompleted = true;
            settings.RunAtStartup = StartupService.IsEnabled();
            _settingsService.Save(settings);
        }

        _launcherViewModel.UpdateSettings(settings);
        _launcherViewModel.Start();

        var background = e.Args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase));
        var openSettings = e.Args.Any(arg => arg.Equals("--settings", StringComparison.OrdinalIgnoreCase));

        if (openSettings)
        {
            Dispatcher.BeginInvoke(OpenSettings, DispatcherPriority.ApplicationIdle);
        }
        else if (!background)
        {
            _launcher.ShowLauncher(force: false);
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (isFirstRun)
            {
                _tray?.ShowBalloon(
                    "Universal Search Launcher",
                    $"Нажмите {_settingsService.Current.Hotkey}, чтобы открыть поиск. Настройки доступны в трее.");
            }

            if (_hotkey is { IsRegistered: false, LastError: not null })
            {
                _tray?.ShowBalloon(
                    "Горячая клавиша недоступна",
                    $"{_hotkey.LastError} Откройте настройки в трее и выберите другую комбинацию.",
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void InitializeHotkey()
    {
        if (_hotkeyInitialized || _hotkey is null || _launcher is null)
        {
            return;
        }

        _hotkeyInitialized = true;
        var handle = new WindowInteropHelper(_launcher).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            AppLog.Error("Не удалось получить HwndSource для горячей клавиши.");
            return;
        }

        var settings = _settingsService!.Current;
        if (!HotkeyDefinition.TryParse(settings.Hotkey, out var definition, out var error) || definition is null)
        {
            AppLog.Warn("Некорректная горячая клавиша в настройках: " + error);
            definition = new HotkeyDefinition(System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.Space);
        }

        _hotkey.Initialize(source, definition);
        _tray?.SetHotkeyText(_hotkey.Current?.Display ?? definition.Display);
    }

    private void CreateTray()
    {
        try
        {
            _tray = new TrayService(
                () => Dispatcher.BeginInvoke(new Action(() => _launcher?.ShowLauncher(force: true))),
                () => Dispatcher.BeginInvoke(new Action(OpenSettings)),
                () =>
                {
                    Dispatcher.BeginInvoke(new Action(() => _ = CheckForUpdatesAsync()));
                    return Task.CompletedTask;
                },
                () => Dispatcher.BeginInvoke(new Action(ExitApplication)));
            _tray.SetHotkeyText(_hotkey?.Current?.Display ?? _settingsService?.Current.Hotkey ?? "Alt+Space");
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось создать значок в системном трее.", ex);
        }
    }

    private void StartBackgroundIndexing()
    {
        if (_settingsService is null || _appIndex is null || _fileIndex is null)
        {
            return;
        }

        var settings = _settingsService.Current;
        _ = Task.Run(async () =>
        {
            try
            {
                await _appIndex.InitializeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Инициализация индекса приложений завершилась ошибкой.", ex);
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _fileIndex.InitializeAsync(settings).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Инициализация индекса файлов завершилась ошибкой.", ex);
            }
        });
    }

    private void OpenSettings()
    {
        if (_exiting || _settingsService is null || _hotkey is null || _storage is null || _fileIndex is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        _launcher?.HideLauncher(animated: false);
        var window = new SettingsWindow(
            _settingsService.Current,
            _settingsService,
            _hotkey,
            _storage,
            _fileIndex);
        _settingsWindow = window;
        try
        {
            var result = window.ShowDialog();
            if (result == true && window.AppliedSettings is not null)
            {
                ApplySettings(window.AppliedSettings, window.RebuildIndexRequested);
            }

            if (window.HistoryCleared)
            {
                _launcherViewModel?.ReloadHistory();
            }
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    private void ApplySettings(Settings settings, bool rebuildIndex)
    {
        _themeService?.Apply(settings.Theme);
        if (!StartupService.SetEnabled(settings.RunAtStartup))
        {
            MessageDialog.Show(
                null,
                "Автозапуск",
                "Не удалось изменить параметр автозапуска в реестре Windows. " +
                "Проверьте права текущего пользователя и повторите попытку в настройках.");
        }

        _launcherViewModel?.UpdateSettings(settings);
        _tray?.SetHotkeyText(_hotkey?.Current?.Display ?? settings.Hotkey);

        if (_fileIndex is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _fileIndex.ApplySettingsAsync(settings, rebuildIndex).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Не удалось применить настройки индекса.", ex);
                }
            });
        }

        AppLog.Info("Настройки применены.");
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_settingsService is null)
        {
            return;
        }

        try
        {
            var result = await UpdateService.CheckAsync(_settingsService.Current.UpdateFeedUrl);
            var primary = result.DownloadUri is not null ? "Скачать" : "Понятно";
            var secondary = result.DownloadUri is not null ? "Позже" : null;
            var download = MessageDialog.Show(
                _launcher is { IsVisible: true } ? _launcher : null,
                "Обновления",
                result.Message,
                primary,
                secondary,
                "Проверка обновлений");

            if (download && result.DownloadUri is not null)
            {
                ShellService.OpenUrl(result.DownloadUri.AbsoluteUri);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Проверка обновлений завершилась ошибкой.", ex);
            MessageDialog.Show(null, "Обновления", "Не удалось проверить обновления.\n\n" + ex.Message);
        }
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        AppLog.Info("Завершение приложения.");
        _launcher?.PrepareForShutdown();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _fileIndex?.Dispose();
        _appIndex?.Dispose();
        _launcherViewModel?.Dispose();

        try
        {
            _settingsWindow?.Close();
        }
        catch
        {
            // The settings dialog may already be closing.
        }

        if (_launcher is not null)
        {
            _launcher.Close();
        }

        _storage?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _fileIndex?.Dispose();
        _appIndex?.Dispose();
        _launcherViewModel?.Dispose();
        _storage?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Необработанное исключение интерфейса.", e.Exception);
        e.Handled = true;

        try
        {
            MessageDialog.Show(
                _launcher is { IsVisible: true } ? _launcher : null,
                "Ошибка",
                "Произошла ошибка, но приложение продолжает работу.\n\n" + e.Exception.Message);
        }
        catch
        {
            // Prevent a dialog failure from terminating the dispatcher loop.
        }
    }
}
