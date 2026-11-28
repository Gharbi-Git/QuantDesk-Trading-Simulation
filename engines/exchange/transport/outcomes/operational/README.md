# M06.12b.1 — Operational persistence schema (review candidate)

The 13-stage M06 regression passed on Windows according to the supplied log. This additive migration establishes an isolated proposed schema, not an operational trading service. It leaves all V1/V4 development tables and services, the shared EF model, and the approved wire contracts unchanged. The test provisions the schema in a single transaction in `quantdesk_test`, exercises it with synthetic IDs/opaque bytes, then rolls back all DDL and fixture data. An existing operational schema aborts the test; no reset/drop is attempted. No real portfolio mutations, engine calls or production database migrations occur.

## Design

- `schema_version` tracks the migration version. The migration is explicitly transactional and not idempotent; a repeat deployment must be managed by a reviewed migration process.
- `journal_binding` is a singleton tied to one V4 epoch. Binding requires explicit healthy/empty V4 status from an authenticated engine. It records the largest observed journal head and the ordered settlement cursor. A regressed head, wrong epoch or unhealthy status blocks recovery. No rollover/reset is provided.
- `commands` shares one command-ID and decision-sequence namespace for registration, submissions and cancellations. Intent identity and payload are immutable. Submissions must pass from pending to reserved to accepted/rejected; a rejected cancellation can be followed by a new cancellation intent, but only one unresolved or accepted cancellation per order is allowed. Cancellation must target the same owner's accepted submission. Registration requires a trusted admin provisioning path in future composition.
- Admission reference and risk policy version are recorded for submission. Their existence does not prove admission/risk authorization: the future M05/M08 integration must validate them. Payloads use serialized V1 request bodies and V2 decision records; the future adapter must verify complete identity, data and payload semantics. The database cannot authenticate engine status, prove actual reservations or prove settlement effects. Only trusted services may call phase/checkpoint transitions. The cursor advances by exactly one after a recorded decision; future consumer must commit every M07 effect first, replay safely and use a single ordered worker (or explicit locking across effects). A high watermark is not cryptographic proof of journal integrity.
- Payload sizes (64 KiB request / 1 MiB decision), one active epoch per database and one immutable submission per order are conservative proposed bounds for review, not measured operational capacity. The existing 512-command/1 MiB engine limits remain.
- Schema objects are owned by the migration role; PUBLIC grants revoked. A privileged owner can bypass protections (e.g. DDL/TRUNCATE). Operational runtime roles, restricted grants and backup/restore, schema migration ownership, safe handling of pending local reservation failures, epoch rollover and receipt verification remain to be implemented/reviewed. Do not grant unrestricted table privileges to players. Do not import test rows or auto-create the schema on app startup.

## Run

From the repository root, with Docker Desktop and local `quantdesk-postgres-1` running:

```powershell
& '.\engines\exchange\transport\outcomes\operational\Test-ExchangeOperationalSchema.ps1'
```

Enter the PostgreSQL password at psql's prompt. The script copies only SQL files into a unique temporary container directory and cleans up afterward. No C++ build, gRPC reinstall or .NET SDK use. Requires `qd_app` to have CREATE SCHEMA privilege on `quantdesk_test`; if that is missing, stop and review permissions rather than grant broadly.

Expected last test message: `M06 OPERATIONAL SCHEMA SMOKE PASSED - ROLLED BACK`.

## Next

After Windows validation, review the schema with D1. Add operational .NET store/dispatcher/consumer composition, exact authenticated status and receipt checks, role grants, migration application procedure and restart/concurrency tests. Keep production admission disabled until capacity, security deployment and M05/M08 authorization are complete.
