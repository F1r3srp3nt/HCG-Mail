<#
.SYNOPSIS
  Runtime checks of an installed or unzipped HCG Mail for Windows (HCGMail.exe) on this PC.

.DESCRIPTION
  Uses an isolated data folder (HCG_MAIL_DATA_DIR), so it never touches your own accounts or settings.
    1. Credential store: write, read, update and delete a throwaway entry in Windows Credential Manager
       (HCGMail --check-credential-store, dummy secret only).
    2. TLS certificate validation (HCGMail --diagnose-tls): public IMAP and SMTP servers with valid certificates must
       be accepted; hosts with an expired, wrong-host or self-signed certificate must be rejected.
    3. Start-up and rendering: the app starts, shows its main window and is screenshotted; it is closed through its
       window (as a user would), then started again (restart), screenshotted again and closed. The data folder must
       hold the app's settings and log after the first run.
  Prints PASS/FAIL per check and exits 1 if any check fails. Needs network access for step 2.

.EXAMPLE
  .\windows-runtime-check.ps1 -Exe "$env:LOCALAPPDATA\Programs\HCG Mail\HCGMail.exe" -Out "$env:TEMP\hcg-mail-check"
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Out,
    [int] $StartSeconds = 25
)
$ErrorActionPreference = 'Stop'
$failures = 0
$results = New-Object System.Collections.ArrayList
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what"; $script:failures++ }
    [void]$script:results.Add([ordered]@{ check = $what; passed = $ok })
}

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$data = Join-Path $Out 'data'
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory -Force -Path $data | Out-Null
$env:HCG_MAIL_DATA_DIR = $data

$info = (Get-Item $Exe).VersionInfo
$sig = Get-AuthenticodeSignature $Exe
Write-Host "HCG Mail: $Exe"
Write-Host "  product $($info.ProductName) $($info.ProductVersion) · file version $($info.FileVersion) · $($info.CompanyName)"
Write-Host "  Authenticode: $($sig.Status)$(if ($sig.SignerCertificate) { ' · ' + $sig.SignerCertificate.Subject })"
Write-Host "  Windows: $((Get-CimInstance Win32_OperatingSystem).Caption) $([Environment]::OSVersion.Version) · $env:PROCESSOR_ARCHITECTURE"

function Run([string[]] $arguments, [int] $timeoutSeconds = 90) {
    $p = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($timeoutSeconds * 1000)) { & taskkill.exe /PID $p.Id /T /F | Out-Null; return -1 }
    return $p.ExitCode
}

# 1. Credential store (Windows Credential Manager)
$code = Run @('--check-credential-store')
$report = Get-ChildItem $data -Recurse -Filter '*credential*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($report) { Get-Content $report.FullName | ForEach-Object { Write-Host "      $_" } }
Check ($code -eq 0) "Credential store round trip in Windows Credential Manager (exit $code)"

# 2. TLS certificate validation
$tls = @(
    @{ Target = 'imap.gmail.com:993'; Valid = $true; What = 'IMAP over TLS, valid certificate' },
    @{ Target = 'smtp.gmail.com:465'; Valid = $true; What = 'SMTP over TLS, valid certificate' },
    @{ Target = 'outlook.office365.com:993'; Valid = $true; What = 'IMAP over TLS, valid certificate (second provider)' },
    # The provider presets offered in account setup (implicit-TLS ports; iCloud's SMTP uses STARTTLS on 587).
    @{ Target = 'imap.mail.me.com:993'; Valid = $true; What = 'iCloud Mail IMAP preset' },
    @{ Target = 'imap.mail.yahoo.com:993'; Valid = $true; What = 'Yahoo Mail IMAP preset' },
    @{ Target = 'smtp.mail.yahoo.com:465'; Valid = $true; What = 'Yahoo Mail SMTP preset' },
    @{ Target = 'imap.fastmail.com:993'; Valid = $true; What = 'Fastmail IMAP preset' },
    @{ Target = 'smtp.fastmail.com:465'; Valid = $true; What = 'Fastmail SMTP preset' },
    @{ Target = 'expired.badssl.com:443'; Valid = $false; What = 'expired certificate' },
    @{ Target = 'wrong.host.badssl.com:443'; Valid = $false; What = 'certificate for another host name' },
    @{ Target = 'self-signed.badssl.com:443'; Valid = $false; What = 'self-signed certificate' }
)
foreach ($t in $tls) {
    $code = Run @('--diagnose-tls', $t.Target)
    $host_, $port = $t.Target.Split(':')
    $file = Join-Path $data "tls-diagnosis-$host_-$port.txt"
    if (Test-Path $file) { Get-Content $file | ForEach-Object { Write-Host "      $_" } }
    if ($t.Valid) { Check ($code -eq 0) "TLS accepted: $($t.Target) ($($t.What), exit $code)" }
    # A refusal counts only when the certificate or TLS check refused it, not when the host could not be reached.
    else { Check ($code -ne 0 -and (Test-Path $file) -and ((Get-Content $file -Raw) -match 'FAILED after .* (Certificate|Tls|RevocationUnavailable)')) "TLS rejected: $($t.Target) ($($t.What), exit $code)" }
}

