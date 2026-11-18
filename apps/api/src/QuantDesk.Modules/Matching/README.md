# M06 - Matching

**Owner:** D2 (synchronized roadmap, 15 November 2026)

**Scope:** Orders, C++ matching-engine adapter, command and execution lifecycles.

This module uses Onion architecture: Domain <- Application <- external adapters.
Presentation and Infrastructure depend on Application; Domain depends on none.
Do not access another module's database tables or internal classes directly.
Start with explicit application contracts and acceptance tests for the assigned QD issues.