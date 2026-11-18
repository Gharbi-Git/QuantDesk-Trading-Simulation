# Module ownership — authoritative 15 Nov 2026 roadmap

| Module | Owner | Main implementation surface |
|---|---|---|
| F00 Planning, UML & GitHub | Both | docs, GitHub, contracts |
| P00 Docker Platform & Keycloak | Developer 2 | infra, API host, web shell |
| M01 Market Data | Developer 1 | .NET + React |
| M02 Quantitative Indicators | Developer 1 | quant-core + .NET + React |
| M03 Strategy Registry | Developer 1 | .NET + React |
| M04 Backtesting | Developer 1 | C++ backtest + .NET + React |
| M05 Strategy Runner & Paper Trading | Developer 1 | Rust + Python SDK + .NET + React |
| M06 Matching Engine | Developer 2 | C++ exchange + .NET + React |
| M07 Portfolio & Accounting | Developer 2 | .NET + React |
| M08 Risk Management | Developer 1 | .NET / quant-core + React |
| M09 Performance Analytics | Developer 1 | .NET / quant-core + React |
| M11 Equity Options | Developer 1 | .NET / quant-core + React |
| M12 Market Simulation | Developer 2 | .NET + React |
| M13 Arena | Developer 2 | .NET + React |
| M14 Administration & Education | Developer 2 | .NET + React |
| Q15 Integration & Testing | Both | tests, docs, release/demo checks |

Original QD-001...QD-235 issues are retained. The older corrected backlog PDF
has a superseded ownership split; the synchronized roadmap takes precedence.