using System.Windows.Input;

namespace UniversalSearchLauncher.Services;

public sealed class HotkeyDefinition : IEquatable<HotkeyDefinition>
{
    public HotkeyDefinition(ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public ModifierKeys Modifiers { get; }
    public Key Key { get; }

    public uint NativeModifiers
    {
        get
        {
            var result = NativeMethods.ModNoRepeat;
            if (Modifiers.HasFlag(ModifierKeys.Control)) result |= NativeMethods.ModControl;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) result |= NativeMethods.ModAlt;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) result |= NativeMethods.ModShift;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) result |= NativeMethods.ModWin;
            return result;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public string Display => Format(Modifiers, Key);

    public static bool TryParse(string? text, out HotkeyDefinition? definition, out string error)
    {
        definition = null;
        error = string.Empty;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "Введите комбинацию клавиш.";
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "Комбинация должна содержать модификатор и клавишу, например Alt+Space.";
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key key = Key.None;

        foreach (var part in parts)
        {
            if (TryParseModifier(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (key != Key.None)
            {
                error = "В комбинации можно указать только одну клавишу.";
                return false;
            }

            key = ParseKey(part);
        }

        if (key == Key.None)
        {
            error = "Не удалось распознать клавишу комбинации.";
            return false;
        }

        if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24))
        {
            error = "Добавьте хотя бы один модификатор: Ctrl, Alt, Shift или Win.";
            return false;
        }

        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            error = "Нужно указать обычную клавишу вместе с модификатором.";
            return false;
        }

        definition = new HotkeyDefinition(modifiers, key);
        return true;
    }

    public static HotkeyDefinition FromKeyboard(ModifierKeys modifiers, Key key)
    {
        if (key == Key.System)
        {
            key = Key.LeftAlt;
        }

        return new HotkeyDefinition(modifiers, key);
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    public static string FormatKey(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "Num" + ((int)key - (int)Key.NumPad0);
        }

        return key switch
        {
            Key.Space => "Space",
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Back => "Backspace",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => key.ToString()
        };
    }

    private static bool TryParseModifier(string value, out ModifierKeys modifier)
    {
        modifier = value.ToLowerInvariant() switch
        {
            "ctrl" or "control" or "ctl" => ModifierKeys.Control,
            "alt" => ModifierKeys.Alt,
            "shift" => ModifierKeys.Shift,
            "win" or "windows" or "meta" or "super" => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };
        return modifier != ModifierKeys.None;
    }

    private static Key ParseKey(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 1)
        {
            var ch = char.ToUpperInvariant(normalized[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                return Key.A + (ch - 'A');
            }

            if (ch is >= '0' and <= '9')
            {
                return Key.D0 + (ch - '0');
            }
        }

        if (normalized.Equals("Esc", StringComparison.OrdinalIgnoreCase))
        {
            return Key.Escape;
        }

        if (normalized.Equals("Space", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Spacebar", StringComparison.OrdinalIgnoreCase))
        {
            return Key.Space;
        }

        if (normalized.Equals("Enter", StringComparison.OrdinalIgnoreCase))
        {
            return Key.Return;
        }

        if (normalized.Equals("Backspace", StringComparison.OrdinalIgnoreCase))
        {
            return Key.Back;
        }

        if (normalized.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(normalized[3..], out var numPad) &&
            numPad is >= 0 and <= 9)
        {
            return Key.NumPad0 + numPad;
        }

        return Enum.TryParse<Key>(normalized, true, out var key) ? key : Key.None;
    }

    public bool Equals(HotkeyDefinition? other)
    {
        return other is not null && other.Modifiers == Modifiers && other.Key == Key;
    }

    public override bool Equals(object? obj) => obj is HotkeyDefinition other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Modifiers, Key);

    public override string ToString() => Display;
}
