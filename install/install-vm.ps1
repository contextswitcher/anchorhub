#Requires -Version 5.1
<#
.SYNOPSIS
    Installs ankd.exe inside a Windows VM in --listen mode.

.DESCRIPTION
    The VM-side companion to install-linux.sh. Pairs with the Linux
    forwarder via a shared token. See docs/plan-linux-port.md and
    docs/decisions/0006-linux-port-via-vm-relay.md.

    Run inside the Windows guest (NOT inside a WinApps RemoteApp
    session -- the listener should live in the user session
    independently of any RemoteApp window's lifetime).

.PARAMETER Token
    The hex token printed by install-linux.sh. If omitted, the script
    prompts.

.PARAMETER Listen
    host:port to listen on inside the guest. Default 0.0.0.0:9876 -- bind
    ALL interfaces, not loopback: QEMU user-mode (slirp) hostfwd delivers
    forwarded connections to the guest's NAT interface (e.g. 10.0.2.15),
    so a 127.0.0.1 binding is unreachable from the Linux host. The guest
    is NAT-isolated and reachable only through the host's hostfwd.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install\install-vm.ps1 -Token abcd1234...

.NOTES
    [impl->req~ank.transport.modes~1]      listen-mode wiring
    [impl->req~ank.transport.framing~1]    inherits from MessageProtocol
    [impl->req~ank.transport.auth~1]       token persisted to file
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$false, Position=0)]
    [string]$Token,

    [string]$Listen = "0.0.0.0:9876"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Paths ─────────────────────────────────────────────────────────────────────

$ExeName       = "ankd.exe"
$InstDir       = "$env:LOCALAPPDATA\Programs\AnchorHub"
$AppData       = "$env:APPDATA\AnchorHub"
$TokenFile     = "$AppData\token"
$RunKey        = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$RunValueName  = "AnchorHubListener"

# ── Prereqs ───────────────────────────────────────────────────────────────────

# Ensure a .NET SDK (>= 8) and a nuget.org package source. Dot-sourced
# (not '&') so a winget SDK install can refresh PATH in this script's scope.
. (Join-Path $PSScriptRoot 'ensure-prereqs.ps1')

# ── 1. Token ──────────────────────────────────────────────────────────────────

if ([string]::IsNullOrWhiteSpace($Token)) {
    $Token = Read-Host "Paste the token from install-linux.sh"
}
$Token = $Token.Trim()
if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "Token is required (run install-linux.sh on the host first; it prints the token)."
}

# ── 2. Publish hub (same project, win-x64 + net8.0-windows TFM) ───────────────

Write-Host "Publishing ankd.exe..."
$hubProj    = Join-Path $PSScriptRoot "..\hub\AnchorHub.Hub\AnchorHub.Hub.csproj"
$publishOut = Join-Path $PSScriptRoot "bin"
if (Test-Path $publishOut) { Remove-Item -Recurse -Force $publishOut }
dotnet publish $hubProj -c Release -r win-x64 --self-contained true `
    -f net8.0-windows `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ── 3. Copy binary into the install dir ───────────────────────────────────────

