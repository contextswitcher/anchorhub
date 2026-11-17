# install/ensure-prereqs.ps1
#
# Shared build-prerequisite bootstrap for the Windows-side AnchorHub
# installers (install.ps1, install-chrome.ps1, install-vm.ps1).
#
# DOT-SOURCE it -- do not run with '&':
#
#     . (Join-Path $PSScriptRoot 'ensure-prereqs.ps1')
#
# Dot-sourcing runs it in the CALLER's scope, so when we winget-install the
# SDK and refresh $env:PATH, the caller's subsequent 'dotnet publish' finds
# the newly installed dotnet without the user reopening the terminal.
#
# It ensures, idempotently:
#   1. A .NET SDK (major >= 8) is on PATH -- winget-installs SDK 8 if absent.
#   2. A nuget.org package source is registered -- a fresh SDK has none, so
#      'dotnet restore' otherwise fails with NU1100 (no source to resolve
#      the runtime packs / NuGet packages from).
#
# Keep all code ASCII: Windows PowerShell 5.1 reads BOM-less .ps1 files as
# the system ANSI codepage, and a stray non-ASCII char in a string breaks
# the parser. (Comments are fine.)

Set-StrictMode -Version Latest

function Update-SessionPath {
    # Rebuild $env:PATH from the persisted Machine + User values so a tool
    # installed moments ago becomes visible in THIS session.
    $machine = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user    = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    $env:PATH = (@($machine, $user) | Where-Object { $_ }) -join ';'
}

function Test-DotnetSdk {
    # True if a .NET SDK with major version >= 8 is available on PATH.
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    foreach ($line in (& dotnet --list-sdks 2>$null)) {
        if ($line -match '^\s*(\d+)\.' -and [int]$Matches[1] -ge 8) { return $true }
    }
    return $false
}

# --- 1. .NET SDK (>= 8) ------------------------------------------------------

if (Test-DotnetSdk) {
    Write-Host "[prereq] .NET SDK (>= 8) present."
} else {
    Write-Host "[prereq] .NET SDK 8 not found; installing via winget..."
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Neither a .NET SDK (>= 8) nor winget is available. Install the SDK from https://dotnet.microsoft.com/download/dotnet/8.0 and re-run."
    }
    winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --silent `
        --accept-source-agreements --accept-package-agreements
    # Do NOT trust winget's exit code: it returns non-zero for benign cases
    # ('already installed', 'reboot suggested'). Re-detect the SDK instead.
    Update-SessionPath
    if (-not (Test-DotnetSdk)) {
        throw "winget ran but no .NET SDK (>= 8) is detectable yet. Open a NEW terminal and re-run the installer, or install manually from https://dotnet.microsoft.com/download/dotnet/8.0."
    }
    Write-Host "[prereq] .NET SDK installed."
}

# --- 2. nuget.org package source --------------------------------------------

if ((& dotnet nuget list source 2>$null) -match 'api\.nuget\.org') {
    Write-Host "[prereq] nuget.org package source already configured."
} else {
    Write-Host "[prereq] Adding nuget.org package source..."
    & dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to add the nuget.org source. Add it manually: dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org"
    }
}
