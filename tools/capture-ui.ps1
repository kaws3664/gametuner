# capture-ui.ps1 — screenshot the app's main window for UI verification.
#
# Primary path: the app renders its own main window offscreen (App.exe --capture-ui <png>),
# which works in session 0 / service contexts where there is no interactive desktop.
# Fallback: PrintWindow on the live window, for when the offscreen switch is unavailable.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/capture-ui.ps1 -Out artifacts/ui-after.png [-Exe <path>] [-WaitSec 40] [-ForceWindowCapture]
param(
    [string]$Exe = "",
    [string]$Out = "artifacts/ui-capture.png",
    [int]$WaitSec = 40,
    [switch]$ForceWindowCapture
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $candidates = @(
        (Join-Path $root "src/GameTuner.App/bin/Debug/net9.0-windows/GameTuner.App.exe"),
        (Join-Path $root "src/GameTuner.App/bin/Release/net9.0-windows/GameTuner.App.exe"),
        (Join-Path $root "artifacts/build/release/Release/net9.0-windows/GameTuner.App.exe")
    )
    $Exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Exe -or -not (Test-Path $Exe)) { throw "GameTuner.App.exe not found. Pass -Exe <path>." }
$OutPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutPath) | Out-Null

function Assert-Capture([string]$path) {
    if (-not (Test-Path $path)) { throw "Capture file was not created: $path" }
    $len = (Get-Item $path).Length
    if ($len -lt 5000) { throw "Capture looks empty ($len bytes): $path" }
    Write-Output "CAPTURED $path $len bytes"
}

if (-not $ForceWindowCapture) {
    $proc = Start-Process -FilePath $Exe -ArgumentList @("--capture-ui", $OutPath) -PassThru -WindowStyle Hidden
    if (-not $proc.WaitForExit($WaitSec * 1000)) {
        try { $proc.Kill() } catch {}
        throw "App did not exit within $WaitSec s in --capture-ui mode."
    }
    if ($proc.ExitCode -ne 0) { throw "App exited with code $($proc.ExitCode) in --capture-ui mode." }
    Assert-Capture $OutPath
    return
}

# ---- fallback: live window capture via PrintWindow ----
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class UiCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    public static IntPtr FindMainWindow(int targetPid) {
        IntPtr found = IntPtr.Zero;
        // no IsWindowVisible requirement: in session 0 the WPF window exists but is never marked visible.
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)targetPid && GetWindowTextLength(h) > 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[UiCap]::SetProcessDPIAware() | Out-Null

$proc = Start-Process -FilePath $Exe -PassThru
$hwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds($WaitSec)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $proc.Refresh()
    if ($proc.HasExited) { throw "App exited early with code $($proc.ExitCode)." }
    $h = [UiCap]::FindMainWindow($proc.Id)
    if ($h -ne [IntPtr]::Zero) { $hwnd = $h; break }
}
if ($hwnd -eq [IntPtr]::Zero) { try { $proc.Kill() } catch {}; throw "No main window appeared within $WaitSec s." }

Start-Sleep -Milliseconds 2500
[UiCap]::ShowWindow($hwnd, 9) | Out-Null
[UiCap]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800

$r = New-Object UiCap+RECT
[UiCap]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
if ($w -le 1 -or $h -le 1) { try { $proc.Kill() } catch {}; throw "Bad window rect: ${w}x${h} (no interactive desktop?) - use the default offscreen mode instead." }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $gfx.GetHdc()
$ok = [UiCap]::PrintWindow($hwnd, $hdc, 2)   # 2 = PW_RENDERFULLCONTENT (WPF / D3D)
$gfx.ReleaseHdc($hdc); $gfx.Dispose()
if (-not $ok) { $bmp.Dispose(); try { $proc.Kill() } catch {}; throw "PrintWindow failed." }
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
try { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 700; if (-not $proc.HasExited) { $proc.Kill() } } catch {}
Assert-Capture $OutPath
