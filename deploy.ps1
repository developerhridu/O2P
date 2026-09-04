# deploy.ps1 - O2P one-click native Windows deployment (no Docker required)
#
# Publishes the API and Worker, builds the web UI against this server's own
# public address, opens the firewall, stops any previously running O2P
# processes, and relaunches the full stack (API, Worker, proxy, UI) in the
# background. Safe to re-run any time to redeploy the latest code.
#
# Usage:
#   .\deploy.ps1                                   # deploy for 27.147.159.194 (default)
#   .\deploy.ps1 -ServerHost 27.147.159.197         # deploy for a different server/IP
#   .\deploy.ps1 -SkipBuild                         # relaunch without rebuilding
#   .\deploy.ps1 -SkipFirewall                      # skip firewall rule creation

param(
    [string]$ServerHost = "27.147.159.194",
    [int]$UiPort = 3051,
    [int]$ProxyPort = 3052,
    [int]$ApiPort = 5050,
    [switch]$SkipFirewall,
    [switch]$SkipBuild,
    # Run the Worker on exactly ONE machine (the one that can reach the Oracle source). Running a
    # Worker on several servers makes them all pull from the same job queue and open Oracle sessions
    # in parallel, which overloads a constrained source (ORA-50000). Use -NoWorker on the public
    # UI/API servers, and either a normal deploy or -WorkerOnly on the single Oracle-side Worker box.
    [switch]$NoWorker,     # deploy UI + API only (no Worker)
    [switch]$WorkerOnly    # deploy the Worker only (no UI/API/proxy)
)

$ErrorActionPreference = "Stop"

if ($NoWorker -and $WorkerOnly) {
    Write-Host "[ERROR] -NoWorker and -WorkerOnly are mutually exclusive." -ForegroundColor Red
    Exit 1
}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "      O2P ORACLE TO POSTGRES - ONE-CLICK DEPLOYMENT       " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

# === 1. Privilege check =========================================================
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $SkipFirewall) {
    Write-Host "[WARN] Not running as Administrator. Firewall rules will be skipped." -ForegroundColor Yellow
    $SkipFirewall = $true
}

$rootPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $rootPath

Write-Host "[Deploy] Project path: $rootPath" -ForegroundColor Cyan
Write-Host "[Network] Public address: $ServerHost" -ForegroundColor Cyan

# === 2. Check prerequisites =====================================================
foreach ($cmd in @("dotnet", "node", "npm")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Write-Host "[ERROR] '$cmd' is not installed or not on PATH. Install the .NET 8 SDK and Node.js first." -ForegroundColor Red
        Exit 1
    }
}
Write-Host "[OK] dotnet, node, npm found." -ForegroundColor Green

# === 3. Configure secrets (.env) ================================================
function New-HexSecret([int]$Bytes) {
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($buffer)
    } finally {
        $rng.Dispose()
    }
    return [System.BitConverter]::ToString($buffer).Replace("-", "").ToLowerInvariant()
}

