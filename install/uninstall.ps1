#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$HostName  = "io.github.contextswitcher.anchorhub"
$RegPath   = "HKCU:\SOFTWARE\Mozilla\NativeMessagingHosts\$HostName"
$InstDir   = "$env:LOCALAPPDATA\Programs\AnchorHub"
$Manifest  = "$env:APPDATA\AnchorHub\$HostName.json"
$Token     = "$env:APPDATA\AnchorHub\fulltext.token"
$Discovery = "$env:APPDATA\JabRef\fulltext-providers\anchorhub.json"

if (Test-Path $RegPath)   { Remove-Item $RegPath   -Recurse -Force }
if (Test-Path $Manifest)  { Remove-Item $Manifest  -Force }
if (Test-Path $Token)     { Remove-Item $Token     -Force }
if (Test-Path $Discovery) { Remove-Item $Discovery -Force }
if (Test-Path $InstDir)   { Remove-Item $InstDir   -Recurse -Force }

Write-Host "Uninstalled."
