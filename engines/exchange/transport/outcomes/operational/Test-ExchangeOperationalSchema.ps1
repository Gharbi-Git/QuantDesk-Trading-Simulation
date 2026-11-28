param([string]$Container = 'quantdesk-postgres-1')
$ErrorActionPreference = 'Stop'
# No password passed on command line, stored in a file or exported in an env var.
# psql prompts interactively inside the existing PostgreSQL container.
$remote = '/tmp/qd-operational-schema-' + [Guid]::NewGuid().ToString('N')
function Require-Success([int]$code, [string]$operation) {
    if ($code -ne 0) { throw "$operation failed (exit $code)." }
}
Get-Command docker -ErrorAction Stop | Out-Null
& docker exec $Container mkdir -p $remote
Require-Success $LASTEXITCODE 'Create isolated temporary directory'
try {
    & docker cp (Join-Path $PSScriptRoot 'migrations') "${Container}:${remote}/migrations"
    Require-Success $LASTEXITCODE 'Copy migration'
    & docker cp (Join-Path $PSScriptRoot 'tests') "${Container}:${remote}/tests"
    Require-Success $LASTEXITCODE 'Copy test'
    & docker exec -it $Container psql -X -W -v ON_ERROR_STOP=1 -U qd_app -d quantdesk_test -f "$remote/tests/verify_schema.sql"
    Require-Success $LASTEXITCODE 'Operational schema smoke'
} finally {
    & docker exec $Container rm -rf -- $remote
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Temporary SQL file cleanup failed.' }
}
