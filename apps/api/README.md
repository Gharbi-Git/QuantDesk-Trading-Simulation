# ASP.NET Core backend

Initial structure only. Initialize these three production projects next:
- `src/QuantDesk.Api` (ASP.NET Core host and BFF)
- `src/QuantDesk.Shared` (minimal stable primitives)
- `src/QuantDesk.Modules` (business modules with Onion layers)

Add tests in `tests/`. Use .NET 10 selected by repository root `global.json`.
The buildable .NET 10 solution and baseline tests are initialized by scripts/setup/Initialize-QuantDesk-Projects.ps1.