#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Go([string]$cmd, [string[]]$parameters, [string]$directory) {
    Push-Location $directory
    try { & $cmd @parameters; if ($LASTEXITCODE -ne 0) { throw "$cmd failed" } }
    finally { Pop-Location }
}
Go 'dotnet' @('build','apps/api/QuantDesk.sln','-c','Debug') $root
Go 'cmake' @('--preset','windows-x64') (Join-Path $root 'engines')
Go 'cmake' @('--build','--preset','windows-x64-debug') (Join-Path $root 'engines')
Go 'cargo' @('build','--locked') (Join-Path $root 'services/strategy-runner')
Go 'npm.cmd' @('run','build') (Join-Path $root 'apps/web')
Write-Host 'QuantDesk bootstrap builds completed.' -ForegroundColor Green