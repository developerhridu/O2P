$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. "$root\scripts\Initialize-O2PEnvironment.ps1"
Initialize-O2PEnvironment -RootPath $root
Set-Location "$root\publish\worker"

# Supervisor loop: keep the Worker running. It exits (code 0) when an admin uses "Cancel all &
# restart worker", and would also exit on an unexpected crash - relaunch it either way so the node
# self-heals. To stop it for a redeploy, deploy.ps1 kills THIS supervising powershell process
# (matched by the run-worker.ps1 script name), which prevents it from relaunching the Worker.
while ($true) {
    dotnet ".\O2P.Worker.dll"
    Write-Host "[Worker] exited (code $LASTEXITCODE); relaunching in 3s..." -ForegroundColor Yellow
    Start-Sleep -Seconds 3
}
