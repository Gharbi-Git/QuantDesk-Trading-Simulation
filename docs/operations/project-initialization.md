# QuantDesk - project initialization (bootstrap only)

The single Git repository contains all 13 functional modules, not 13 repositories.
Run `Initialize-QuantDesk-Projects.ps1` from Windows PowerShell 5.1 or later,
with the existing Git checkout at `C:\QuantDesk-Trading-Simulation`.

## Verification and local run

From the repository root:

```powershell
.\scripts\build\build-all.ps1
.\scripts\test\test-all.ps1
.\scripts\build\start-infrastructure.ps1
cd apps\api
dotnet run --project src\QuantDesk.Api --urls http://localhost:5080
# In another terminal: cd C:\QuantDesk-Trading-Simulation\apps\web; npm run dev
```

- API smoke endpoint: http://localhost:5080/health
- API module-catalog smoke endpoint: http://localhost:5080/api/meta/modules
- Vite frontend: http://localhost:5173
- PostgreSQL: 127.0.0.1:5433 (separate qd_app and qd_keycloak database roles)
- Keycloak development console: http://localhost:8180
- For an entire app/web image build: `docker compose --env-file infra/compose/.env -f infra/compose/compose.yaml --profile app up --build -d`.
- For isolated Linux C++ tests: `docker build -f infra/docker/engines.Dockerfile -t quantdesk-engines-smoke .`.

## Bootstrap is NOT completed features

The scaffold API exposes only health and roadmap-module metadata. The module
folders do not yet have application services, persistence mappings or controllers.
React only displays a development shell. C++/Rust executables are startup smokes.
Keycloak realm/roles are imported, but no OIDC/BFF client or protected endpoints
exist yet. PostgreSQL has isolated development databases/users, but no EF Core
migration or business tables. Protobuf contracts, real-time SignalR, sandbox
hardening, market-data integrations and financial flows are future tasks.

## Secrets and safety

`infra/compose/.env` is generated with cryptographic random values and ignored by
Git. Never commit this file. The dev infrastructure binds DB and Keycloak ports
to 127.0.0.1, but `start-dev` is not suitable for public production exposure.
The bootstrap ASP.NET Core endpoints are unauthenticated; keep the server local.
Do not run `docker compose down -v` unless you intend to DESTROY local DB volumes.

## Coordination

Developer 1 owns M01/M02/M03/M04/M05/M08/M09/M11. Developer 2 owns P00,
M06/M07/M12/M13/M14. F00 and Q15 remain shared. Contracts must be frozen before
wiring financial cross-module behavior. Keep the branch and Git history intact.