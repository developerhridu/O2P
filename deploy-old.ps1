# deploy.ps1 - O2P automated Docker deployment for Windows Server
param(
    [string]$ServerHost = "27.147.159.197",
    [switch]$SkipFirewall,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "      O2P ORACLE TO POSTGRES - DEPLOYMENT SCRIPT          " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

# === 1. Privilege and environment setup =======================================
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $SkipFirewall) {
    Write-Host "[WARN] Not running as Administrator. Firewall rules will be skipped." -ForegroundColor Yellow
    $SkipFirewall = $true
}

$rootPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $rootPath

Write-Host "[Deploy] Project path: $rootPath" -ForegroundColor Cyan
Write-Host "[Network] Public address: $ServerHost" -ForegroundColor Cyan

# === 2. Check prerequisites ====================================================
if (Get-Command docker -ErrorAction SilentlyContinue) {
    Write-Host "[OK] Docker CLI found." -ForegroundColor Green
} else {
    Write-Host "[ERROR] Docker is not installed or not available on PATH." -ForegroundColor Red
    Exit 1
}

$dockerService = Get-Service -Name "com.docker.service" -ErrorAction SilentlyContinue
if ($dockerService -and $dockerService.Status -ne "Running") {
    if ($isAdmin) {
        Write-Host "[Docker] Docker Desktop Service is stopped. Starting it..." -ForegroundColor Cyan
        Start-Service -Name "com.docker.service"
        Start-Sleep -Seconds 8
    } else {
        Write-Host "[ERROR] Docker Desktop Service is stopped and this session is not Administrator." -ForegroundColor Red
        Write-Host "        Rerun PowerShell as Administrator or start Docker Desktop manually, then rerun this script." -ForegroundColor Yellow
        Exit 1
    }
}

docker version | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Docker engine is not reachable. Start Docker Desktop or the Docker service, then rerun this script." -ForegroundColor Red
    Exit 1
}
Write-Host "[OK] Docker engine is reachable." -ForegroundColor Green

docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Docker Compose v2 is required." -ForegroundColor Red
    Exit 1
}
Write-Host "[OK] Docker Compose found." -ForegroundColor Green

# === 3. Configure deployment environment ======================================
$envPath = Join-Path $rootPath ".env"
$webEnvPath = Join-Path $rootPath "web\.env.production"

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

$existingEnv = ""
if (Test-Path $envPath) {
    $existingEnv = Get-Content $envPath -Raw
}

function Get-OrCreateEnvValue([string]$Content, [string]$Key, [string]$DefaultValue) {
    $match = [regex]::Match($Content, "(?m)^$([regex]::Escape($Key))=(.*)$")
    if ($match.Success -and -not [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
        return $match.Groups[1].Value.Trim()
    }
    return $DefaultValue
}

$metadataDb = Get-OrCreateEnvValue $existingEnv "O2P_METADATA_DB" "nonoraclemigrationdb"
$metadataUser = Get-OrCreateEnvValue $existingEnv "O2P_METADATA_USER" "nonoraclemigrationdb"
$metadataPassword = Get-OrCreateEnvValue $existingEnv "O2P_METADATA_PASSWORD" (New-HexSecret 24)
$jwtSecret = Get-OrCreateEnvValue $existingEnv "O2P_JWT_SECRET" (New-HexSecret 64)

$envFile = @"
O2P_METADATA_DB=$metadataDb
O2P_METADATA_USER=$metadataUser
O2P_METADATA_PASSWORD=$metadataPassword
O2P_JWT_SECRET=$jwtSecret
"@
[System.IO.File]::WriteAllText($envPath, $envFile.Trim() + [Environment]::NewLine)
Write-Host "[Config] Root .env configured with metadata DB and JWT secrets." -ForegroundColor Green

$apiBaseUrl = "http://${ServerHost}:3052/api/v1"
"VITE_API_BASE_URL=$apiBaseUrl" | Out-File -FilePath $webEnvPath -Encoding utf8 -NoNewline
Write-Host "[Config] Web API URL set to: $apiBaseUrl" -ForegroundColor Green

# === 4. Configure Windows Firewall ============================================
if (-not $SkipFirewall) {
    Write-Host ""
    Write-Host "[Firewall] Configuring Windows Firewall rules..." -ForegroundColor Cyan

    $rules = @(
        @{ Name = "O2P-UI-3051"; Port = 3051; Description = "O2P web UI" },
        @{ Name = "O2P-API-3052"; Port = 3052; Description = "O2P browser API proxy" }
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
    Write-Host "[Firewall] Skipped. Open TCP ports 3051 and 3052 manually if needed." -ForegroundColor Yellow
}

# === 5. Validate source builds =================================================
Write-Host ""
Write-Host "[Build] Validating .NET solution..." -ForegroundColor Cyan
dotnet build src\O2P.slnx -c Release

Write-Host ""
Write-Host "[Build] Validating React web app..." -ForegroundColor Cyan
Push-Location web
cmd /c npm install
cmd /c npm run build
Pop-Location

# === 6. Deploy containers ======================================================
Write-Host ""
Write-Host "[Deploy] Stopping previous O2P containers..." -ForegroundColor Cyan
docker compose down

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "[Deploy] Building Docker images..." -ForegroundColor Cyan
    docker compose build --pull
}

Write-Host ""
Write-Host "[Deploy] Starting O2P stack..." -ForegroundColor Cyan
docker compose up -d

Write-Host ""
Write-Host "[Status] Container status:" -ForegroundColor Cyan
docker compose ps

# === 7. Summary ================================================================
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "[OK] O2P Deployment Completed" -ForegroundColor Green
Write-Host "" -ForegroundColor Green
Write-Host "  Web UI             : http://${ServerHost}:3051" -ForegroundColor Cyan
Write-Host "  API / Swagger      : http://${ServerHost}:3052/swagger" -ForegroundColor Cyan
Write-Host "  API Base           : http://${ServerHost}:3052/api/v1" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Bootstrap user     : admin" -ForegroundColor Yellow
Write-Host "  Admin password     : read from .env / O2P_ADMIN_PASSWORD" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Check status       : docker compose ps" -ForegroundColor Yellow
Write-Host "  View logs          : docker compose logs -f api worker ui" -ForegroundColor Yellow
Write-Host "  Restart stack      : docker compose restart" -ForegroundColor Yellow
Write-Host ""
Write-Host "  NOTE: Do not expose metadata PostgreSQL port 7936 publicly unless you intentionally need DB administration access." -ForegroundColor Yellow
Write-Host "  NOTE: Router/ISP forwarding for ports 3051 and 3052 may be required for public internet access." -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Green
