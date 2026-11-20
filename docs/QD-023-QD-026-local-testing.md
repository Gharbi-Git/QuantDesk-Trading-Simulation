
# QD-023 / QD-026 — Local Database Testing

## Prerequisites

- .NET SDK 10
- Docker Desktop running
- PostgreSQL started through QuantDesk Docker Compose
- Local `infra/compose/.env` configured
- EF Core CLI (`dotnet-ef`) version 10.0.15

## Application database

The QuantDesk application uses PostgreSQL:

- Host: `127.0.0.1`
- Port: `5433`
- Database: `quantdesk`
- User: `qd_app`

Store `ConnectionStrings:QuantDesk` using ASP.NET Core User Secrets.
Never commit database passwords or local `.env` files.

Apply migrations:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"

dotnet ef database update `
    --project .\apps\api\src\QuantDesk.Modules `
    --startup-project .\apps\api\src\QuantDesk.Api `
    --context QuantDeskDbContext
```

## Integration test database

Integration tests use a separate database named `quantdesk_test`.

Create it once:

```powershell
docker compose --env-file .\infra\compose\.env `
    -f .\infra\compose\compose.yaml `
    exec -T postgres psql -U postgres -d postgres -c `
    "CREATE DATABASE quantdesk_test OWNER qd_app;"
```

If the database already exists, skip creation.

Configure the test connection in the PowerShell terminal
that will run the tests:

```powershell
$envLine = Get-Content .\infra\compose\.env |
    Where-Object { $_ -match '^\s*QD_APP_DB_PASSWORD=' } |
    Select-Object -First 1

if (-not $envLine) {
    throw "QD_APP_DB_PASSWORD is missing."
}

$dbPassword = $envLine.Substring($envLine.IndexOf('=') + 1).Trim()

if ($dbPassword.Length -ge 2) {
    if (
        ($dbPassword.StartsWith('"') -and $dbPassword.EndsWith('"')) -or
        ($dbPassword.StartsWith("'") -and $dbPassword.EndsWith("'"))
    ) {
        $dbPassword = $dbPassword.Substring(1, $dbPassword.Length - 2)
    }
}

$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection["Host"] = "127.0.0.1"
$connection["Port"] = 5433
$connection["Database"] = "quantdesk_test"
$connection["Username"] = "qd_app"
$connection["Password"] = $dbPassword

$env:QD_TEST_CONNECTION_STRING = $connection.ConnectionString

Remove-Variable envLine, dbPassword, connection
```

Do not print the connection string: it contains a password.

## Run the tests

```powershell
dotnet build .\apps\api\QuantDesk.sln

dotnet test .\apps\api\QuantDesk.sln --no-build
```

The provisioning integration tests verify:

- New user creation
- Repeated login returning the same internal user ID
- Username/email updates
- Concurrent requests without duplicate users

The tests create and clean up synthetic user records in
`quantdesk_test`. They must never run against `quantdesk`.

## Authentication behavior

Keycloak manages authentication and credentials.

After a successful Keycloak login, the API reads the validated
subject (`sub`) and creates or updates the corresponding
`Utilisateur` in the QuantDesk PostgreSQL database.

`keycloak_subject` is unique, so one Keycloak identity maps
to one stable local QuantDesk user ID.
