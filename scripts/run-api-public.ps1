param(
    [int]$Port = 5000
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. "$root\scripts\Initialize-O2PEnvironment.ps1"
Initialize-O2PEnvironment -RootPath $root
Set-Location "$root\publish\api"
dotnet ".\O2P.Api.dll" --urls "http://0.0.0.0:$Port"
