param(
    [string]$Action = 'show',
    [string]$Text = '',
    [string]$Name = 'ui',
    [int]$WaitMs = 700
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

if (-not ('UslUiProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class UslUiProbe {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static IntPtr FindMain(uint target) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target) {
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                if (cls.ToString().StartsWith("HwndWrapper[UniversalSearchLauncher")) {
                    var title = new StringBuilder(256);
                    GetWindowText(h, title, 256);
                    if (title.ToString() == "Universal Search Launcher") {
                        found = h;
                        return false;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
}

$process = Get-Process dotnet -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $process) {
    throw 'No dotnet process found.'
}

$hwnd = [UslUiProbe]::FindMain([uint32]$process.Id)
if ($hwnd -eq [IntPtr]::Zero) {
    throw 'Launcher window not found.'
}

function Get-Shot {
    param([string]$fileName)
    $rect = New-Object UslUiProbe+RECT
    [UslUiProbe]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $path = Join-Path (Get-Location) (".testdata\" + $fileName)
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Host "SHOT $path"
}

switch ($Action) {
    'show' {
        [UslUiProbe]::PostMessage($hwnd, 0x0312, [IntPtr]0x554C, [IntPtr]0) | Out-Null
        Start-Sleep -Milliseconds $WaitMs
        [UslUiProbe]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 150
        Get-Shot $Name
    }
    'hide' {
        [UslUiProbe]::PostMessage($hwnd, 0x0312, [IntPtr]0x554C, [IntPtr]0) | Out-Null
    }
    'keys' {
        [UslUiProbe]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait($Text)
        Start-Sleep -Milliseconds $WaitMs
        Get-Shot $Name
    }
    'shot' {
        Get-Shot $Name
    }
    'clear' {
        [UslUiProbe]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        [System.Windows.Forms.SendKeys]::SendWait('{DEL}')
        Start-Sleep -Milliseconds $WaitMs
        Get-Shot $Name
    }
    default {
        Write-Host "PID=$($process.Id) HWND=$hwnd"
    }
}
