# Keeps the installed ankd at the head of this checkout's upstream branch:
# pull, install (which stops the running ankd; the extension reconnects and
# starts the new one), then wait for the next commit. Ctrl+C ends the loop.
# The browser has no CLI to reload an extension, so a change below extension/
# is only announced; reload it by hand.
param(
    [string] $Installer = 'install',       # install (Firefox) or install-chrome
    [int]    $PollSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

while ($true) {
    $before = git rev-parse HEAD
    git pull --no-rebase
    if ($LASTEXITCODE -ne 0) { throw 'git pull failed' }
    if (git diff --name-only $before HEAD -- extension) {
        Write-Host 'extension/ changed: reload AnchorHub in about:debugging or chrome://extensions.' -ForegroundColor Yellow
    }
    & (Join-Path 'install' "$Installer.ps1")
    Write-Host "Installed $(git log -1 --format='%h %s'). Waiting for new commits..."
    do {
        Start-Sleep -Seconds $PollSeconds
        git fetch --quiet
    } while ([int](git rev-list --count 'HEAD..@{u}') -eq 0)
}
