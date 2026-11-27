param([Parameter(Mandatory=$true)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
& "$PSScriptRoot\Build-ExchangeTrading.ps1" -RepoRoot $repo
$exe = "$HOME\QuantDeskBuild\exchange-trading-repo-build\Debug\qd_exchange_trading.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
dotnet run --project "$PSScriptRoot\client-smoke\QuantDesk.Exchange.ClientSmoke.csproj" --configuration Debug -- $exe
if ($LASTEXITCODE -ne 0) { throw 'C# cross-language smoke test failed.' }
