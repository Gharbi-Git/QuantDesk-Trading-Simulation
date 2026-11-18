# Contributing to QuantDesk

- One monorepo and one integration branch (`main`).
- Branches: `feat/qd-###-short-name`, `fix/qd-###-short-name`, `docs/qd-###-short-name`.
- One developer owns each functional module end to end (.NET, React, engine, tests).
- Cross-module contracts require both developers' approval.
- Pull requests must link the QD issue and describe tests, contract impact, and migrations.
- Keep `main` runnable after the initial executable scaffold is introduced.
- Avoid a full CI/CD pipeline; local tests and optional minimal PR checks are enough.
- Weekly integration and demonstrations; keep PRs small.
- No real-money trading, broker credentials, or unsafe unrestricted strategy images.