function Get-OrCreateEnvValue([string]$Content, [string]$Key, [string]$DefaultValue) {
    $match = [regex]::Match($Content, "(?m)^$([regex]::Escape($Key))=(.*)$")
    if ($match.Success -and -not [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
        return $match.Groups[1].Value.Trim()
    }
    return $DefaultValue
}

$envPath = Join-Path $rootPath ".env"
$existingEnv = ""
if (Test-Path $envPath) {
    $existingEnv = Get-Content $envPath -Raw
}

$metadataDb = Get-OrCreateEnvValue $existingEnv "O2P_METADATA_DB" "nonoraclemigrationdb"
$metadataUser = Get-OrCreateEnvValue $existingEnv "O2P_METADATA_USER" "nonoraclemigrationdb"
$metadataPassword = Get-OrCreateEnvValue $existingEnv "O2P_METADATA_PASSWORD" (New-HexSecret 24)
$jwtSecret = Get-OrCreateEnvValue $existingEnv "O2P_JWT_SECRET" (New-HexSecret 64)
$adminPassword = Get-OrCreateEnvValue $existingEnv "O2P_ADMIN_PASSWORD" ""

$envLines = @(
    "O2P_METADATA_DB=$metadataDb"
    "O2P_METADATA_USER=$metadataUser"
    "O2P_METADATA_PASSWORD=$metadataPassword"
    "O2P_JWT_SECRET=$jwtSecret"
)
if (-not [string]::IsNullOrWhiteSpace($adminPassword)) {
    $envLines += "O2P_ADMIN_PASSWORD=$adminPassword"
}
[System.IO.File]::WriteAllText($envPath, ($envLines -join [Environment]::NewLine) + [Environment]::NewLine)
Write-Host "[Config] .env configured (existing secrets preserved where already present)." -ForegroundColor Green

# === 4. Stop any previously running O2P processes ==============================
# The publish step below overwrites the API/Worker DLLs. If any O2P process is still running it
# holds those files open and publish fails with "the process cannot access the file ... because it
# is being used by another process". So we forcibly stop every O2P process first, then WAIT until
# they are really gone (handles release a moment after the process exits) before building.
Write-Host ""
Write-Host "[Deploy] Stopping any previously running O2P processes..." -ForegroundColor Cyan

# Match the API/Worker in every form they can run as: the published DLLs, the bin\Debug|Release
# DLLs, and `dotnet run --project ...\O2P.Api` dev instances. Matching the project/DLL *name*
# (not just "*.dll") is what catches a `dotnet run` instance, which would otherwise keep the build
# output locked. Also stop the browser proxy and static UI host (node).
function Get-O2PProcesses {
    # Include the supervising powershell processes for the run-*.ps1 launchers. run-worker.ps1 now
    # loops to relaunch the Worker, so killing only the dotnet child would let the supervisor bring
    # it right back - we must stop the supervisor script itself before publishing.
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe' OR Name = 'node.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe'" |
        Where-Object {
            $_.CommandLine -and (
                $_.CommandLine -like "*O2P.Api*" -or
                $_.CommandLine -like "*O2P.Worker*" -or
                $_.CommandLine -like "*api-proxy.js*" -or
                $_.CommandLine -like "*serve-ui-static.js*" -or
                $_.CommandLine -like "*run-worker.ps1*" -or
                $_.CommandLine -like "*run-api-public.ps1*" -or
                $_.CommandLine -like "*run-proxy-public.ps1*" -or
                $_.CommandLine -like "*run-ui-public.ps1*"
            )
        }
}

function Stop-O2PProcesses {
    $procs = @(Get-O2PProcesses)
    foreach ($p in $procs) {
        Write-Host "  Stopping PID $($p.ProcessId): $($p.CommandLine)" -ForegroundColor Yellow
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    return $procs.Count
}

# Shut down the persistent Roslyn/MSBuild build servers too - they keep handles open on the
# project output and are another common source of "file is being used" errors during publish.
try { & dotnet build-server shutdown *> $null } catch { }

$null = Stop-O2PProcesses
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 1
    $remaining = @(Get-O2PProcesses)
    if ($remaining.Count -eq 0) { break }
    Write-Host "  Waiting for $($remaining.Count) O2P process(es) to fully exit..." -ForegroundColor Yellow
    $null = Stop-O2PProcesses
}

if ((@(Get-O2PProcesses)).Count -gt 0) {
    Write-Host "[WARN] Some O2P processes are still running; publish may fail on locked files." -ForegroundColor Red
} else {
    Write-Host "[OK] No O2P processes running; safe to build/publish." -ForegroundColor Green
}

# === 5. Build & publish ==========================================================
$apiBaseUrl = "http://${ServerHost}:$ProxyPort/api/v1"

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "[Build] Publishing O2P.Api..." -ForegroundColor Cyan
    dotnet publish src\O2P.Api\O2P.Api.csproj -c Release -o publish\api

    Write-Host ""
    Write-Host "[Build] Publishing O2P.Worker..." -ForegroundColor Cyan
    dotnet publish src\O2P.Worker\O2P.Worker.csproj -c Release -o publish\worker

    Write-Host ""
    Write-Host "[Build] Building React web app..." -ForegroundColor Cyan
    Push-Location web
    $env:VITE_API_BASE_URL = $apiBaseUrl
    cmd /c npm install
    cmd /c npm run build
    Pop-Location
    Write-Host "[Config] Web API URL set to: $apiBaseUrl" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[Build] Skipped (-SkipBuild). Reusing existing publish\ and web\dist." -ForegroundColor Yellow
}

