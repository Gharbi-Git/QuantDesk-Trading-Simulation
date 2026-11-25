#include "quantdesk/exchange/order.hpp"

#include <limits>
#include <stdexcept>
#include <utility>

namespace quantdesk::exchange {
namespace {
bool valid_id(const std::string& id) {
    // Opaque local IDs; canonical UUID and instrument validation belong at the adapter.
    return !id.empty() && id.size() <= 128 &&
        id.find_first_of(" \t\r\n\f\v") == std::string::npos &&
        id.find('\0') == std::string::npos;
}
}

Order::Order(LimitOrderRequest request) : request_(std::move(request)) {
    if (!valid_id(request_.order_id) || !valid_id(request_.account_id) ||
        !valid_id(request_.session_id) || !valid_id(request_.instrument_id))
        throw std::invalid_argument("Valid order, account, session and instrument IDs required");
    if (request_.side != Side::Buy && request_.side != Side::Sell)
        throw std::invalid_argument("Invalid order side");
    if (request_.limit_price <= 0 || request_.quantity <= 0)
        throw std::invalid_argument("Price and quantity must be positive");
    if (request_.quantity > std::numeric_limits<Price>::max() / request_.limit_price)
        throw std::overflow_error("Limit notional exceeds supported integer range");
}

bool Order::is_open() const noexcept {
    return state_ == OrderState::Accepted || state_ == OrderState::PartiallyFilled;
}

void Order::apply_fill(Quantity quantity, Price execution_price) {
    if (!is_open()) throw std::logic_error("Order is terminal");
    if (quantity <= 0 || quantity > remaining_quantity() || execution_price <= 0)
        throw std::invalid_argument("Invalid fill quantity or price");
    if ((request_.side == Side::Buy && execution_price > request_.limit_price) ||
        (request_.side == Side::Sell && execution_price < request_.limit_price))
        throw std::invalid_argument("Fill violates limit price");
    if (quantity > std::numeric_limits<Price>::max() / execution_price)
        throw std::overflow_error("Execution notional exceeds supported integer range");
    filled_ += quantity;
    state_ = remaining_quantity() == 0 ? OrderState::Filled : OrderState::PartiallyFilled;
}

void Order::cancel() {
    if (state_ == OrderState::Cancelled) return;
    if (!is_open()) throw std::logic_error("Filled order cannot be cancelled");
    state_ = OrderState::Cancelled;
}
} // namespace quantdesk::exchange
