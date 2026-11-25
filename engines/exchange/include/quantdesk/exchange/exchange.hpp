#pragma once

#include "quantdesk/exchange/order_book.hpp"
#include <map>
#include <string>
#include <utility>
#include <tuple>

namespace quantdesk::exchange {

struct BookKey {
    std::string session_id;
    std::string instrument_id;
    bool operator<(const BookKey& other) const noexcept {
        return std::tie(session_id, instrument_id) < std::tie(other.session_id, other.instrument_id);
    }
};

// Trusted, single-writer in-memory router. The caller authenticates accounts,
// authorizes sessions and validates instrument metadata before calling this API.
// No book removal or ID reuse in this process lifetime.
class Exchange final {
public:
    Exchange() = default;
    Exchange(const Exchange&) = delete;
    Exchange& operator=(const Exchange&) = delete;
    Exchange(Exchange&&) = delete;
    Exchange& operator=(Exchange&&) = delete;

    // Explicit registration is required; unknown books never auto-create.
    // Re-registering an existing key is a harmless no-op.
    void register_book(const BookKey& key);
    [[nodiscard]] std::size_t book_count() const noexcept { return books_.size(); }
    [[nodiscard]] std::size_t order_count() const noexcept { return order_index_.size(); }
    [[nodiscard]] SubmitResult submit(LimitOrderRequest request);
    void cancel(const BookKey& key, const std::string& order_id, const std::string& account_id);
    // Null for a missing order or incorrect book/account. Read-only pointer;
    // valid until Exchange destruction, not a cross-thread snapshot.
    [[nodiscard]] const Order* find(const BookKey& key, const std::string& order_id,
                                    const std::string& account_id) const;
    [[nodiscard]] std::vector<PriceLevel> depth(const BookKey& key, Side side) const;
    [[nodiscard]] std::optional<Price> best_bid(const BookKey& key) const;
    [[nodiscard]] std::optional<Price> best_ask(const BookKey& key) const;
    [[nodiscard]] std::int64_t last_execution_sequence(const BookKey& key) const;

private:
    OrderBook& require_book(const BookKey& key);
    const OrderBook& require_book(const BookKey& key) const;
    // std::map nodes keep addresses stable; no book can be erased or moved.
    std::map<BookKey, OrderBook> books_;
    std::map<std::string, const OrderBook*> order_index_;
};
}
