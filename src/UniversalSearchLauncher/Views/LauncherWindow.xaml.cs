using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UniversalSearchLauncher.Services;
using UniversalSearchLauncher.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UniversalSearchLauncher.Views;

public partial class LauncherWindow : Window
{
    private readonly LauncherViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private HwndSource? _source;
    private bool _allowClose;
    private bool _suppressDeactivate;
    private bool _contextMenuOpen;
    private bool _isHiding;
    private bool _shownAtLeastOnce;

    public LauncherWindow(LauncherViewModel viewModel, SettingsService settingsService)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        DataContext = viewModel;
        InitializeComponent();

        _viewModel.HideRequested += (_, _) => HideLauncher();
        _viewModel.FocusSearchRequested += (_, _) => FocusSearch();
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    public event EventHandler? WindowHandleCreated;

    public bool IsLauncherVisible => IsVisible;

    public void ToggleLauncher()
    {
        if (IsVisible && !_isHiding)
        {
            HideLauncher();
        }
        else
        {
            ShowLauncher(force: false);
        }
    }

    public void ShowLauncher(bool force = false)
    {
        if (_allowClose)
        {
            return;
        }

        if (!force && !CanShowInCurrentContext())
        {
            return;
        }

        if (IsVisible)
        {
            if (_isHiding)
            {
                _isHiding = false;
                BeginAnimation(OpacityProperty, null);
                RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                Opacity = 1;
                RootTranslate.Y = 0;
            }
            else
            {
                ActivateAndFocus();
                return;
            }
        }

        var settings = _settingsService.Current;
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Min(700, Math.Max(300, workArea.Height - 24));

        _suppressDeactivate = true;
        Topmost = true;
        Opacity = 0;
        RootTranslate.Y = 14;
        Show();
        UpdateLayout();
        WindowPlacement.CenterOnCursorScreen(this);

        var animate = settings.EnableAnimations;
        if (animate)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            Opacity = 1;
            RootTranslate.Y = 0;
        }

        _shownAtLeastOnce = true;
        ActivateAndFocus();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _suppressDeactivate = false;
        };
        timer.Start();
    }

    public void HideLauncher(bool animated = true)
    {
        if (!IsVisible || _isHiding)
        {
            return;
        }

        _viewModel.PersistState();
        var animate = animated && _settingsService.Current.EnableAnimations;
        _isHiding = true;
        _suppressDeactivate = true;

        if (!animate)
        {
            CompleteHide();
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(115))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (_isHiding)
            {
                CompleteHide();
            }
        };

        BeginAnimation(OpacityProperty, fade);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(115))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
    }

    public void PrepareForShutdown()
    {
        _allowClose = true;
    }

    private void CompleteHide()
    {
        Hide();
        BeginAnimation(OpacityProperty, null);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 1;
        RootTranslate.Y = 0;
        _isHiding = false;
        _suppressDeactivate = false;
        Topmost = false;
    }

    private bool CanShowInCurrentContext()
    {
        if (_settingsService.Current.ShowOverFullscreenApps)
        {
            return true;
        }

        if (!FullscreenDetector.IsForegroundWindowFullscreen())
        {
            return true;
        }

        AppLog.Info("Показ поиска пропущен: активно полноэкранное приложение.");
        return false;
    }

    private void ActivateAndFocus()
    {
        WindowPlacement.ForceForeground(this);
        Activate();
        FocusSearch();
    }

    private void FocusSearch()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherViewModel.SelectedIndex))
        {
            if (_viewModel.SelectedResult is { } selected)
            {
                ResultsList.ScrollIntoView(selected);
            }
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        WindowPlacement.ApplyDarkTitleBar(this, ThemeService.IsDark(_settingsService.Current.Theme));
        WindowHandleCreated?.Invoke(this, EventArgs.Empty);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || _isHiding)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var maximumTop = workArea.Bottom - ActualHeight - 8;
        if (Top > maximumTop)
        {
            Top = Math.Max(workArea.Top + 8, maximumTop);
        }
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == SingleInstanceService.ShowMessage)
        {
            Dispatcher.BeginInvoke(new Action(() => ShowLauncher(force: false)), DispatcherPriority.Normal);
            handled = true;
        }

        return 0;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_contextMenuOpen)
            {
                return;
            }

            e.Handled = true;
            HideLauncher();
            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            _viewModel.MoveSelection(-1);
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            _viewModel.MoveSelection(1);
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.ActivateSelected();
            return;
        }

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            _viewModel.CycleFilter(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            return;
        }

        if (e.Key == Key.Back && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _viewModel.ClearQuery();
            return;
        }

        if (e.Key == Key.OemComma && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _viewModel.OpenSettingsCommand.Execute(null);
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            _viewModel.TogglePin(_viewModel.SelectedResult);
            return;
        }

        if (e.Key == Key.PageDown)
        {
            e.Handled = true;
            _viewModel.MoveSelection(5);
            return;
        }

        if (e.Key == Key.PageUp)
        {
            e.Handled = true;
            _viewModel.MoveSelection(-5);
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            var element = source;
            while (element is not null)
            {
                if (element is TextBox or Button or ScrollBar or ListBoxItem)
                {
                    return;
                }

                element = VisualTreeHelper.GetParent(element);
            }
        }

        FocusSearch();
    }

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_shownAtLeastOnce || _suppressDeactivate || _contextMenuOpen || _isHiding)
        {
            return;
        }

        if (!_settingsService.Current.HideOnFocusLost)
        {
            return;
        }

        await Task.Delay(130);
        if (IsVisible && !IsActive && !_contextMenuOpen && !_suppressDeactivate)
        {
            HideLauncher();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideLauncher();
        }
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultItemViewModel item)
        {
            _viewModel.SelectedIndex = ResultsList.SelectedIndex;
            ResultsList.ScrollIntoView(item);
        }
    }

    private void ResultContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = true;
        _suppressDeactivate = true;
    }

    private void ResultContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _suppressDeactivate = false;
                if (IsVisible && !IsActive)
                {
                    ActivateAndFocus();
                }
            };
            timer.Start();
        }, DispatcherPriority.Background);
    }

    private static ResultItemViewModel? GetContextItem(object sender)
    {
        return (sender as MenuItem)?.DataContext as ResultItemViewModel;
    }

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Activate(GetContextItem(sender));
    }

    private void ContextReveal_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenContainingFolder(GetContextItem(sender));
    }

    private void ContextCopy_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CopyPath(GetContextItem(sender));
    }

    private void ContextPin_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.TogglePin(GetContextItem(sender));
    }
}
