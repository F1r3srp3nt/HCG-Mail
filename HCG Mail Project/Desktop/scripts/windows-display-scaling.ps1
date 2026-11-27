<#
.SYNOPSIS
  HCG Mail at Windows display scalings 100 %, 125 %, 150 %, 175 % and 200 %, on this Windows machine.
.DESCRIPTION
  1. Sets the display to its largest available resolution (Windows offers higher scalings only on larger displays).
  2. For each scaling: sets it live, the way Settings › Display › Scale does (DisplayConfigSetDeviceInfo), starts HCG Mail
     with an isolated data folder, reads the DPI Windows gives HCG Mail's window (GetDpiForWindow), captures the window,
     checks that the window has rendered content and closes HCG Mail through its window.
  3. Restores the original scaling and resolution.
  A scaling this display cannot offer is reported as "not available on this display", never as passed.
  Writes display-scaling.json and window-<scale>.png to -Out.
  Usage: windows-display-scaling.ps1 -Exe <HCGMail.exe> -Out <folder> [-Theme HcgFuture|HcgClassic]
#>
param(
  [Parameter(Mandatory)] [string] $Exe,
  [Parameter(Mandatory)] [string] $Out,
  [string] $Theme = 'HcgFuture'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class HcgDisplay
{
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] public struct PathSourceInfo { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct PathInfo
    {
        public PathSourceInfo sourceInfo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] targetInfo;
        public uint flags;
    }
    [StructLayout(LayoutKind.Sequential)] public struct ModeInfo { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] data; }
    [StructLayout(LayoutKind.Sequential)] public struct Header { public int type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)] public struct DpiGet { public Header header; public int minScaleRel; public int curScaleRel; public int maxScaleRel; }
    [StructLayout(LayoutKind.Sequential)] public struct DpiSet { public Header header; public int scaleRel; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DpiGet info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref DpiSet info);

    // Windows' scaling steps; DisplayConfig reports and takes them relative to the recommended one.
    public static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    static PathSourceInfo PrimarySource()
    {
        uint np, nm;
        int r = GetDisplayConfigBufferSizes(2 /* QDC_ONLY_ACTIVE_PATHS */, out np, out nm);
        if (r != 0) throw new Exception("GetDisplayConfigBufferSizes " + r);
        var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
        r = QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero);
        if (r != 0 || np == 0) throw new Exception("QueryDisplayConfig " + r);
        return paths[0].sourceInfo;
    }

    /// <summary>Recommended, current and largest available scaling in percent.</summary>
    public static int[] Get()
    {
        var src = PrimarySource();
        var g = new DpiGet();
        g.header.type = -3; g.header.size = (uint)Marshal.SizeOf(typeof(DpiGet)); g.header.adapterId = src.adapterId; g.header.id = src.id;
        int r = DisplayConfigGetDeviceInfo(ref g);
        if (r != 0) throw new Exception("DisplayConfigGetDeviceInfo " + r);
        int rec = Math.Abs(g.minScaleRel);
        return new[] { Steps[rec], Steps[Math.Min(rec + g.curScaleRel, Steps.Length - 1)], Steps[Math.Min(rec + g.maxScaleRel, Steps.Length - 1)] };
    }

    /// <summary>Sets the scaling (percent); returns false when this display does not offer it.</summary>
    public static bool Set(int percent)
    {
        var src = PrimarySource();
        var g = new DpiGet();
        g.header.type = -3; g.header.size = (uint)Marshal.SizeOf(typeof(DpiGet)); g.header.adapterId = src.adapterId; g.header.id = src.id;
        if (DisplayConfigGetDeviceInfo(ref g) != 0) return false;
        int rec = Math.Abs(g.minScaleRel), want = Array.IndexOf(Steps, percent);
        if (want < 0 || want - rec < g.minScaleRel || want - rec > g.maxScaleRel) return false;
        var s = new DpiSet();
        s.header.type = -4; s.header.size = (uint)Marshal.SizeOf(typeof(DpiSet)); s.header.adapterId = src.adapterId; s.header.id = src.id;
        s.scaleRel = want - rec;
        return DisplayConfigSetDeviceInfo(ref s) == 0;
    }

    // Resolution
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    public static string Resolution() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); EnumDisplaySettings(null, -1, ref d); return d.dmPelsWidth + "x" + d.dmPelsHeight; }
    /// <summary>Switches to the largest mode the display offers; returns it.</summary>
    public static string Largest()
    {
        DEVMODE best = new DEVMODE(); long bestArea = 0;
        for (int i = 0; ; i++)
        {
            var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(null, i, ref d)) break;
            long a = (long)d.dmPelsWidth * d.dmPelsHeight;
            if (a > bestArea && d.dmBitsPerPel >= 24) { bestArea = a; best = d; }
        }
        if (bestArea > 0) { best.dmFields = 0x80000 | 0x100000; ChangeDisplaySettings(ref best, 0); }
        return Resolution();
    }
    public static void SetResolution(int w, int h)
    {
        var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); EnumDisplaySettings(null, -1, ref d);
        d.dmPelsWidth = w; d.dmPelsHeight = h; d.dmFields = 0x80000 | 0x100000; ChangeDisplaySettings(ref d, 0);
    }

    // The app's window
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

