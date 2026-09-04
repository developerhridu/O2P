param(
    [int]$Port = 3051,
    [string]$ApiBaseUrl = "http://27.147.159.194:3052/api/v1"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location "$root\web"

cmd /c "set VITE_API_BASE_URL=$ApiBaseUrl&& npm run build" | Out-Null

$env:O2P_UI_PORT = $Port
node "$root\scripts\serve-ui-static.js"
