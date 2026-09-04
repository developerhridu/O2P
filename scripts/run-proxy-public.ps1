param(
    [int]$Port = 3052,
    [int]$TargetPort = 5050
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$env:O2P_PROXY_PORT = $Port
$env:O2P_TARGET_HOST = "127.0.0.1"
$env:O2P_TARGET_PORT = $TargetPort

node (Join-Path $root "scripts\api-proxy.js")
