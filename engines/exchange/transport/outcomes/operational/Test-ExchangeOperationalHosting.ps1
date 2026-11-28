param([string]$RepoRoot = (Get-Location).Path)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$exe = "$HOME\QuantDeskBuild\exchange-outcomes-transport-build\Debug\qd_exchange_outcomes.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Missing validated native executable. Run Test-ExchangeOutcomes.ps1 first.' }
if (-not $env:QD_TEST_CONNECTION_STRING) {
    $secure = Read-Host 'Password for qd_app in local quantdesk_test' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $restore = $true
    try {
        $builder['Host'] = '127.0.0.1'
        $builder['Port'] = 5433
        $builder['Database'] = 'quantdesk_test'
        $builder['Username'] = 'qd_app'
        $builder['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        $env:QD_TEST_CONNECTION_STRING = $builder.ConnectionString
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        $builder.Clear()
    }
} else { $restore = $false }
try {
    dotnet run --project "$PSScriptRoot\hosting-smoke\QuantDesk.Exchange.OperationalHostingSmoke.csproj" --configuration Debug -- $exe "$PSScriptRoot\migrations\0001_exchange_v4.sql"
    if ($LASTEXITCODE -ne 0) { throw 'Operational hosting smoke failed.' }
} finally { if ($restore) { Remove-Item Env:\QD_TEST_CONNECTION_STRING -ErrorAction SilentlyContinue } }
