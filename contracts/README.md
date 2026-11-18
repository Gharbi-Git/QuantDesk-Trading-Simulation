# Shared contracts (freeze before cross-module implementation)

Core contracts: MarketEvent, OrderRequest, ExecutionEvent, PortfolioSnapshot,
OptionContract and ArenaRuleset. Schemas belong in proto/ or json-schema/;
producer/consumer examples and failure vectors belong in fixtures/.

Agree IDs, units, currencies, UTC timestamps, money precision, event sequencing,
versioning, idempotency and errors. Do not invent fields in consumer implementations.