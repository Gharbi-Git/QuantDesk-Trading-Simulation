#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$envFile = Join-Path $root 'infra/compose/.env'
$compose = Join-Path $root 'infra/compose/compose.yaml'
& docker compose --env-file $envFile -f $compose up -d postgres keycloak
if ($LASTEXITCODE -ne 0) { throw 'Docker infrastructure startup failed.' }
Write-Host 'PostgreSQL: localhost:5433 ; Keycloak: http://localhost:8180' -ForegroundColor Green