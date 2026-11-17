#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows-direct install for Chromium-based browsers (Chrome, Edge, Brave,
# Vivaldi, Opera). Sibling of install.ps1 (Firefox). Same exe, same host
# manifest file (which carries both `allowed_extensions` and
# `allowed_origins`), only the per-browser registry hive differs.

$ExeName   = "ankd.exe"
$InstDir   = "$env:LOCALAPPDATA\Programs\AnchorHub"
$AppData   = "$env:APPDATA\AnchorHub"
$HostName  = "io.github.contextswitcher.anchorhub"
$ManifestDst = "$AppData\$HostName.json"

# Chrome and its derivatives all read native messaging hosts from their
# own `Software\<Vendor>\<Browser>\NativeMessagingHosts\` hive. Register
# under each so loading the unpacked extension in any of them works
# without re-running the installer. (Empty hives are harmless if the
# browser isn't installed.)
$ChromiumRegPaths = @(
  "HKCU:\SOFTWARE\Google\Chrome\NativeMessagingHosts\$HostName",
  "HKCU:\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\$HostName",
  "HKCU:\SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName",
  "HKCU:\SOFTWARE\Vivaldi\NativeMessagingHosts\$HostName",
  "HKCU:\SOFTWARE\Chromium\NativeMessagingHosts\$HostName"
)

# 0. Ensure build prerequisites: a .NET SDK (>= 8) and a nuget.org package
#    source, winget-installing the SDK if it is missing.
. (Join-Path $PSScriptRoot 'ensure-prereqs.ps1')

# 1. Publish hub as self-contained single-file exe.
Write-Host "Publishing hub..."
$hubProj = Join-Path $PSScriptRoot "..\hub\AnchorHub.Hub\AnchorHub.Hub.csproj"
$publishOut  = Join-Path $PSScriptRoot "bin"
dotnet publish $hubProj -c Release -r win-x64 --self-contained true `
    -f net8.0-windows `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2. Copy hub exe.
Write-Host "Installing hub to $InstDir..."
New-Item -ItemType Directory -Force -Path $InstDir | Out-Null
$src = Join-Path $PSScriptRoot "bin\$ExeName"
Copy-Item $src "$InstDir\$ExeName" -Force

# 3. Write native messaging manifest. Same template as the Firefox
#    install — allowed_extensions and allowed_origins coexist; each
#    browser reads only the field it understands.
Write-Host "Writing native messaging manifest..."
New-Item -ItemType Directory -Force -Path $AppData | Out-Null
$template = Get-Content "$PSScriptRoot\io.github.contextswitcher.anchorhub.json.template" -Raw
$manifest  = $template -replace 'HOST_EXE_PATH', "$InstDir\$ExeName".Replace('\','\\')
# PS 5.1's `-Encoding UTF8` writes a BOM that some Chromium builds also
# reject in native-messaging manifests. Use the .NET API to write
# BOM-less UTF-8.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($ManifestDst, $manifest, $utf8NoBom)

# 4. Register under every Chromium-family browser's HKCU hive.
Write-Host "Registering under Chromium-family browsers..."
foreach ($regPath in $ChromiumRegPaths) {
  New-Item -Path $regPath -Force | Out-Null
  Set-ItemProperty -Path $regPath -Name '(Default)' -Value $ManifestDst
  Write-Host "  $regPath"
}

# 5. Stage extension as a load-unpacked directory. Chrome can load the
#    repo's extension/ folder directly, but pointing users at the working
#    tree means an accidental edit can break the browser-loaded copy. A
#    staged copy under LOCALAPPDATA matches the daemon's install location
#    and is what users will keep loaded long-term.
$ExtStageDir = "$InstDir\extension"
Write-Host "Staging unpacked extension at $ExtStageDir..."
$ExtSrc = Resolve-Path (Join-Path $PSScriptRoot "..\extension")
if (Test-Path $ExtStageDir) { Remove-Item $ExtStageDir -Recurse -Force }
Copy-Item $ExtSrc $ExtStageDir -Recurse

# 6. Inject the Chrome `key` field into the staged manifest. The field is
#    Chrome-specific (it derives the stable extension ID
#    `bhnaeoglhfolnnkciokfdinoghhjlfeh` that the host manifest's
#    `allowed_origins` references) and Firefox emits a manifest warning
#    when it sees it. Keeping it out of the source tree's
#    extension/manifest.json keeps the Firefox "Load Temporary Add-on"
#    workflow clean; the field is added here so Chrome's staged copy
#    has it.
$ChromeKey = 'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA9GL6po09OBwjqyGBNxXYVu1Msy8aQ/g3y+G+NVCS+mPxQGv8PW8To5Zma97htKEgl7Nkbak+PlIVV322rxow80jzjbq5nT/fTA3RDpejnkVGQZmpXEt4wwpLPekLmRgsg432e87c7P83GEkPawMdx1RdyUBibEG8NiP5RCne19f0XwvlKCznWICqjnRJr2ZxlVcaTWpmwXP90C+DOjod74wmQGSSgwHIQiQQzger2VeltAOoDjotsdT8bwRnppt+Mzq5TENqAUcDDDk8zwuHwrk0Ukf2hh7VGzJcpFWaLZupU9/kuDqqL/3gTy6BojW3IiW0CP0vvSLl9t+SelynTQIDAQAB'
$ExtManifest = Join-Path $ExtStageDir 'manifest.json'
$mf = Get-Content $ExtManifest -Raw | ConvertFrom-Json
$mf | Add-Member -NotePropertyName 'key' -NotePropertyValue $ChromeKey -Force
$mfJson = $mf | ConvertTo-Json -Depth 32
[System.IO.File]::WriteAllText($ExtManifest, $mfJson, $utf8NoBom)

Write-Host ""
Write-Host 'Done. To load the extension in Chrome / Edge / Brave / Vivaldi:'
Write-Host '  1. Open chrome://extensions (or edge://extensions, etc.)'
Write-Host '  2. Enable "Developer mode"'
Write-Host '  3. Click "Load unpacked" and select:'
Write-Host "       $ExtStageDir"
Write-Host ''
Write-Host 'The extension ID is fixed (key field in manifest.json):'
Write-Host '  bhnaeoglhfolnnkciokfdinoghhjlfeh'
