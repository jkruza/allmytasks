# Installs the x64 MSI silently for the current user, checks it, and uninstalls. Run on a Windows x64 machine.
# Usage: pwsh packaging/windows/smoke.ps1 -Msi dist/AllMyTasks-1.2.0-windows-x64.msi -Version 1.2.0
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$msi = (Resolve-Path $Msi).Path
$exe = Join-Path $env:LOCALAPPDATA 'Programs\AllMyTasks\AllMyTasks.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\All My Tasks.lnk'

function Invoke-Msi($arguments) {
    $p = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "msiexec $arguments exited with $($p.ExitCode)" }
}

Invoke-Msi "/i `"$msi`" /qn STARTWITHWINDOWS=1 /l*v `"$env:RUNNER_TEMP\install.log`""
if (-not (Test-Path $exe)) { throw "not installed at $exe" }
if (-not (Test-Path $shortcut)) { throw "Start menu shortcut missing" }
if (-not (Get-ItemProperty $runKey -Name AllMyTasks -ErrorAction SilentlyContinue)) { throw "Run value missing after install" }

# The app is a GUI executable: redirect its output to a file to see what --version printed.
$outFile = Join-Path $env:RUNNER_TEMP 'version.txt'
Start-Process $exe -ArgumentList '--version' -Wait -NoNewWindow -RedirectStandardOutput $outFile
$text = (Get-Content $outFile -Raw).Trim()
if ($text -ne "AllMyTasks $Version") { throw "unexpected version output: '$text'" }

Invoke-Msi "/x `"$msi`" /qn"
if (Test-Path $exe) { throw "executable still present after uninstall" }
if (Test-Path $shortcut) { throw "shortcut still present after uninstall" }
if (Get-ItemProperty $runKey -Name AllMyTasks -ErrorAction SilentlyContinue) { throw "Run value still present after uninstall" }
Write-Host "Windows MSI smoke test passed"
