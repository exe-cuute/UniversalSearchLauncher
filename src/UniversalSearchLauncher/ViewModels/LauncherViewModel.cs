using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using UniversalSearchLauncher.Core.Models;
using UniversalSearchLauncher.Core.Search;
using UniversalSearchLauncher.Services;

namespace UniversalSearchLauncher.ViewModels;

public sealed class LauncherViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly StorageService _storage;
    private readonly SearchService _search;
    private readonly UsageService _usage;
    private readonly AppIndexService _appIndex;
    private readonly FileIndexService _fileIndex;
    private readonly IconService _icons;
    private readonly DispatcherTimer _statusTimer;

    private Settings _settings;
    private SearchSnapshot? _lastSnapshot;
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private int _selectedIndex = -1;
    private SearchCategory? _selectedCategory;
    private string _query = string.Empty;
    private bool _isSearching;
    private bool _hasResults;
    private string _indexStatusText = "Подготовка индекса…";
    private string _indexStatusTooltip = string.Empty;
    private bool _isIndexing;
    private string _resultSummary = string.Empty;
    private bool _isNoticeVisible;
    private string _noticeText = string.Empty;
    private bool _noticeIsError;
    private int _noticeVersion;
    private string? _lastSelectedKey;
    private string? _lastSelectedTitle;
    private bool _disposed;
    private string? _preferredSelectionKey;

    public LauncherViewModel(
        SettingsService settingsService,
        StorageService storage,
        SearchService search,
        UsageService usage,
        AppIndexService appIndex,
        FileIndexService fileIndex,
        IconService icons)
    {
        _settingsService = settingsService;
        _storage = storage;
        _search = search;
        _usage = usage;
        _appIndex = appIndex;
        _fileIndex = fileIndex;
        _icons = icons;
        _settings = settingsService.Current;

        var state = storage.LoadUiState();
        _lastSelectedKey = state.LastSelectedKey;
        _lastSelectedTitle = state.LastSelectedTitle;

        FilterChips = new ObservableCollection<FilterChipViewModel>
        {
            new(null, "Все"),
            new(SearchCategory.Application, "Приложения"),
            new(SearchCategory.File, "Файлы"),
            new(SearchCategory.Web, "Интернет")
        };
        FilterChips[0].IsSelected = true;

        HistoryQueries = new ObservableCollection<string>();
        LoadHistory();

        ClearQueryCommand = new RelayCommand(ClearQuery);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        SetFilterCommand = new RelayCommand(parameter => SetFilter(parameter as FilterChipViewModel));
        UseHistoryCommand = new RelayCommand(parameter => UseHistory(parameter as string));
        ActivateResultCommand = new RelayCommand(parameter => Activate(parameter as ResultItemViewModel));
        ActivateSelectedCommand = new RelayCommand(ActivateSelected);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        DismissNoticeCommand = new RelayCommand(DismissNotice);

        _appIndex.IndexChanged += OnAppIndexChanged;
        _fileIndex.IndexChanged += OnFileIndexChanged;
        _fileIndex.StateChanged += OnFileIndexStateChanged;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    public event EventHandler? HideRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? FocusSearchRequested;

    public ObservableCollection<ResultItemViewModel> Results { get; } = new();
    public ObservableCollection<FilterChipViewModel> FilterChips { get; }
    public ObservableCollection<string> HistoryQueries { get; }
    public RelayCommand ClearQueryCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand UseHistoryCommand { get; }
    public RelayCommand ActivateResultCommand { get; }
    public RelayCommand ActivateSelectedCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasQuery));
            OnPropertyChanged(nameof(IsFilterBarVisible));
            OnPropertyChanged(nameof(ShowHistory));
            if (string.IsNullOrWhiteSpace(value))
            {
                _selectedCategory = null;
                foreach (var chip in FilterChips)
                {
                    chip.IsSelected = chip.Category is null;
                }
            }

            _ = RunSearchAsync();
        }
    }

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool IsFilterBarVisible => HasQuery;
    public bool ShowHistory => !HasQuery && HistoryQueries.Count > 0;
    public bool HasResults
    {
        get => _hasResults;
        private set
        {
            if (SetProperty(ref _hasResults, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool ShowEmptyState => !HasResults;
    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Results.Count == 0 ? -1 : Math.Clamp(value, -1, Results.Count - 1);
            if (!SetProperty(ref _selectedIndex, clamped))
            {
                return;
            }

            for (var i = 0; i < Results.Count; i++)
            {
                Results[i].IsSelected = i == clamped;
            }

            if (clamped >= 0)
            {
                _lastSelectedKey = Results[clamped].Id;
                _lastSelectedTitle = Results[clamped].Title;
            }

            OnPropertyChanged(nameof(SelectedResult));
        }
    }

    public ResultItemViewModel? SelectedResult =>
        SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    public string HotkeyHint => _settings.Hotkey.Replace("+", " ").ToUpperInvariant();
    public string VersionText => "v" + UpdateService.CurrentVersionDisplay;
    public string IndexStatusText
    {
        get => _indexStatusText;
        private set => SetProperty(ref _indexStatusText, value);
    }

    public string IndexStatusTooltip
    {
        get => _indexStatusTooltip;
        private set => SetProperty(ref _indexStatusTooltip, value);
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set => SetProperty(ref _isIndexing, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public bool IsNoticeVisible
    {
        get => _isNoticeVisible;
        private set => SetProperty(ref _isNoticeVisible, value);
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => SetProperty(ref _noticeText, value);
    }

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => SetProperty(ref _noticeIsError, value);
    }

    public string EmptyTitle
    {
        get
        {
            if (!HasQuery)
            {
                return "Начните вводить запрос";
            }

            if (_selectedCategory is not null)
            {
                return "В этой категории ничего не найдено";
            }

            return "Ничего не найдено";
        }
    }

    public string EmptyDescription
    {
        get
        {
            if (!HasQuery)
            {
                return "Приложения, файлы и интернет — в одном быстром поиске.";
            }

            if (_selectedCategory is not null)
            {
                return "Нажмите Tab, чтобы переключить категорию.";
            }

            return "Попробуйте изменить запрос или выполните поиск в интернете.";
        }
    }

    public void Start()
    {
        UpdateStatus();
        _statusTimer.Start();
        _ = RunSearchAsync();
    }

    public void UpdateSettings(Settings settings)
    {
        _settings = settings.Clone();
        OnPropertyChanged(nameof(HotkeyHint));
        _ = RunSearchAsync();
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var current = SelectedIndex < 0 ? 0 : SelectedIndex;
        var next = current + delta;
        if (next < 0)
        {
            next = Results.Count - 1;
        }
        else if (next >= Results.Count)
        {
            next = 0;
        }

        SelectedIndex = next;
    }

    public void CycleFilter(int direction = 1)
    {
        if (!HasQuery || FilterChips.Count == 0)
        {
            return;
        }

        direction = direction < 0 ? -1 : 1;
        var current = FilterChips.ToList().FindIndex(chip => chip.IsSelected);
        for (var offset = 1; offset <= FilterChips.Count; offset++)
        {
            var index = ((current + direction * offset) % FilterChips.Count + FilterChips.Count) % FilterChips.Count;
            var candidate = FilterChips[index];
            if (candidate.Count > 0 || candidate.Category is null)
            {
                SetFilter(candidate);
                return;
            }
        }
    }

    public void SetFilter(FilterChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        _selectedCategory = chip.Category;
        foreach (var item in FilterChips)
        {
            item.IsSelected = ReferenceEquals(item, chip);
        }

        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        ApplyFilteredResults(SelectedResult?.Id ?? _lastSelectedKey);
        UpdateResultSummary();
    }

    public void ActivateSelected()
    {
        if (SelectedResult is not null)
        {
            Activate(SelectedResult);
            return;
        }

        if (!HasQuery)
        {
            return;
        }

        // No result is selected (for example the active category is empty):
        // Enter still performs the universal web search for the typed query.
        ExecuteWebFallback();
    }

    public void Activate(ResultItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var index = Results.IndexOf(item);
        if (index >= 0)
        {
            SelectedIndex = index;
        }

        if (!ShellService.TryExecute(item.Model, out var error))
        {
            ShowNotice(string.IsNullOrWhiteSpace(error) ? "Не удалось открыть элемент." : error, true);
            return;
        }

        _usage.RecordLaunch(item.Model);
        if (HasQuery)
        {
            string query = Query;
            string title = item.Title;
            Task.Run(() => _storage.AddHistory(query, title));
            AddHistoryQuery(Query);
        }

        _lastSelectedKey = item.Id;
        _lastSelectedTitle = item.Title;
        PersistState();
        HideRequested?.Invoke(this, EventArgs.Empty);
        _ = ResetAfterLaunchAsync();
    }

    public void UseHistory(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        Query = query;
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ClearQuery()
    {
        Query = string.Empty;
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        Task.Run(() => _storage.ClearHistory());
        HistoryQueries.Clear();
        OnPropertyChanged(nameof(ShowHistory));
        ShowNotice("История поиска очищена.", false);
    }

    public void ReloadHistory()
    {
        LoadHistory();
        OnPropertyChanged(nameof(ShowHistory));
    }

    public void PersistState()
    {
        var selected = SelectedResult;
        if (selected is not null)
        {
            _lastSelectedKey = selected.Id;
            _lastSelectedTitle = selected.Title;
        }

        var state = new UiState
        {
            LastSelectedKey = _lastSelectedKey,
            LastSelectedTitle = _lastSelectedTitle
        };
        Task.Run(() => _storage.SaveUiState(state));
    }

    public void OpenContainingFolder(ResultItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            ShellService.OpenContainingFolder(item.Model);
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, true);
        }
    }

    public void CopyPath(ResultItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var value = item.Model.Target ?? item.Model.Subtitle;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            Clipboard.SetText(value);
            ShowNotice("Путь скопирован в буфер обмена.", false);
        }
        catch (Exception ex)
        {
            ShowNotice("Не удалось скопировать: " + ex.Message, true);
        }
    }

    public void TogglePin(ResultItemViewModel? item)
    {
        if (item is null || item.Model.Category == SearchCategory.Web)
        {
            return;
        }

        var pinned = _usage.TogglePin(item.Model);
        item.IsPinned = pinned;
        ShowNotice(pinned ? "Закреплено." : "Откреплено.", false);
        _ = RunSearchAsync();
    }

    public void DismissNotice()
    {
        IsNoticeVisible = false;
    }

    private async Task RunSearchAsync()
    {
        if (_disposed)
        {
            return;
        }

        var query = Query;
        var generation = Interlocked.Increment(ref _searchGeneration);
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchCts, cts);
        previous?.Cancel();

        IsSearching = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                await Task.Delay(12, cts.Token);
            }

            var snapshot = await _search.SearchAsync(
                query,
                _settings,
                partial =>
                {
                    if (!_disposed && generation == _searchGeneration && ReferenceEquals(_searchCts, cts))
                    {
                        ApplySnapshot(partial, partial: true);
                    }
                },
                cts.Token);

            if (!_disposed && generation == _searchGeneration && ReferenceEquals(_searchCts, cts))
            {
                ApplySnapshot(snapshot, partial: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                AppLog.Error("Поиск завершился ошибкой.", ex);
                ShowNotice("Не удалось выполнить поиск: " + ex.Message, true);
            }
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                IsSearching = false;
            }
        }
    }

    private void ApplySnapshot(SearchSnapshot snapshot, bool partial)
    {
        var previousSelection = SelectedResult?.Id ?? _preferredSelectionKey ?? _lastSelectedKey;
        _preferredSelectionKey = null;
        _lastSnapshot = snapshot;

        UpdateFilterCounts(snapshot);
        ApplyFilteredResults(previousSelection);

        ResultSummary = partial
            ? "Поиск…"
            : Results.Count == 0
                ? "Ничего не найдено"
                : FormatResultCount(Results.Count);
    }

    private void UpdateResultSummary()
    {
        if (IsSearching)
        {
            ResultSummary = "Поиск…";
            return;
        }

        ResultSummary = Results.Count == 0
            ? "Ничего не найдено"
            : FormatResultCount(Results.Count);
    }

    private void ApplyFilteredResults(string? preferredSelection)
    {
        var snapshot = _lastSnapshot;
        if (snapshot is null)
        {
            return;
        }

        var filtered = _selectedCategory is null
            ? snapshot.Results.ToList()
            : snapshot.Results.Where(result => result.Category == _selectedCategory).ToList();

        var previous = preferredSelection;
        Results.Clear();
        _selectedIndex = -1;

        var lastCategory = (SearchCategory?)null;
        var recentHeaderShown = false;
        foreach (var result in filtered)
        {
            var isRecentView = !HasQuery;
            var showHeader = isRecentView
                ? !recentHeaderShown
                : lastCategory != result.Category;
            var item = new ResultItemViewModel(result, _icons)
            {
                ShowHeader = showHeader,
                GroupTitle = isRecentView ? "Быстрый доступ" : CategoryTitle(result.Category)
            };

            if (showHeader)
            {
                recentHeaderShown = true;
            }

            lastCategory = result.Category;
            Results.Add(item);
        }

        HasResults = Results.Count > 0;

        var targetIndex = 0;
        if (!string.IsNullOrEmpty(previous))
        {
            var found = Results.ToList().FindIndex(item => string.Equals(item.Id, previous, StringComparison.OrdinalIgnoreCase));
            if (found >= 0)
            {
                targetIndex = found;
            }
        }

        if (Results.Count > 0)
        {
            _selectedIndex = -1;
            SelectedIndex = targetIndex;
        }
        else
        {
            _selectedIndex = -1;
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(SelectedResult));
        }

        if (IsSearching && _lastSnapshot?.IsPartial == true)
        {
            ResultSummary = "Поиск…";
        }
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    private void UpdateFilterCounts(SearchSnapshot snapshot)
    {
        foreach (var chip in FilterChips)
        {
            chip.Count = chip.Category is null
                ? snapshot.Results.Count
                : snapshot.Results.Count(result => result.Category == chip.Category);
            chip.IsSelected = chip.Category == _selectedCategory;
        }
    }

    private void ExecuteWebFallback()
    {
        try
        {
            var intent = QueryIntent.Parse(Query);
            var uri = intent.GetSearchUri(_settings.SearchEngine);
            ShellService.OpenUrl(uri.AbsoluteUri);
            string query = Query;
            Task.Run(() => _storage.AddHistory(query, "Поиск в интернете"));
            AddHistoryQuery(Query);
            PersistState();
            HideRequested?.Invoke(this, EventArgs.Empty);
            _ = ResetAfterLaunchAsync();
        }
        catch (Exception ex)
        {
            ShowNotice("Не удалось открыть браузер: " + ex.Message, true);
        }
    }

    private async Task ResetAfterLaunchAsync()
    {
        try
        {
            await Task.Delay(180);
        }
        catch
        {
            // Ignored.
        }

        if (_disposed)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _selectedCategory = null;
            foreach (var chip in FilterChips)
            {
                chip.IsSelected = chip.Category is null;
            }

            _query = string.Empty;
            OnPropertyChanged(nameof(Query));
            OnPropertyChanged(nameof(HasQuery));
            OnPropertyChanged(nameof(IsFilterBarVisible));
            OnPropertyChanged(nameof(ShowHistory));
            _ = RunSearchAsync();
        });
    }

    private void LoadHistory()
    {
        HistoryQueries.Clear();
        foreach (var entry in _storage.LoadHistory(8))
        {
            if (!HistoryQueries.Contains(entry.Query, StringComparer.OrdinalIgnoreCase))
            {
                HistoryQueries.Add(entry.Query);
            }
        }
    }

    private void AddHistoryQuery(string query)
    {
        var existing = HistoryQueries
            .Select((value, index) => (value, index))
            .FirstOrDefault(item => string.Equals(item.value, query, StringComparison.OrdinalIgnoreCase));
        if (existing.value is not null)
        {
            HistoryQueries.Move(existing.index, 0);
        }
        else
        {
            HistoryQueries.Insert(0, query);
            while (HistoryQueries.Count > 8)
            {
                HistoryQueries.RemoveAt(HistoryQueries.Count - 1);
            }
        }

        OnPropertyChanged(nameof(ShowHistory));
    }

    private void OnAppIndexChanged(object? sender, EventArgs e)
    {
        DispatchIndexChange();
    }

    private void OnFileIndexChanged(object? sender, EventArgs e)
    {
        DispatchIndexChange();
    }

    private void OnFileIndexStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(UpdateStatus), DispatcherPriority.Background);
    }

    private void DispatchIndexChange()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            UpdateStatus();
            if (!_disposed)
            {
                _ = RunSearchAsync();
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateStatus()
    {
        if (_disposed)
        {
            return;
        }

        var fileCount = _fileIndex.EntryCount;
        var appCount = _appIndex.Entries.Count;
        var indexing = _fileIndex.IsIndexing;
        IsIndexing = indexing || _appIndex.IsRefreshing;

        if (indexing)
        {
            IndexStatusText = $"Индексирование… {_fileIndex.ScannedDuringRefresh:N0} объектов";
        }
        else if (_appIndex.IsRefreshing && appCount == 0)
        {
            IndexStatusText = "Поиск приложений…";
        }
        else if (fileCount == 0)
        {
            IndexStatusText = "Файлы ещё не проиндексированы";
        }
        else
        {
            IndexStatusText = $"{fileCount:N0} файлов · {appCount} приложений";
        }

        var details = new List<string>
        {
            $"Файлов в индексе: {fileCount:N0}",
            $"Приложений: {appCount:N0}"
        };
        if (_fileIndex.LastIndexedUtc != default)
        {
            details.Add("Индекс обновлён: " + _fileIndex.LastIndexedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
        }

        if (!string.IsNullOrWhiteSpace(_fileIndex.LastError))
        {
            details.Add(_fileIndex.LastError);
        }

        IndexStatusTooltip = string.Join("\n", details);
    }

    private async void ShowNotice(string text, bool error)
    {
        var version = ++_noticeVersion;
        NoticeText = text;
        NoticeIsError = error;
        IsNoticeVisible = true;
        try
        {
            await Task.Delay(error ? 5000 : 2600);
        }
        catch
        {
            return;
        }

        if (version == _noticeVersion && !_disposed)
        {
            IsNoticeVisible = false;
        }
    }

    private static string CategoryTitle(SearchCategory category)
    {
        return category switch
        {
            SearchCategory.Application => "Приложения",
            SearchCategory.File => "Файлы и папки",
            _ => "В интернете"
        };
    }

    private static string FormatResultCount(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;
        var word = mod10 == 1 && mod100 != 11
            ? "результат"
            : mod10 is >= 2 and <= 4 && !(mod100 is >= 12 and <= 14)
                ? "результата"
                : "результатов";
        return $"{count} {word}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _appIndex.IndexChanged -= OnAppIndexChanged;
        _fileIndex.IndexChanged -= OnFileIndexChanged;
        _fileIndex.StateChanged -= OnFileIndexStateChanged;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }
}
