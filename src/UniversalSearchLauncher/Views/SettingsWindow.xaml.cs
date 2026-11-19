using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalSearchLauncher.Core.Models;
using UniversalSearchLauncher.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UniversalSearchLauncher.Views;

public partial class SettingsWindow : Window
{
    private readonly Settings _original;
    private readonly SettingsService _settingsService;
    private readonly HotkeyService _hotkeyService;
    private readonly StorageService _storage;
    private readonly FileIndexService _fileIndex;
    private readonly ObservableCollection<string> _folders;
    private readonly ObservableCollection<string> _exclusions;
    private readonly DispatcherTimer _indexTimer;
    private HotkeyDefinition? _hotkeyDraft;
    private bool _rebuildRequested;
    private bool _checkingUpdates;

    public SettingsWindow(
        Settings settings,
        SettingsService settingsService,
        HotkeyService hotkeyService,
        StorageService storage,
        FileIndexService fileIndex)
    {
        _original = settings.Clone();
        _settingsService = settingsService;
        _hotkeyService = hotkeyService;
        _storage = storage;
        _fileIndex = fileIndex;
        _folders = new ObservableCollection<string>(_original.IndexedFolders);
        _exclusions = new ObservableCollection<string>(_original.Exclusions);

        InitializeComponent();
        FoldersList.ItemsSource = _folders;
        ExclusionsList.ItemsSource = _exclusions;

        HotkeyBox.Text = _original.Hotkey;
        _hotkeyDraft = HotkeyDefinition.TryParse(_original.Hotkey, out var parsed, out _) ? parsed : null;

        ThemeSystem.IsChecked = _original.Theme == "System";
        ThemeDark.IsChecked = _original.Theme == "Dark";
        ThemeLight.IsChecked = _original.Theme == "Light";

        EngineGoogle.IsChecked = _original.SearchEngine == "Google";
        EngineBing.IsChecked = _original.SearchEngine == "Bing";
        EngineDuckDuckGo.IsChecked = _original.SearchEngine == "DuckDuckGo";

        AutostartToggle.IsChecked = _original.RunAtStartup;
        HideOnFocusLostToggle.IsChecked = _original.HideOnFocusLost;
        FullscreenToggle.IsChecked = _original.ShowOverFullscreenApps;
        MaxResultsSlider.Value = _original.MaxResults;
        AppPrioritySlider.Value = _original.PriorityApplications;
        FilePrioritySlider.Value = _original.PriorityFiles;
        WebPrioritySlider.Value = _original.PriorityWeb;
        UpdateFeedBox.Text = _original.UpdateFeedUrl;
        VersionLabel.Text = "Версия " + UpdateService.CurrentVersionDisplay;

        UpdateValueLabels();

        MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 24);
        if (Height > MaxHeight)
        {
            Height = MaxHeight;
        }

