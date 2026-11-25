#include "quantdesk/exchange/order_book.hpp"
#include <algorithm>
#include <limits>
#include <stdexcept>
#include <utility>

namespace quantdesk::exchange {
namespace {
constexpr auto max_quantity = std::numeric_limits<Quantity>::max();

template<class Queue> Quantity level_quantity(const Queue& queue) {
    Quantity total = 0;
    for (const auto* order : queue) {
        if (order->open_quantity() > max_quantity - total)
            throw std::overflow_error("Price level quantity overflow");
        total += order->open_quantity();
    }
    return total;
}

template<class Levels> void append(Levels& levels, Order& order, Quantity remainder) {
    const auto price = order.request().limit_price;
    auto [it, inserted] = levels.try_emplace(price);
    try {
        if (remainder > max_quantity - level_quantity(it->second))
            throw std::overflow_error("Price level quantity overflow");
        it->second.push_back(&order);
    } catch (...) {
        if (inserted) levels.erase(it);
        throw;
    }
}

template<class Levels> void remove_front(Levels& levels, const Order& maker) {
    auto it = levels.find(maker.request().limit_price);
    it->second.pop_front();
    if (it->second.empty()) levels.erase(it);
}

template<class Levels> void remove_cancelled(Levels& levels, Order& order) {
    auto it = levels.find(order.request().limit_price);
    if (it == levels.end()) throw std::logic_error("Missing price level");
    auto pos = std::find(it->second.begin(), it->second.end(), &order);
    if (pos == it->second.end()) throw std::logic_error("Missing resting order");
    order.cancel();
    it->second.erase(pos);
    if (it->second.empty()) levels.erase(it);
}

template<class Levels> std::vector<PriceLevel> make_depth(const Levels& levels) {
    std::vector<PriceLevel> result;
    result.reserve(levels.size());
    for (const auto& [price, queue] : levels)
        result.push_back({price, level_quantity(queue), queue.size()});
    return result;
}
}

OrderBook::OrderBook(std::string session_id, std::string instrument_id)
    : session_id_(std::move(session_id)), instrument_id_(std::move(instrument_id)) {
    // Reuse the existing domain validation for opaque IDs.
    (void)Order({"validation", "validation", session_id_, instrument_id_, Side::Buy, 1, 1});
}

SubmitResult OrderBook::submit(LimitOrderRequest request) {
    Order incoming(std::move(request));
    const auto& r = incoming.request();
    if (r.session_id != session_id_ || r.instrument_id != instrument_id_)
        throw std::invalid_argument("Order belongs to a different book");
    if (orders_.contains(r.order_id)) throw std::invalid_argument("Duplicate order ID");

    // Preflight the entire match before changing either side. All strings and
    // execution records are allocated here. A self-cross or overflow leaves
    // the book and sequence unchanged, even after earlier compatible makers.
    std::vector<Order*> makers;
    std::vector<Execution> executions;
    Quantity remainder = r.quantity;
    auto sequence = last_sequence_;
    const auto plan = [&](const auto& levels) {
        for (const auto& [price, queue] : levels) {
            if (remainder == 0 || (r.side == Side::Buy ? price > r.limit_price : price < r.limit_price)) break;
            for (auto* maker : queue) {
                if (remainder == 0) break;
                const auto& m = maker->request();
                if (r.account_id == m.account_id) throw std::invalid_argument("Self-trade prevented");
                const auto qty = std::min(remainder, maker->open_quantity());
                if (sequence == std::numeric_limits<std::int64_t>::max())
                    throw std::overflow_error("Execution sequence exhausted");
                // Existing Order construction bounds both sides' notional.
                // Resting price respects both limits; buy limit bounds fill notional.
                const auto& buy = r.side == Side::Buy ? r : m;
                const auto& sell = r.side == Side::Sell ? r : m;
                makers.push_back(maker);
                executions.push_back({++sequence, session_id_, instrument_id_,
                    buy.order_id, sell.order_id, buy.account_id, sell.account_id, price, qty});
                remainder -= qty;
            }
        }
    };
    if (r.side == Side::Buy) plan(asks_); else plan(bids_);

    // Allocate the order and any resting queue entry before applying fills.
    // The same-side queue never overlaps the planned opposite-side makers.
    const auto id = r.order_id;
    const auto side = r.side;
    auto [it, inserted] = orders_.try_emplace(id, std::move(incoming));
    if (!inserted) throw std::logic_error("Order ID unexpectedly exists");
    auto& taker = it->second;
    try {
        if (remainder > 0) {
            if (side == Side::Buy) append(bids_, taker, remainder);
            else append(asks_, taker, remainder);
        }
    } catch (...) {
        orders_.erase(it);
        throw;
    }
    // No allocations required in this commit phase. All fills have been
    // validated by the plan and the existing order invariants.
    for (std::size_t i = 0; i < executions.size(); ++i) {
        auto& maker = *makers[i];
        const auto& e = executions[i];
        maker.apply_fill(e.quantity, e.price);
        taker.apply_fill(e.quantity, e.price);
        if (!maker.is_open()) {
            if (side == Side::Buy) remove_front(asks_, maker);
            else remove_front(bids_, maker);
        }
    }
    last_sequence_ = sequence;
    return {taker.state(), taker.filled_quantity(), taker.open_quantity(), std::move(executions)};
}

void OrderBook::cancel(const std::string& order_id, const std::string& account_id) {
    auto it = orders_.find(order_id);
    if (it == orders_.end() || it->second.request().account_id != account_id)
        throw std::invalid_argument("Order not found for account");
    auto& order = it->second;
    if (order.state() == OrderState::Cancelled) return;
    if (!order.is_open()) throw std::logic_error("Filled order cannot be cancelled");
    if (order.request().side == Side::Buy) remove_cancelled(bids_, order);
    else remove_cancelled(asks_, order);
}
const Order* OrderBook::find(const std::string& id) const {
    const auto it = orders_.find(id);
    return it == orders_.end() ? nullptr : &it->second;
}
std::optional<Price> OrderBook::best_bid() const {
    if (bids_.empty()) return std::nullopt;
    return bids_.begin()->first;
}
std::optional<Price> OrderBook::best_ask() const {
    if (asks_.empty()) return std::nullopt;
    return asks_.begin()->first;
}
std::vector<PriceLevel> OrderBook::depth(Side side) const {
    if (side == Side::Buy) return make_depth(bids_);
    if (side == Side::Sell) return make_depth(asks_);
    throw std::invalid_argument("Invalid side");
}
}