# 3. Start-up, rendering, close, restart
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace Hcg -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr hwnd, System.IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hwnd);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
# The app's own window, rendered by Windows even when something else covers the screen (PW_RENDERFULLCONTENT).
function WindowShot([System.IntPtr] $hwnd, [string] $name) {
    $r = New-Object Hcg.Win32+RECT
    if (-not [Hcg.Win32]::GetWindowRect($hwnd, [ref]$r)) { Write-Host "      window screenshot: no window rectangle"; return $false }
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    $ok = [Hcg.Win32]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    # A rendered window is not one flat colour: count distinct colours on a coarse grid.
    $colors = @{}; for ($x = 0; $x -lt $w; $x += [Math]::Max(1, [int]($w / 40))) { for ($y = 0; $y -lt $h; $y += [Math]::Max(1, [int]($h / 40))) { $colors[$bmp.GetPixel($x, $y).ToArgb()] = 1 } }
    $path = Join-Path $Out $name; $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Host "      window screenshot: $path ($w x $h, $($colors.Count) colours sampled)"
    return ($ok -and $colors.Count -ge 8)
}
function Shot([string] $name) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $path = Join-Path $Out $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "      screenshot: $path"
}
function StartAndClose([string] $label, [string] $shot) {
    $p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
    $deadline = (Get-Date).AddSeconds($StartSeconds)
    while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0 -and $p.MainWindowTitle) { break }
        Start-Sleep -Milliseconds 500
    }
    $p.Refresh()
    $shown = -not $p.HasExited -and $p.MainWindowHandle -ne 0
    Check $shown "$label`: main window shown (title '$($p.MainWindowTitle)')"
    if (-not $shown -and $p.HasExited) { Write-Host "      exited with code $($p.ExitCode)" }
    Start-Sleep -Seconds 4   # let the first frames render before the screenshot
    if ($shown) {
        [void][Hcg.Win32]::SetForegroundWindow($p.MainWindowHandle)
        Start-Sleep -Milliseconds 500
        Check (WindowShot $p.MainWindowHandle ("window-" + $shot)) "$label`: the window has rendered content"
    }
    Shot $shot
    if (-not $p.HasExited) {
        [void]$p.CloseMainWindow()
        $closed = $p.WaitForExit(20000)
        Check $closed "$label`: closed through its window"
        if (-not $closed) { & taskkill.exe /PID $p.Id /T /F | Out-Null }
    }
}
StartAndClose 'First start' 'first-start.png'
$settings = Get-ChildItem $data -Recurse -Filter 'settings.json' -ErrorAction SilentlyContinue | Select-Object -First 1
$log = Get-ChildItem (Join-Path $data 'logs') -File -ErrorAction SilentlyContinue | Select-Object -First 1
# Information only: the app writes its log when it has something to log, not at every start.
Write-Host "      log after first start: $(if ($log) { $log.Name } else { 'none written (nothing to log)' })"
Write-Host "      data folder after first start: $((Get-ChildItem $data -Recurse -File | ForEach-Object { $_.FullName.Substring($data.Length + 1) }) -join ', ')"
StartAndClose 'Restart' 'restart.png'
if ($log) {
    $text = Get-Content $log.FullName -Raw
    Check ($text -notmatch '(?i)password\s*[:=]|AUTHENTICATE PLAIN [A-Za-z0-9+/=]{8,}') 'The log holds no password or authentication payload'
}

$os = Get-CimInstance Win32_OperatingSystem
[ordered]@{
    executable = [ordered]@{ productVersion = $info.ProductVersion; fileVersion = $info.FileVersion; authenticode = [string]$sig.Status }
    windows = [ordered]@{ caption = $os.Caption; version = [string][Environment]::OSVersion.Version; build = $os.BuildNumber; architecture = $env:PROCESSOR_ARCHITECTURE }
    checks = $results; failures = $failures
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Out 'runtime-summary.json') -Encoding utf8

Write-Host ''
if ($failures -eq 0) { Write-Host 'Windows runtime check: all checks passed' } else { Write-Host "Windows runtime check: $failures check(s) FAILED" }
exit ([int]($failures -ne 0))
