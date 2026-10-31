# Builds the self-contained Windows x64 test build of HCG Mail on Windows (PowerShell 5+ / 7).
# Output: artifacts\publish\win-x64\HCGMail.exe and artifacts\HCGMail-<version>-win-x64.zip
# The build is not code-signed (SmartScreen will warn). Signing requires founder approval.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
[xml]$props = Get-Content 'Directory.Build.props'
$version = $props.Project.PropertyGroup.InformationalVersion
$out = 'artifacts\publish\win-x64'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src\HCG.Mail.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
$zip = "artifacts\HCGMail-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\HCGMail.exe" -DestinationPath $zip
Get-FileHash $zip, "$out\HCGMail.exe" -Algorithm SHA256 | Format-Table -AutoSize
Write-Host "Windows build ready: $zip"
