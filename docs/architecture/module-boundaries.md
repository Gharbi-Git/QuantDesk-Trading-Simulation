# Backend and engine boundaries

- **Domain**: business rules, entities, value objects, invariants; no EF Core, HTTP, gRPC.
- **Application**: use cases and outbound interfaces; depends on Domain.
- **Infrastructure**: EF Core, market providers, gRPC/native adapters; implements Application ports.
- **Presentation**: REST endpoints and DTO validation; calls Application only.
- **Modules**: communicate through explicit application interfaces or documented events,
  not by reaching into one another's persistence implementation.
- **Financial authority**: Developer 2 Portfolio owns cash/positions/reservations/settlement.
  C++ matching owns price-time matching, but never bypasses authoritative reservations.
- **Risk**: Developer 1 owns risk policies; clarify risk failure and timeout behavior before trading.
- **C++**: quant-core is a library; backtest and exchange are separate engine processes.
- **Rust**: trusted runner controls approved sandboxed strategy containers; no direct DB access.
- **Market data**: use immutable, provenance-labelled snapshots and UTC event ordering.
- **Replay**: identical seed + input snapshot + model version must reproduce output.