using System.Runtime.InteropServices;
using System.Text;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Detects whether the current foreground window looks like a fullscreen game,
/// video player, presentation or kiosk application. Used to avoid covering
/// fullscreen content when the user has disabled that behavior.
/// </summary>
internal static class FullscreenDetector
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const int GaRoot = 2;
    private const int MonitorDefaultToNearest = 2;
    private const int DwmwaCloaked = 14;

    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;

    public static bool IsForegroundWindowFullscreen()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == GetShellWindow())
        {
            return false;
        }

        var root = GetAncestor(foreground, GaRoot);
        if (root != IntPtr.Zero)
        {
            foreground = root;
        }

        if (!IsWindowVisible(foreground) || IsIconic(foreground))
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        try
        {
            var cloaked = 0;
            if (DwmGetWindowAttribute(foreground, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }
        }
        catch
        {
            // DWM is not available on very old systems or during shell shutdown.
        }

        var className = GetClassName(foreground);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        var notificationState = 0;
        try
        {
            _ = SHQueryUserNotificationState(out notificationState);
        }
        catch
        {
            notificationState = 0;
        }

        var shellReportsFullscreen = notificationState is QunsRunningD3DFullScreen or QunsPresentationMode;
        if (shellReportsFullscreen)
        {
            return true;
        }

        if (!GetWindowRect(foreground, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        var monitorWidth = monitorInfo.Monitor.Right - monitorInfo.Monitor.Left;
        var monitorHeight = monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top;
        if (width < monitorWidth - 8 || height < monitorHeight - 8)
        {
            return false;
        }

        var style = GetWindowLongPtr(foreground, GwlStyle).ToInt64();
        var hasCaption = (style & WsCaption) == WsCaption;

        // A normal maximized window covers the work area but still has a caption
        // and a visible taskbar. Borderless fullscreen windows have no caption.
        return !hasCaption;
    }

    private static string GetClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameNative(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameNative(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
