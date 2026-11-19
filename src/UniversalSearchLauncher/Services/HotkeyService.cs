using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Owns the single global hotkey registration. A failed change leaves the
/// previous working registration in place.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x554C;
    private HwndSource? _source;
    private nint _window;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? Pressed;

    public HotkeyDefinition? Current { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRegistered => _registered;

    public void Initialize(HwndSource source, HotkeyDefinition definition)
    {
        _source = source;
        _window = source.Handle;
        source.AddHook(WndProc);
        TryReplace(definition, out _);
    }

    public bool TryReplace(HotkeyDefinition definition, out string error)
    {
        error = string.Empty;
        if (_disposed)
        {
            error = "Служба горячих клавиш уже остановлена.";
            return false;
        }

        if (_registered && Current is not null && Current.Equals(definition))
        {
            return true;
        }

        var candidate = definition.NativeModifiers;
        var virtualKey = definition.VirtualKey;
        if (virtualKey == 0)
        {
            error = "Некорректная клавиша комбинации.";
            return false;
        }

        var previous = Current;
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window, HotkeyId);
            _registered = false;
        }

        if (!NativeMethods.RegisterHotKey(_window, HotkeyId, candidate, virtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = GetErrorMessage(code);
            LastError = error;
            AppLog.Warn($"Не удалось зарегистрировать {definition.Display}: {error} ({code})");

            if (previous is not null &&
                NativeMethods.RegisterHotKey(_window, HotkeyId, previous.NativeModifiers, previous.VirtualKey))
            {
                Current = previous;
                _registered = true;
            }
            else if (previous is not null)
            {
                AppLog.Error("Не удалось восстановить предыдущую горячую клавишу после конфликта.");
            }

            return false;
        }

        Current = definition;
        _registered = true;
        LastError = null;
        AppLog.Info("Зарегистрирована горячая клавиша " + definition.Display);
        return true;
    }

    public bool TryReplace(string hotkeyText, out string error)
    {
        if (!HotkeyDefinition.TryParse(hotkeyText, out var definition, out error) || definition is null)
        {
            return false;
        }

        return TryReplace(definition, out error);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    private static string GetErrorMessage(int code)
    {
        return code switch
        {
            1409 => "Комбинация уже зарегистрирована другой программой. Выберите другую.",
            5 => "Недостаточно прав для регистрации этой комбинации.",
            87 => "Некорректная комбинация клавиш.",
            _ => new Win32Exception(code).Message
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registered && _window != 0)
        {
            NativeMethods.UnregisterHotKey(_window, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
