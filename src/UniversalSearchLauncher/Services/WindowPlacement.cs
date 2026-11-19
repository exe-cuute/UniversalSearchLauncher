using System.Windows;
using System.Windows.Interop;

namespace UniversalSearchLauncher.Services;

public static class WindowPlacement
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static void CenterOnCursorScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var information = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref information))
        {
            return;
        }

        if (!GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var width = windowRect.Width;
        var height = windowRect.Height;
        var x = information.Work.Left + Math.Max(0, (information.Work.Width - width) / 2);
        var y = information.Work.Top + Math.Max(0, (information.Work.Height - height) / 2);

        NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public static void ForceForeground(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            NativeMethods.ShowWindow(handle, NativeMethods.SwShow);
            NativeMethods.BringWindowToTop(handle);
            NativeMethods.SetForegroundWindow(handle);
            NativeMethods.SetFocus(handle);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var value = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // Not available on older Windows builds.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativeMethods.Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeMethods.Rect rectangle);
}