# The app's own window, rendered by Windows even when something else covers the screen (PW_RENDERFULLCONTENT), as in
# windows-runtime-check.ps1. Returns "WxH" and whether the window has rendered content (not one flat colour).
Add-Type -AssemblyName System.Drawing
function WindowShot([System.IntPtr] $hwnd, [string] $path) {
  $r = New-Object HcgDisplay+RECT
  if (-not [HcgDisplay]::GetWindowRect($hwnd, [ref]$r)) { return @{ size = '0x0'; content = $false } }
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  if ($w -le 0 -or $h -le 0) { return @{ size = '0x0'; content = $false } }
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  $ok = [HcgDisplay]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc); $g.Dispose()
  $colors = @{}; for ($x = 0; $x -lt $w; $x += [Math]::Max(1, [int]($w / 40))) { for ($y = 0; $y -lt $h; $y += [Math]::Max(1, [int]($h / 40))) { $colors[$bmp.GetPixel($x, $y).ToArgb()] = 1 } }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return @{ size = "${w}x${h}"; content = ($ok -and $colors.Count -ge 8) }
}

$results = @()
$originalRes = [HcgDisplay]::Resolution()
$original = [HcgDisplay]::Get()
$res = [HcgDisplay]::Largest()
Start-Sleep -Seconds 3
$available = [HcgDisplay]::Get()
Write-Host "Display: $originalRes → $res; scaling recommended $($available[0]) %, current $($available[1]) %, largest offered $($available[2]) %"

foreach ($scale in 100, 125, 150, 175, 200) {
  $entry = [ordered]@{ scale = $scale; available = $false; applied = $null; windowDpi = $null; windowScale = $null; window = $null; rendered = $false; closed = $false; screenshot = $null }
  if (-not [HcgDisplay]::Set($scale)) {
    Write-Host "  $scale %: not available on this display ($res)"
    $results += [pscustomobject]$entry; continue
  }
  Start-Sleep -Seconds 3
  $entry.available = $true
  $entry.applied = ([HcgDisplay]::Get())[1]
  $data = Join-Path $Out "data-$scale"; Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $data | Out-Null
  Set-Content -Path (Join-Path $data 'settings.json') -Value "{`"version`":1,`"theme`":`"$Theme`"}" -Encoding utf8
  $env:HCG_MAIL_DATA_DIR = $data
  $p = Start-Process -FilePath $Exe -PassThru
  $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline -and ($p.MainWindowHandle -eq 0)) { Start-Sleep -Milliseconds 500; $p.Refresh() }
  Start-Sleep -Seconds 6
  $p.Refresh()
  if ($p.MainWindowHandle -ne 0) {
    $entry.windowDpi = [int][HcgDisplay]::GetDpiForWindow($p.MainWindowHandle)
    $entry.windowScale = [math]::Round($entry.windowDpi / 96 * 100)
    $shot = Join-Path $Out "window-$scale.png"
    $cap = WindowShot $p.MainWindowHandle $shot
    $entry.window = $cap.size; $entry.rendered = $cap.content; $entry.screenshot = (Split-Path $shot -Leaf)
    $null = $p.CloseMainWindow()
    $entry.closed = $p.WaitForExit(20000)
  }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  Write-Host ("  {0} %: applied {1} %, HCG Mail window at {2} DPI ({3} %), {4}, rendered {5}, closed {6}" -f $scale, $entry.applied, $entry.windowDpi, $entry.windowScale, $entry.window, $entry.rendered, $entry.closed)
  $results += [pscustomobject]$entry
}
Remove-Item Env:\HCG_MAIL_DATA_DIR -ErrorAction SilentlyContinue
$null = [HcgDisplay]::Set($original[1])
$w, $h = $originalRes -split 'x'; [HcgDisplay]::SetResolution([int]$w, [int]$h)

$os = Get-CimInstance Win32_OperatingSystem
[ordered]@{
  windows = "$($os.Caption) build $($os.BuildNumber) $env:PROCESSOR_ARCHITECTURE"; resolution = $res
  recommendedScale = $available[0]; largestOfferedScale = $available[2]; theme = $Theme; scales = $results
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out 'display-scaling.json') -Encoding utf8

# Every offered scaling must be applied to HCG Mail's window, render and close; none offered beyond 100 % is a failure too.
$offered = @($results | Where-Object available)
$bad = @($offered | Where-Object { -not ($_.rendered -and $_.closed -and $_.windowScale -eq $_.scale) })
if ($offered.Count -lt 2) { Write-Host "Only $($offered.Count) scaling(s) offered by this display: not enough to test scaling"; exit 1 }
if ($bad.Count -gt 0) { Write-Host "FAILED at: $(($bad | ForEach-Object { "$($_.scale) %" }) -join ', ')"; exit 1 }
Write-Host "Display scaling: HCG Mail rendered and closed at $(($offered | ForEach-Object { "$($_.scale) %" }) -join ', ')"
# Explicit success: callers read $LASTEXITCODE, which a script without native commands leaves unset.
exit 0