# Stop any running listener BEFORE copying: a live ankd.exe holds a file
# lock on the target, so overwriting it fails with "being used by another
# process". (The autostart Run-key or a previous run may have launched it.)
# @(...) forces an array so .Count is valid even for a single match --
# Get-Process returns a bare object for one process, and Set-StrictMode
# turns .Count on a scalar into a terminating PropertyNotFoundStrict error.
$running = @(Get-Process -Name ankd -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "Stopping previous ankd processes ($($running.Count))..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

Write-Host "Installing $ExeName to $InstDir..."
New-Item -ItemType Directory -Force -Path $InstDir | Out-Null
# Retry: the file lock can linger briefly after the kill.
$copied = $false
foreach ($attempt in 1..5) {
    try {
        Copy-Item (Join-Path $publishOut $ExeName) "$InstDir\$ExeName" -Force
        $copied = $true; break
    } catch { Start-Sleep -Milliseconds 300 }
}
if (-not $copied) {
    throw "Could not copy $ExeName into $InstDir (still locked). Close any running ankd and retry."
}

# ── 4. Write token (default per-user ACL on %APPDATA% is already
#       user-only, so no extra ACL hardening needed) ───────────────────────────

Write-Host "Writing token to $TokenFile..."
New-Item -ItemType Directory -Force -Path $AppData | Out-Null
[System.IO.File]::WriteAllText($TokenFile, $Token, [System.Text.UTF8Encoding]::new($false))

# ── 5. Stop any previously running listener so the new binary takes effect ────

# (Moved up to step 3: the listener is now stopped BEFORE the binary copy,
#  because a running ankd.exe locks the file being overwritten.)

# ── 6. Run key (autostart at user login) ──────────────────────────────────────

Write-Host "Registering autostart in $RunKey\$RunValueName..."
$runCmd = "`"$InstDir\$ExeName`" --listen=$Listen --token-file=`"$TokenFile`""
Set-ItemProperty -Path $RunKey -Name $RunValueName -Value $runCmd

# ── 6b. Windows Firewall: allow inbound TCP on the listener port ──────────────
#   slirp hostfwd delivers to the guest's NAT interface, so the inbound SYN
#   hits a non-loopback interface and Windows Firewall silently drops it by
#   default (the symptom is a *hang*, not a refusal). Needs elevation;
#   best-effort with a manual fallback message if not admin.

$listenPort = ($Listen -split ':')[-1]
$fwRuleName = "AnchorHub listener ($listenPort)"
try {
    if (Get-NetFirewallRule -DisplayName $fwRuleName -ErrorAction SilentlyContinue) {
        Write-Host "Firewall rule '$fwRuleName' already present."
    } else {
        New-NetFirewallRule -DisplayName $fwRuleName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $listenPort -Profile Any | Out-Null
        Write-Host "Added Windows Firewall inbound rule '$fwRuleName'."
    }
} catch {
    Write-Warning "Could not add the firewall rule (run elevated?): $_"
    Write-Warning "Add it once from an Administrator prompt:"
    Write-Warning "  netsh advfirewall firewall add rule name=`"$fwRuleName`" dir=in action=allow protocol=TCP localport=$listenPort"
}

# ── 7. Start the listener now so the host doesn't need to log out/in ──────────

Write-Host "Launching ankd.exe in --listen mode..."
Start-Process -FilePath "$InstDir\$ExeName" `
    -ArgumentList "--listen=$Listen", "--token-file=`"$TokenFile`"" `
    -WindowStyle Hidden

# ── 8. Summary ────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "VM side installed."
Write-Host "  Binary    : $InstDir\$ExeName"
Write-Host "  Token     : $TokenFile"
Write-Host "  Listening : $Listen"
Write-Host "  Autostart : HKCU\...\Run\$RunValueName"
Write-Host "  Firewall  : inbound TCP $listenPort allowed (or attempted -- see above)"
Write-Host ""
Write-Host "Networking note:"
Write-Host "  Bound to $Listen (all interfaces) on purpose. QEMU user-mode"
Write-Host "  (slirp) hostfwd delivers to the guest's NAT IP, not loopback, so a"
Write-Host "  127.0.0.1 binding is unreachable from the Linux host and the popup"
Write-Host "  stays orange. If it stays orange, verify the firewall rule for TCP"
Write-Host "  $listenPort exists (add it elevated if this script could not)."
Write-Host ""
Write-Host "OneNote / WinApps notes:"
Write-Host "  - Open OneNote at least once in this VM session so the COM"
Write-Host "    bridge has a process to attach to. OneNoteClient also"
Write-Host "    falls back to launching it (New-Object), so this is a UX"
Write-Host "    nicety rather than a correctness requirement."
Write-Host "  - Keep ankd.exe and OneNote in the VM's user session, NOT"
Write-Host "    tied to a WinApps RemoteApp window's lifetime -- otherwise"
Write-Host "    closing the OneNote window from Linux can kill the COM"
Write-Host "    server and stall lookups."
Write-Host ""
Write-Host 'On the Linux host:'
Write-Host '  - Confirm the QEMU launcher forwards'
Write-Host '      -netdev user,id=net0,hostfwd=tcp:127.0.0.1:9876-:9876'
Write-Host '  - Confirm the same token sits in ~/.config/anchorhub/token'
Write-Host '    (or $XDG_CONFIG_HOME/anchorhub/token when that var is set)'
