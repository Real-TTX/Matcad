<#
.SYNOPSIS
  Rebuilds the images and (re)deploys the full Matcat stack.
  This is the live-reload / testing workflow: always rebuild + redeploy.

.PARAMETER Mode
  dev     -> docker-compose.dev.yml     (local version, whoami test upstream)
  release -> docker-compose.release.yml (release version, 80/443)

.EXAMPLE
  ./scripts/deploy.ps1            # dev
  ./scripts/deploy.ps1 -Mode release
#>
param(
    [ValidateSet('dev', 'release')]
    [string]$Mode = 'dev'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($Mode -eq 'release') {
    $composeFile = 'docker-compose.release.yml'
    $env:MATCAT_VERSION = & "$PSScriptRoot/version.ps1" -Mode release
} else {
    $composeFile = 'docker-compose.dev.yml'
    $env:MATCAT_VERSION = & "$PSScriptRoot/version.ps1" -Mode local
}

Write-Host "Deploying Matcat ($Mode) version $($env:MATCAT_VERSION)..." -ForegroundColor Cyan
docker compose -f $composeFile up -d --build
if ($LASTEXITCODE -ne 0) { throw "docker compose failed with exit code $LASTEXITCODE" }

Write-Host "Done. Matcat UI: http://localhost:4433" -ForegroundColor Green
