#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExeName   = "ankd.exe"
$InstDir   = "$env:LOCALAPPDATA\Programs\AnchorHub"
$AppData   = "$env:APPDATA\AnchorHub"
$HostName  = "io.github.contextswitcher.anchorhub"
$RegPath   = "HKCU:\SOFTWARE\Mozilla\NativeMessagingHosts\$HostName"
$ManifestDst = "$AppData\$HostName.json"

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

# 3. Copy hub exe.
Write-Host "Installing hub to $InstDir..."
New-Item -ItemType Directory -Force -Path $InstDir | Out-Null
$src = Join-Path $PSScriptRoot "bin\$ExeName"
Copy-Item $src "$InstDir\$ExeName" -Force

# 4. Write native messaging manifest. Firefox-flavoured: only
# allowed_extensions, no Chrome-style allowed_origins. Recent Firefox
# builds have been observed silently refusing to spawn the host when
# the manifest carries both fields, even though the documentation says
# unknown fields are ignored.
Write-Host "Writing native messaging manifest..."
New-Item -ItemType Directory -Force -Path $AppData | Out-Null
$ExePath = "$InstDir\$ExeName".Replace('\', '\\')
$manifest = @"
{
  "name": "io.github.contextswitcher.anchorhub",
  "description": "AnchorHub host",
  "path": "$ExePath",
  "type": "stdio",
  "allowed_extensions": ["anchorhub@contextswitcher.github.io"]
}
"@
# PowerShell 5.1's `-Encoding UTF8` writes a BOM that Firefox rejects.
# Use the .NET API with a BOM-less UTF8Encoding instead.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($ManifestDst, $manifest, $utf8NoBom)

# 5. Register in HKCU (no admin required).
Write-Host "Registering in HKCU..."
New-Item -Path $RegPath -Force | Out-Null
Set-ItemProperty -Path $RegPath -Name '(Default)' -Value $ManifestDst

# 6. Generate the JabRef fulltext-fetch bearer token if it doesn't
# already exist. Kept for when the provider is re-enabled; ankd
# currently ignores it (JABREF_FULLTEXT_AUTOENABLE is not defined).
# When enabled, the presence of this file is what activates the
# fulltext realm in ankd (Program.cs auto-enables --http-listen=
# 127.0.0.1:17893 when it sees the file). Native-messaging manifests
# cannot pass CLI args, so this convention-over-config approach is the
# simplest path. Keep the existing token across reinstalls so JabRef
# does not need to re-read it.
$tokenPath = "$AppData\fulltext.token"
if (-not (Test-Path $tokenPath)) {
    Write-Host "Generating JabRef fulltext token..."
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = [Convert]::ToBase64String($bytes)
    [System.IO.File]::WriteAllText($tokenPath, $token, $utf8NoBom)
    try {
        $acl = Get-Acl $tokenPath
        $acl.SetAccessRuleProtection($true, $false)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
            "FullControl", "Allow")
        $acl.SetAccessRule($rule)
        Set-Acl -Path $tokenPath -AclObject $acl
    } catch {
        Write-Warning "Could not tighten ACL on $tokenPath ($_); continuing."
    }
} else {
    Write-Host "JabRef fulltext token already present."
}

Write-Host ""
Write-Host 'Done. No configuration needed - AnchorHub uses the OneNote COM API directly.'
Write-Host 'JabRef fulltext provider is currently disabled (see JABREF_FULLTEXT_AUTOENABLE).'
Write-Host 'Load the extension in Firefox via about:debugging > Load Temporary Add-on.'