# === 6. Configure Windows Firewall ===============================================
if (-not $SkipFirewall) {
    Write-Host ""
    Write-Host "[Firewall] Configuring Windows Firewall rules..." -ForegroundColor Cyan

    $rules = @(
        @{ Name = "O2P-UI-$UiPort"; Port = $UiPort; Description = "O2P web UI" },
        @{ Name = "O2P-API-$ProxyPort"; Port = $ProxyPort; Description = "O2P browser API proxy" }
    )

    foreach ($rule in $rules) {
        $existingRule = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
        if (-not $existingRule) {
            New-NetFirewallRule `
                -DisplayName $rule.Name `
                -Direction Inbound `
                -Protocol TCP `
                -LocalPort $rule.Port `
                -Action Allow `
                -Profile Any `
                -Description $rule.Description | Out-Null
            Write-Host "[Firewall] Created inbound rule for port $($rule.Port)." -ForegroundColor Green
        } else {
            Write-Host "[Firewall] Port $($rule.Port) rule already exists." -ForegroundColor Green
        }
    }
} else {
    Write-Host "[Firewall] Skipped. Open TCP ports $UiPort and $ProxyPort manually if needed." -ForegroundColor Yellow
}

# === 7. Launch the stack =========================================================
Write-Host ""
Write-Host "[Deploy] Launching O2P processes..." -ForegroundColor Cyan

# Initialize-O2PEnvironment.ps1 only allow-lists a single default UI origin for
# CORS. Add this server's own UI origin as an extra allowed origin so browser
# calls aren't blocked when deploying to a ServerHost other than the default.
$env:Security__Cors__AllowedOrigins__5 = "http://${ServerHost}:$UiPort"

if (-not $WorkerOnly) {
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$rootPath\scripts\run-api-public.ps1`" -Port $ApiPort" -WindowStyle Hidden
    Start-Sleep -Seconds 5
}
if (-not $NoWorker) {
    Write-Host "[Deploy] Launching Worker on this machine ($env:COMPUTERNAME). Run only ONE Worker across all servers." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$rootPath\scripts\run-worker.ps1`"" -WindowStyle Hidden
} else {
    Write-Host "[Deploy] -NoWorker: skipping the Worker on this machine (UI/API only)." -ForegroundColor Yellow
}
if (-not $WorkerOnly) {
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$rootPath\scripts\run-proxy-public.ps1`" -Port $ProxyPort -TargetPort $ApiPort" -WindowStyle Hidden
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$rootPath\scripts\run-ui-public.ps1`" -Port $UiPort -ApiBaseUrl $apiBaseUrl" -WindowStyle Hidden
} else {
    Write-Host "[Deploy] -WorkerOnly: skipping UI/API/proxy on this machine (Worker only)." -ForegroundColor Yellow
}

Write-Host "[Deploy] Waiting for services to come up..." -ForegroundColor Cyan
Start-Sleep -Seconds 8

# === 8. Verify ====================================================================
Write-Host ""
Write-Host "[Status] Verifying endpoints..." -ForegroundColor Cyan

try {
    $ui = Invoke-WebRequest "http://127.0.0.1:$UiPort/login" -UseBasicParsing -TimeoutSec 10
    Write-Host "  UI  (127.0.0.1:$UiPort/login)          -> $($ui.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  UI check failed: $($_.Exception.Message)" -ForegroundColor Red
}

try {
    $api = Invoke-WebRequest "http://127.0.0.1:$ProxyPort/api/v1/auth/login" -Method Post -ContentType "application/json" -Body '{"username":"deploy-check","password":"deploy-check"}' -UseBasicParsing -TimeoutSec 10
    Write-Host "  API via proxy (127.0.0.1:$ProxyPort)    -> $($api.StatusCode)" -ForegroundColor Green
} catch {
    if ($_.Exception.Response) {
        # A 400/401 response means the proxy and API are reachable and answering;
        # it just rejected the bogus probe credentials, which is expected.
        $code = [int]$_.Exception.Response.StatusCode
        Write-Host "  API via proxy (127.0.0.1:$ProxyPort)    -> $code (reachable)" -ForegroundColor Green
    } else {
        Write-Host "  API check failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# === 9. Summary ===================================================================
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "[OK] O2P Deployment Completed" -ForegroundColor Green
Write-Host "" -ForegroundColor Green
Write-Host "  Web UI             : http://${ServerHost}:$UiPort" -ForegroundColor Cyan
Write-Host "  API Base           : http://${ServerHost}:$ProxyPort/api/v1" -ForegroundColor Cyan
Write-Host "  Internal API       : http://127.0.0.1:$ApiPort" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Bootstrap user     : admin" -ForegroundColor Yellow
Write-Host "  Admin password     : whatever it was last rotated to (see UI > Users to reset)" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Check running procs: Get-CimInstance Win32_Process -Filter `"Name='dotnet.exe' OR Name='node.exe'`"" -ForegroundColor Yellow
Write-Host "  Redeploy           : rerun this script (it stops the old processes first)" -ForegroundColor Yellow
Write-Host "  Relaunch only      : .\deploy.ps1 -SkipBuild -ServerHost $ServerHost" -ForegroundColor Yellow
Write-Host ""
Write-Host "  NOTE: Do not expose the metadata PostgreSQL port publicly unless intentional." -ForegroundColor Yellow
Write-Host "  NOTE: The old Docker Compose deployment path is preserved in deploy-old.ps1." -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Green
