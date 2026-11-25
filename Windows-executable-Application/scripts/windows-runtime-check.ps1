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
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what"; $script:failures++ }
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
    else { Check ($code -ne 0 -and (Test-Path $file)) "TLS rejected: $($t.Target) ($($t.What), exit $code)" }
}

# 3. Start-up, rendering, close, restart
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
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
Check ($null -ne $log) "The app wrote its log in the data folder ($(if ($log) { $log.Name } else { 'none' }))"
Write-Host "      data folder after first start: $((Get-ChildItem $data -Recurse -File | ForEach-Object { $_.FullName.Substring($data.Length + 1) }) -join ', ')"
StartAndClose 'Restart' 'restart.png'
if ($log) {
    $text = Get-Content $log.FullName -Raw
    Check ($text -notmatch '(?i)password\s*[:=]|AUTHENTICATE PLAIN [A-Za-z0-9+/=]{8,}') 'The log holds no password or authentication payload'
}

Write-Host ''
if ($failures -eq 0) { Write-Host 'Windows runtime check: all checks passed' } else { Write-Host "Windows runtime check: $failures check(s) FAILED" }
exit ([int]($failures -ne 0))
