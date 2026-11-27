param([Parameter(Mandatory=$true)][string]$RepoRoot, [switch]$RebuildNative)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
if ($RebuildNative) { & "$PSScriptRoot\Build-ExchangeTrading.ps1" -RepoRoot $repo }
$exe = "$HOME\QuantDeskBuild\exchange-trading-repo-build\Debug\qd_exchange_trading.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Missing previously built native executable. Run again with -RebuildNative in VS developer PowerShell.' }
dotnet run --project "$PSScriptRoot\backend-client-smoke\QuantDesk.Exchange.BackendClientSmoke.csproj" --configuration Debug -- $exe
if ($LASTEXITCODE -ne 0) { throw 'Backend client smoke failed.' }
