#pragma once
#include "quantdesk/exchange/order.hpp"
#include <deque>
#include <functional>
#include <map>
#include <optional>
#include <vector>

namespace quantdesk::exchange {

// Local sequence, not yet a durable execution ID. Scope: session + instrument.
struct Execution {
    std::int64_t sequence;
    std::string session_id, instrument_id;
    std::string buy_order_id, sell_order_id;
    std::string buyer_account_id, seller_account_id;
    Price price;
    Quantity quantity;
};
struct SubmitResult {
    OrderState state;
    Quantity filled_quantity;
    Quantity open_quantity;
    std::vector<Execution> executions;
};
struct PriceLevel {
    Price price;
    Quantity quantity;
    std::size_t order_count;
};

// Single writer, one book per session/instrument. No IO or financial mutations.
class OrderBook final {
public:
    OrderBook(std::string session_id, std::string instrument_id);
    OrderBook(const OrderBook&) = delete;
    OrderBook& operator=(const OrderBook&) = delete;
    OrderBook(OrderBook&&) = delete;
    OrderBook& operator=(OrderBook&&) = delete;

    // Duplicate IDs are rejected, even when the original order is terminal.
    // Any crossing self-trade rejects the entire incoming command before mutation.
    [[nodiscard]] SubmitResult submit(LimitOrderRequest request);
    // Trusted caller supplies the authorized account. Repeated cancel is harmless.
    void cancel(const std::string& order_id, const std::string& account_id);
    [[nodiscard]] const Order* find(const std::string& order_id) const;
    [[nodiscard]] std::optional<Price> best_bid() const;
    [[nodiscard]] std::optional<Price> best_ask() const;
    [[nodiscard]] std::vector<PriceLevel> depth(Side side) const;
    [[nodiscard]] std::int64_t last_execution_sequence() const noexcept { return last_sequence_; }

private:
    using Queue = std::deque<Order*>;
    using Bids = std::map<Price, Queue, std::greater<Price>>;
    using Asks = std::map<Price, Queue>;
    std::string session_id_, instrument_id_;
    // std::map keeps addresses stable during insertion. Terminal orders retained.
    std::map<std::string, Order> orders_;
    Bids bids_;
    Asks asks_;
    std::int64_t last_sequence_ = 0;
};
}
