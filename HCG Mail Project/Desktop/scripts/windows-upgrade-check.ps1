<#
.SYNOPSIS
  Upgrade from an installed earlier HCG Mail to a new version, as a user would, on this Windows machine.
.DESCRIPTION
  1. Installs the earlier version silently for the current user (no administrator rights), like the published download.
  2. Gives it user data exactly as that version writes it (tests/fixtures/beta1-user-data: one sanitized IMAP/SMTP account
     and the HCG Classic theme) and a saved password in Windows Credential Manager under the name HCG Mail uses. Starts
     it once and captures its window.
  3. Installs the new version over it silently.
  4. Checks: one registration, now with the new version; the installed program is the new version; the user data and
     the saved password are unchanged by the installer; the new version starts with the account and closes; afterwards
     the account is still there. Captures the window.
  5. Uninstalls and reports whether user data is kept (it must be: uninstalling removes the program, not the mailboxes'
     settings), then removes the test data and the test password.
  The account points at mail.example.com: nothing is sent anywhere. Writes upgrade.json, before-upgrade.png, after-upgrade.png.
  Usage: windows-upgrade-check.ps1 -OldSetup <Setup.exe> -OldVersion 1.0.0-beta.1 -NewSetup <Setup.exe> -NewVersion 1.0.0-beta.2
                                   -Fixture <tests/fixtures/beta1-user-data> -Out <folder>
#>
param(
  [Parameter(Mandatory)] [string] $OldSetup, [Parameter(Mandatory)] [string] $OldVersion,
  [Parameter(Mandatory)] [string] $NewSetup, [Parameter(Mandatory)] [string] $NewVersion,
  [Parameter(Mandatory)] [string] $Fixture, [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([bool] $ok, [string] $what) {
  $checks.Add([pscustomobject]@{ check = $what; passed = $ok })
  Write-Host ("{0}  {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what)
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class HcgUpgrade
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL { public uint Flags, Type; public string TargetName, Comment; public long LastWritten; public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount; public IntPtr Attributes; public string TargetAlias, UserName; }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL c, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, uint type, uint flags, out IntPtr c);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr p);
    // As HCG Mail writes it: generic credential, UTF-8 blob, persisted for the local machine.
    public static void Write(string target, string user, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret); var blob = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, blob, bytes.Length);
              var c = new CREDENTIAL { Type = 1, TargetName = target, UserName = user, CredentialBlob = blob, CredentialBlobSize = (uint)bytes.Length, Persist = 2, Comment = "HCG Mail upgrade test" };
              if (!CredWrite(ref c, 0)) throw new Exception("CredWrite " + Marshal.GetLastWin32Error()); }
        finally { Marshal.FreeHGlobal(blob); }
    }
    public static string Read(string target)
    {
        IntPtr p; if (!CredRead(target, 1, 0, out p)) return null;
        try { var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL)); var b = new byte[c.CredentialBlobSize]; Marshal.Copy(c.CredentialBlob, b, 0, b.Length); return Encoding.UTF8.GetString(b); }
        finally { CredFree(p); }
    }
    public static void Delete(string target) { CredDelete(target, 1, 0); }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

# The app's own window, rendered by Windows even when something else covers the screen (PW_RENDERFULLCONTENT), as in
# windows-runtime-check.ps1. Returns "WxH" and whether the window has rendered content (not one flat colour).
Add-Type -AssemblyName System.Drawing
function WindowShot([System.IntPtr] $hwnd, [string] $path) {
  $r = New-Object HcgUpgrade+RECT
  if (-not [HcgUpgrade]::GetWindowRect($hwnd, [ref]$r)) { return @{ size = '0x0'; content = $false } }
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  if ($w -le 0 -or $h -le 0) { return @{ size = '0x0'; content = $false } }
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  $ok = [HcgUpgrade]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc); $g.Dispose()
  $colors = @{}; for ($x = 0; $x -lt $w; $x += [Math]::Max(1, [int]($w / 40))) { for ($y = 0; $y -lt $h; $y += [Math]::Max(1, [int]($h / 40))) { $colors[$bmp.GetPixel($x, $y).ToArgb()] = 1 } }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return @{ size = "${w}x${h}"; content = ($ok -and $colors.Count -ge 8) }
}

function Registrations { @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
  Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -like 'HCG Mail*' } | ForEach-Object { Get-ItemProperty $_.PSPath }) }
