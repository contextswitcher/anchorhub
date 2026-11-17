# Simulates Firefox launching the native messaging host.
# Sends a framed "status" request and prints the response.
$exe = "$env:LOCALAPPDATA\Programs\AnchorHub\ankd.exe"
$origin = "anchorhub@contextswitcher.github.io"   # argument Firefox passes

$msg = '{"requestId":"test-1","action":"status"}'
$body = [System.Text.Encoding]::UTF8.GetBytes($msg)
$len  = [BitConverter]::GetBytes([int32]$body.Length)   # 4-byte LE

$psi = [System.Diagnostics.ProcessStartInfo]::new($exe, $origin)
$psi.UseShellExecute        = $false
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true

$proc = [System.Diagnostics.Process]::Start($psi)

# Write framed message to stdin.
$proc.StandardInput.BaseStream.Write($len,  0, 4)
$proc.StandardInput.BaseStream.Write($body, 0, $body.Length)
$proc.StandardInput.BaseStream.Flush()

# Read 4-byte length prefix back.
$respLen = [byte[]]::new(4)
$proc.StandardOutput.BaseStream.Read($respLen, 0, 4) | Out-Null
$size = [BitConverter]::ToInt32($respLen, 0)

if ($size -gt 0) {
    $respBody = [byte[]]::new($size)
    $proc.StandardOutput.BaseStream.Read($respBody, 0, $size) | Out-Null
    Write-Host "RESPONSE: $([System.Text.Encoding]::UTF8.GetString($respBody))"
} else {
    Write-Host "RESPONSE: (empty / bad length=$size)"
}

# Close stdin so the host exits.
$proc.StandardInput.Close()
$proc.WaitForExit(3000) | Out-Null

$stderr = $proc.StandardError.ReadToEnd()
if ($stderr) { Write-Host "STDERR: $stderr" }
Write-Host "Exit code: $($proc.ExitCode)"
