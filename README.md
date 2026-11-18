# QuantDesk — Trading Simulation

A virtual-funds equities and listed equity-options trading simulator with:
manual trading, financial accounting, historical backtesting, approved OCI
strategies, paper trading, simulated market sessions, risk analytics and 1v1 Arena.

## Architecture

- One GitHub monorepo; no separate repositories per module.
- ASP.NET Core 10 modular monolith with Onion layers per business module.
- One React/TypeScript frontend with feature-owned screens.
- C++20 quant-core library and standalone historical/matching engines.
- Rust Tokio strategy runner and Python strategy SDK.
- PostgreSQL + Keycloak managed by Docker Compose.
- REST/SignalR to the web; versioned protobuf/gRPC and framed JSON at process boundaries.

## Ownership

Developer 1: M01/M02/M03/M04/M05/M08/M09/M11.
Developer 2: P00/M06/M07/M12/M13/M14.
Shared: F00 planning/UML and Q15 integration/testing.

See `docs/architecture/module-ownership.md` for the full mapping.

## Development baseline

Native Windows + Docker Desktop, VS 2026/MSVC x64, CMake, .NET 10,
Rust 1.100.0, Node.js 26.13.1 and Git. Toolchain versions are declared
in `global.json`, `.node-version`, and `rust-toolchain.toml`.

## Important

The buildable project skeleton is initialized; no trading or financial functionality is implemented.
It does not yet contain functional engines, API endpoints, React pages,
Cargo crates, a runnable Compose stack, or completed QD tasks.
Build and application projects should be initialized next, in reviewed PRs.

## Repository hygiene

Do not commit `.env`, local secrets, build output, runtime volumes, or API keys.
There must be only one `.git` directory, at this repository root.