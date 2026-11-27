# M06 exchange transport — reviewed v1

Status: D1 approval reported by D2 on 2026-11-27. Schema fields unchanged from the reviewed proposal. Implementation gates below remain open until tested. Based on M06 commit 0cec97b and the M07 TrustedReservedEquityExecution input. This is an exchange-specific transport schema; it does not freeze or replace the canonical OrderRequest, ExecutionEvent, MarketEvent, PortfolioSnapshot, OptionContract or ArenaRuleset.

## Scope and representation

* USD, long-only whole-share equity limit orders only; no fees, market orders, options, or short selling in this slice. Risk, authorization, order lifecycle orchestration and reservations stay outside C++. M06 matches; M07 settles.
* Order, account and session IDs: propose canonical lowercase hyphenated nonzero UUIDs to bridge C# Guid and C++ opaque strings. Instrument ID: exact shared M01 instrument identifier, not necessarily a ticker; confirm format and tick-size policy with D1. Command IDs: propose canonical lowercase UUIDs. Journal epoch: externally provisioned lowercase UUIDv4, checked on every RPC except discovery GetStatus. Never automatically adopt a new epoch or reuse one after divergent restore.
* Signed 64-bit integer prices in units of USD 0.0001; positive whole-share signed 64-bit quantities. Checked price × quantity must fit signed int64 (current native domain limit); reject out-of-range or fractional conversion from decimal, no rounding. M07's wider numeric storage does not expand that limit. Check tick-size separately; no implicit assumption every instrument trades in 0.0001 ticks.
* Enum values are wire values, not C++ enum ordinals; reject unspecified and unrecognized values. Validate nested message presence and every ID, numeric range and event variant; Protobuf alone cannot enforce business rules. JSON fixtures use ProtoJSON int64 strings.
* Timestamp convention: UTC Unix microseconds, assigned by engine when accepting a matching command, persisted with the accepted command before acknowledgement; all fills of that command share its timestamp. Replay never samples the current clock. Decide with D1 whether simulated market time is separately required. Journal V3 stores an eight-byte UTC Unix microsecond timestamp in every accepted command frame (before command payload). V2 remains explicitly readable with absent fill timestamps. V3 requires a fresh journal and unique externally provisioned epoch; no automatic upgrade or historical backfill. No invented timestamps for historical v2 fills. M07 booking time may be distinct from execution time; agree mappings.

## Operations and durability

All engine access (including reads) must be serialized; no direct public access. Authentication/authorization of the internal service must be designed. Loopback-only development without TLS is not a production security boundary.

RegisterBook / SubmitOrder / CancelOrder: same epoch + command ID + identical validated payload returns original durable receipt. Conflicting reuse rejects and does not change original command. Receipt acknowledges the native journal, not PostgreSQL settlement. RegisterBook explicitly provisions session+instrument. Submit creates zero or more fills; state and quantity reflect the original submission, not current order state. Cancel applies to an active or previously cancelled owned order; filled order rejects. Original receipt is sequence only; full effects are retrieved through delivery.

GetCommandReceipt: no receipt means no accepted command currently recorded in the healthy expected epoch; not a durable rejection, nor proof an earlier RPC cannot still execute. Never release a reservation solely on a missing receipt or transport timeout. Retry exactly the same immutable command ID and payload; do not manufacture another ID. Query original delivery record by sequence to verify payload if recovering a receipt. Backend needs durable command/outbox state and reconciliation, including a design for definitive rejection and preventing late retries after release.

GetDeliveryBatch: sequence begins at 1 for accepted commands, contiguous globally within epoch. Cursor is last fully processed command (0 at start). High watermark is a snapshot, not an acknowledgement. Book execution sequence is scoped by session+instrument and is distinct from command sequence. Execution UUIDv5 namespace is journal epoch; name is 8-byte big-endian command sequence concatenated with 8-byte big-endian zero-based fill index. Never recompute an identity from clock time. Process fills in recorded order, then subsequent cancellation effects. Checkpoint only after all effects of the command are durable; retries must be idempotent, compare original payload, and never skip gaps. Stable IDs do not by themselves guarantee exactly-once network delivery.

## Remaining implementation gates

1. Confirm ID formats, instrument catalogue, currency/tick rules and canonical schema ownership; no changes to D1 modules in this draft.
2. Validate V3 timestamp persistence/recovery in Windows; separately define any required market simulation timestamps.
3. Define precise error taxonomy: definitive pre-mutation domain rejection, conflicting command identity, wrong epoch, and ambiguous persistence/transport failure. A timeout is not a rejection. Existing rejected commands are not journaled. A poisoned engine must refuse normal operations until recovery. Define durable backend rejection/retry coordination and reservation release policy before wiring live orders.
4. Bound RPC sizes and single-command fanout. A page command-count cap does not limit bytes. Never silently truncate fills or advance the cursor over an incomplete command. Decide caps or explicit chunking before implementation. Depth/read API deferred to avoid unbounded replies; no streaming or public trading API in this slice.
5. Agree M05/M08 to M07/M06 ordering, reserve before submit; durable settlement checkpoint and cancellation release; test network interruption, duplicate commands and fills, crash/restart and ledger conservation in dedicated test DB. Define service authentication, deadlines and deployment. No production-ready claim.

## Examples and validation

Example scenario: book registration sequence 1, resting SELL 100 shares at $149 sequence 2, BUY 40 at $150 sequence 3 (executes 40 at resting $149), cancellation of remaining SELL sequence 4. Fixture order and delivery messages illustrate schema, not real trades or a running server. The timestamp is synthetic. Fixtures can be parsed by protoc/Python protobuf; semantic negative vectors and real cross-language tests remain future work.

References: https://protobuf.dev/programming-guides/proto3/ and https://protobuf.dev/programming-guides/json/.