        _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _indexTimer.Tick += (_, _) => UpdateIndexSummary();
        _indexTimer.Start();
        UpdateIndexSummary();
    }

    public Settings? AppliedSettings { get; private set; }
    public bool RebuildIndexRequested => _rebuildRequested;
    public bool HistoryCleared { get; private set; }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !HotkeyBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.SelectAll();
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_hotkeyDraft is not null)
        {
            HotkeyBox.Text = _hotkeyDraft.Display;
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_hotkeyDraft is not null)
            {
                HotkeyBox.Text = _hotkeyDraft.Display;
            }

            e.Handled = true;
            Keyboard.ClearFocus();
            return;
        }

        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24))
        {
            SetStatus("Добавьте модификатор: Ctrl, Alt, Shift или Win.", true);
            e.Handled = true;
            return;
        }

        var definition = new HotkeyDefinition(modifiers, key);
        _hotkeyDraft = definition;
        HotkeyBox.Text = definition.Display;
        HotkeyBox.SelectAll();
        SetStatus(string.Empty, false);
        e.Handled = true;
    }

    private void MaxResultsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxResultsValue is not null)
        {
            MaxResultsValue.Text = ((int)Math.Round(e.NewValue)).ToString();
        }
    }

    private void PrioritySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AppPriorityValue is null || FilePriorityValue is null || WebPriorityValue is null)
        {
            return;
        }

        AppPriorityValue.Text = ((int)Math.Round(AppPrioritySlider.Value)).ToString();
        FilePriorityValue.Text = ((int)Math.Round(FilePrioritySlider.Value)).ToString();
        WebPriorityValue.Text = ((int)Math.Round(WebPrioritySlider.Value)).ToString();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку, которую нужно индексировать",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = _folders.LastOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        var owner = new WindowInteropHelper(this).Handle;
        if (owner == IntPtr.Zero || dialog.ShowDialog(new Win32Window(owner)) != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var path = Path.GetFullPath(dialog.SelectedPath).TrimEnd('\\');
        if (_folders.Any(folder => string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Эта папка уже есть в списке.", true);
            return;
        }

        _folders.Add(path);
        SetStatus("Папка добавлена. Не забудьте сохранить настройки.", false);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is string folder)
        {
            _folders.Remove(folder);
            SetStatus("Папка удалена из индекса после сохранения.", false);
        }
        else
        {
            SetStatus("Выберите папку в списке.", true);
        }
    }

    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        var value = NewExclusionBox.Text.Trim();
        if (value.Length == 0)
        {
            SetStatus("Введите маску или имя папки для исключения.", true);
            return;
        }

        if (_exclusions.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Такое исключение уже добавлено.", true);
            return;
        }

        _exclusions.Add(value);
        NewExclusionBox.Clear();
        SetStatus("Исключение добавлено.", false);
    }

    private void NewExclusionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddExclusion_Click(sender, e);
        }
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (ExclusionsList.SelectedItem is string exclusion)
        {
            _exclusions.Remove(exclusion);
            SetStatus("Исключение удалено.", false);
        }
        else
        {
            SetStatus("Выберите исключение в списке.", true);
        }
    }

    private void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        _rebuildRequested = true;
        if (!SaveSettings())
        {
            _rebuildRequested = false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _rebuildRequested = false;
        SaveSettings();
    }

    private bool SaveSettings()
    {
        if (!HotkeyDefinition.TryParse(HotkeyBox.Text, out var hotkey, out var hotkeyError) || hotkey is null)
        {
            SetStatus(hotkeyError, true);
            return false;
        }

        var settings = _original.Clone();
        settings.Hotkey = hotkey.Display;
        settings.Theme = ThemeDark.IsChecked == true
            ? "Dark"
            : ThemeLight.IsChecked == true
                ? "Light"
                : "System";
        settings.SearchEngine = EngineBing.IsChecked == true
            ? "Bing"
            : EngineDuckDuckGo.IsChecked == true
                ? "DuckDuckGo"
                : "Google";
        settings.RunAtStartup = AutostartToggle.IsChecked == true;
        settings.HideOnFocusLost = HideOnFocusLostToggle.IsChecked == true;
        settings.ShowOverFullscreenApps = FullscreenToggle.IsChecked == true;
        settings.MaxResults = (int)Math.Round(MaxResultsSlider.Value);
        settings.PriorityApplications = (int)Math.Round(AppPrioritySlider.Value);
        settings.PriorityFiles = (int)Math.Round(FilePrioritySlider.Value);
        settings.PriorityWeb = (int)Math.Round(WebPrioritySlider.Value);
        settings.IndexedFolders = _folders.ToList();
        settings.Exclusions = _exclusions.ToList();
        settings.UpdateFeedUrl = UpdateFeedBox.Text.Trim();

        if (settings.IndexedFolders.Count == 0)
        {
            SetStatus("Добавьте хотя бы одну папку для поиска файлов.", true);
            return false;
        }

        var previousHotkey = _hotkeyService.Current;
        if (!_hotkeyService.TryReplace(hotkey, out var registrationError))
        {
            SetStatus(registrationError, true);
            return false;
        }

        if (!_settingsService.Save(settings))
        {
            if (previousHotkey is not null)
            {
                _hotkeyService.TryReplace(previousHotkey, out _);
            }

            SetStatus("Не удалось записать настройки на диск.", true);
            return false;
        }

        AppliedSettings = settings;
        DialogResult = true;
        return true;
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageDialog.Show(
            this,
            "Очистка истории",
            "Удалить все сохранённые поисковые запросы? На статистику запусков это не повлияет.",
            "Очистить",
            "Отмена",
            "История поиска");
        if (!confirmed)
        {
            return;
        }

        await Task.Run(() => _storage.ClearHistory());
        HistoryCleared = true;
        SetStatus("История поиска очищена.", false);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdates)
        {
            return;
        }

        _checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        var originalContent = CheckUpdatesButton.Content;
        CheckUpdatesButton.Content = "Проверка…";
        try
        {
            var result = await UpdateService.CheckAsync(UpdateFeedBox.Text.Trim());
            var primary = result.DownloadUri is not null ? "Скачать" : "Понятно";
            var secondary = result.DownloadUri is not null ? "Позже" : null;
            var download = MessageDialog.Show(
                this,
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
        finally
        {
            CheckUpdatesButton.Content = originalContent;
            CheckUpdatesButton.IsEnabled = true;
            _checkingUpdates = false;
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            ShellService.OpenFolder(AppPaths.Root);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _indexTimer.Stop();
    }

    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }

    private void UpdateIndexSummary()
    {
        var count = _fileIndex.EntryCount;
        var text = $"{count:N0} файлов в индексе";
        if (_fileIndex.IsIndexing)
        {
            text += $" · сканирование: {_fileIndex.ScannedDuringRefresh:N0}";
        }
        else if (_fileIndex.LastIndexedUtc != default)
        {
            text += " · обновлён " + _fileIndex.LastIndexedUtc.ToLocalTime().ToString("HH:mm");
        }

        IndexSummaryText.Text = text;
    }

    private void UpdateValueLabels()
    {
        MaxResultsValue.Text = ((int)Math.Round(MaxResultsSlider.Value)).ToString();
        AppPriorityValue.Text = ((int)Math.Round(AppPrioritySlider.Value)).ToString();
        FilePriorityValue.Text = ((int)Math.Round(FilePrioritySlider.Value)).ToString();
        WebPriorityValue.Text = ((int)Math.Round(WebPrioritySlider.Value)).ToString();
    }

    private void SetStatus(string text, bool error)
    {
        ValidationText.Text = text;
        ValidationText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "SuccessBrush");
    }
}
