#pragma once
#include "quantdesk/exchange/exchange.hpp"
#include <filesystem>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace quantdesk::exchange {
// Durable receipt; delivery IDs are derived separately from the bound epoch.
struct CommandReceipt {
    std::int64_t command_sequence;
    std::optional<SubmitResult> submission;
};
enum class JournalMode { CreateNew, OpenExisting };
enum class DeliveryKind { RegisterBook, Submit, Cancel };
struct DeliveredFill {
    std::string execution_id;
    Execution execution;
};
struct DeliveryRecord {
    std::int64_t command_sequence;
    std::string command_id;
    DeliveryKind kind;
    BookKey key;
    std::string order_id, account_id;
    std::optional<LimitOrderRequest> request; // only Submit
    std::optional<OrderState> state; // only Submit
    Quantity filled_quantity, open_quantity; // only Submit
    std::vector<DeliveredFill> fills;
};
struct DeliveryBatch {
    std::string journal_epoch;
    std::int64_t high_watermark;
    std::vector<DeliveryRecord> records;
};
// One writer, exclusive OS file lock. All calls must be externally serialized.
// A persistence/unknown internal failure poisons this instance until restart.
class DurableExchange final {
public:
    DurableExchange(const std::filesystem::path& path, JournalMode mode,
                    std::size_t byte_limit = 64 * 1024 * 1024);
    // Version 2: binds an externally provisioned UUIDv4 to the journal header.
    // Both CreateNew and OpenExisting must supply the same epoch.
    DurableExchange(const std::filesystem::path& path, JournalMode mode,
                    const std::string& journal_epoch, std::size_t byte_limit = 64 * 1024 * 1024);
    ~DurableExchange();
    DurableExchange(const DurableExchange&) = delete;
    DurableExchange& operator=(const DurableExchange&) = delete;
    [[nodiscard]] CommandReceipt register_book(const std::string& command_id, const BookKey& key);
    [[nodiscard]] CommandReceipt submit(const std::string& command_id, const LimitOrderRequest& request);
    [[nodiscard]] CommandReceipt cancel(const std::string& command_id, const BookKey& key,
                                         const std::string& order_id, const std::string& account_id);
    [[nodiscard]] std::optional<CommandReceipt> receipt(const std::string& command_id) const;
    // Pull committed commands after a consumer-owned durable cursor (0 = start).
    // Non-mutating; replay returns the same IDs. V1 journals cannot deliver.
    [[nodiscard]] DeliveryBatch delivery_after(const std::string& expected_epoch,
                                               std::int64_t after_sequence,
                                               std::size_t max_commands = 64) const;
    [[nodiscard]] const Exchange& view() const; // borrowed, never use across commands or failure
    [[nodiscard]] bool healthy() const noexcept;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
