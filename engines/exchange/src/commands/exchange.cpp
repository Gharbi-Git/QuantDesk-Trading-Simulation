#include "quantdesk/exchange/exchange.hpp"
#include <stdexcept>
#include <utility>

namespace quantdesk::exchange {
void Exchange::register_book(const BookKey& key) {
    // OrderBook constructor validates IDs. Failed construction inserts nothing.
    books_.try_emplace(key, key.session_id, key.instrument_id);
}
OrderBook& Exchange::require_book(const BookKey& key) {
    const auto it = books_.find(key);
    if (it == books_.end()) throw std::invalid_argument("Unknown session/instrument book");
    return it->second;
}
const OrderBook& Exchange::require_book(const BookKey& key) const {
    const auto it = books_.find(key);
    if (it == books_.end()) throw std::invalid_argument("Unknown session/instrument book");
    return it->second;
}
SubmitResult Exchange::submit(LimitOrderRequest request) {
    auto& book = require_book({request.session_id, request.instrument_id});
    // Allocate the global index entry before matching. If the book rejects the
    // request, remove it so the rejected command has no lasting effect.
    auto [it, inserted] = order_index_.try_emplace(request.order_id, &book);
    if (!inserted) throw std::invalid_argument("Duplicate exchange order ID");
    try {
        return book.submit(std::move(request));
    } catch (...) {
        order_index_.erase(it);
        throw;
    }
}
const Order* Exchange::find(const BookKey& key, const std::string& id,
                            const std::string& account_id) const {
    const auto book = books_.find(key);
    if (book == books_.end()) return nullptr;
    const auto index = order_index_.find(id);
    if (index == order_index_.end() || index->second != &book->second) return nullptr;
    const auto* order = book->second.find(id);
    if (!order || order->request().account_id != account_id) return nullptr;
    return order;
}
void Exchange::cancel(const BookKey& key, const std::string& id, const std::string& account_id) {
    if (!find(key, id, account_id)) throw std::invalid_argument("Order not found for account and book");
    require_book(key).cancel(id, account_id);
}
std::vector<PriceLevel> Exchange::depth(const BookKey& key, Side side) const {
    return require_book(key).depth(side);
}
std::optional<Price> Exchange::best_bid(const BookKey& key) const { return require_book(key).best_bid(); }
std::optional<Price> Exchange::best_ask(const BookKey& key) const { return require_book(key).best_ask(); }
std::int64_t Exchange::last_execution_sequence(const BookKey& key) const {
    return require_book(key).last_execution_sequence();
}
}
