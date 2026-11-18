#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Go([string]$cmd, [string[]]$parameters, [string]$directory) {
    Push-Location $directory
    try { & $cmd @parameters; if ($LASTEXITCODE -ne 0) { throw "$cmd failed" } }
    finally { Pop-Location }
}
Go 'dotnet' @('test','apps/api/QuantDesk.sln','-c','Debug') $root
Go 'ctest' @('--test-dir','out/build/windows-x64','-C','Debug','--output-on-failure') (Join-Path $root 'engines')
Go 'cargo' @('test','--locked') (Join-Path $root 'services/strategy-runner')
Go 'npm.cmd' @('test') (Join-Path $root 'apps/web')
if (Get-Command py -ErrorAction SilentlyContinue) {
    Go 'py' @('-3','-m','unittest','discover','-s','tests','-v') (Join-Path $root 'sdk/python')
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
    Go 'python' @('-m','unittest','discover','-s','tests','-v') (Join-Path $root 'sdk/python')
} else { Write-Warning 'Python is not installed; skipped Python SDK smoke tests.' }
Write-Host 'QuantDesk bootstrap tests completed.' -ForegroundColor Green