function Install([string] $setup, [string] $log) {
  $p = Start-Process -FilePath $setup -PassThru -Wait -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$log`"")
  return $p.ExitCode
}
function Hash([string] $path) { if (Test-Path $path) { (Get-FileHash $path -Algorithm SHA256).Hash } else { $null } }
function RunApp([string] $exe, [string] $shot) {
  $p = Start-Process -FilePath $exe -PassThru
  $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline -and ($p.MainWindowHandle -eq 0)) { Start-Sleep -Milliseconds 500; $p.Refresh() }
  Start-Sleep -Seconds 8; $p.Refresh()
  $shown = $p.MainWindowHandle -ne 0
  $rendered = $shown -and (WindowShot $p.MainWindowHandle (Join-Path $Out $shot)).content
  if ($shown) { $null = $p.CloseMainWindow() }
  $closed = $p.WaitForExit(20000)
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  return @{ shown = $shown; rendered = $rendered; closed = $closed }
}

$data = Join-Path $env:LOCALAPPDATA 'HCG\HCG Mail'
$target = 'HCG Mail/account:beta1-upgrade-test'
Remove-Item Env:\HCG_MAIL_DATA_DIR -ErrorAction SilentlyContinue   # the real per-user location, as for a user
Check (@(Registrations).Count -eq 0) 'No HCG Mail installed before the test'

# 1–2. The earlier version, with its user data and a saved password
Check ((Install $OldSetup (Join-Path $Out 'install-old.log')) -eq 0) "Installed $OldVersion silently for the current user"
$reg = Registrations
Check ($reg.Count -eq 1 -and $reg[0].DisplayVersion -eq $OldVersion) "Registered as $OldVersion"
$exe = Join-Path $reg[0].InstallLocation 'HCGMail.exe'
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item (Join-Path $Fixture 'accounts.json'), (Join-Path $Fixture 'settings.json') $data -Force
[HcgUpgrade]::Write($target, 'account:beta1-upgrade-test', 'upgrade-test-password')
$old = RunApp $exe 'before-upgrade.png'
Check ($old.shown -and $old.rendered -and $old.closed) "$OldVersion starts with the account and closes"
$accountsBefore = Hash (Join-Path $data 'accounts.json'); $settingsBefore = Hash (Join-Path $data 'settings.json')

# 3. The new version over it
Check ((Install $NewSetup (Join-Path $Out 'install-new.log')) -eq 0) "Installed $NewVersion over $OldVersion silently"
$reg = Registrations
Check ($reg.Count -eq 1) "One registration after the upgrade (found $($reg.Count))"
Check ($reg[0].DisplayVersion -eq $NewVersion) "Registered version is now $NewVersion (was $($reg[0].DisplayVersion))"
$exe = Join-Path $reg[0].InstallLocation 'HCGMail.exe'
$pv = (Get-Item $exe).VersionInfo.ProductVersion
Check ($pv -like "$NewVersion*") "Installed program is $NewVersion (product version $pv)"
Check ((Hash (Join-Path $data 'accounts.json')) -eq $accountsBefore) 'The installer left the accounts unchanged'
Check ((Hash (Join-Path $data 'settings.json')) -eq $settingsBefore) 'The installer left the settings unchanged'
Check ([HcgUpgrade]::Read($target) -eq 'upgrade-test-password') 'The saved password is still in Windows Credential Manager'

# 4. The new version with that data
$new = RunApp $exe 'after-upgrade.png'
Check ($new.shown -and $new.rendered -and $new.closed) "$NewVersion starts with the earlier data and closes"
$accounts = Get-Content (Join-Path $data 'accounts.json') -Raw | ConvertFrom-Json
Check (@($accounts.accounts | Where-Object { $_.id -eq 'beta1-upgrade-test' -and $_.emailAddress -eq 'test@example.com' }).Count -eq 1) 'The account is still there after the new version ran'
Check ([HcgUpgrade]::Read($target) -eq 'upgrade-test-password') 'The saved password is still there after the new version ran'

# 5. Uninstall keeps user data; then clean up the test data
$unins = Join-Path $reg[0].InstallLocation 'unins000.exe'
$u = Start-Process -FilePath $unins -PassThru -Wait -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
Check ($u.ExitCode -eq 0 -and @(Registrations).Count -eq 0) 'Uninstalled'
Check (Test-Path (Join-Path $data 'accounts.json')) 'Uninstalling keeps the user data (accounts and settings)'
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
[HcgUpgrade]::Delete($target)

$os = Get-CimInstance Win32_OperatingSystem
[ordered]@{ windows = "$($os.Caption) build $($os.BuildNumber) $env:PROCESSOR_ARCHITECTURE"; from = $OldVersion; to = $NewVersion
            checks = $checks; failures = @($checks | Where-Object { -not $_.passed }).Count } |
  ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out 'upgrade.json') -Encoding utf8
$failed = @($checks | Where-Object { -not $_.passed }).Count
if ($failed -gt 0) { Write-Host "Upgrade check: $failed check(s) FAILED"; exit 1 }
Write-Host "Upgrade check: all checks passed ($OldVersion → $NewVersion)"
exit 0
