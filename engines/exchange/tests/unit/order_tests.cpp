#include "quantdesk/exchange/order.hpp"
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string_view>

using namespace quantdesk::exchange;

namespace {
void check(bool condition) { if (!condition) throw std::runtime_error("Assertion failed"); }
template<class E, class F> void rejects(F f) {
    try { f(); } catch (const E&) { return; }
    throw std::runtime_error("Expected exception was not raised");
}
LimitOrderRequest sample(Side side = Side::Buy) {
    return {"order-1", "account-1", "session-1", "TEST-EQUITY", side, 150 * price_scale, 100};
}
void run(std::string_view name) {
    if (name == "valid_order") {
        Order o(sample());
        check(o.request().limit_price == 1'500'000 && o.request().quantity == 100);
        check(o.is_open() && o.filled_quantity() == 0 && o.open_quantity() == 100);
        check(o.state() == OrderState::Accepted);
    } else if (name == "invalid_request") {
        auto r = sample(); r.order_id.clear(); rejects<std::invalid_argument>([&]{ Order o(r); });
        r = sample(); r.session_id = " "; rejects<std::invalid_argument>([&]{ Order o(r); });
        r = sample(); r.account_id.clear(); rejects<std::invalid_argument>([&]{ Order o(r); });
        r = sample(); r.instrument_id.clear(); rejects<std::invalid_argument>([&]{ Order o(r); });
        r = sample(); r.side = static_cast<Side>(42); rejects<std::invalid_argument>([&]{ Order o(r); });
        r = sample(); r.limit_price = 0; rejects<std::invalid_argument>([&]{ Order o(r); });
        r = sample(); r.quantity = -1; rejects<std::invalid_argument>([&]{ Order o(r); });
    } else if (name == "notional_overflow") {
        auto r = sample(); r.limit_price = std::numeric_limits<Price>::max();
        rejects<std::overflow_error>([&]{ Order o(r); });
        r = sample(Side::Sell); Order o(r);
        rejects<std::overflow_error>([&]{ o.apply_fill(2, std::numeric_limits<Price>::max()); });
        check(o.filled_quantity() == 0 && o.state() == OrderState::Accepted);
    } else if (name == "partial_and_full_fill") {
        Order o(sample()); o.apply_fill(40, 145 * price_scale);
        check(o.filled_quantity() == 40 && o.remaining_quantity() == 60 && o.is_open());
        check(o.state() == OrderState::PartiallyFilled);
        o.apply_fill(60, 150 * price_scale);
        check(o.filled_quantity() == 100 && o.open_quantity() == 0 && !o.is_open());
        check(o.state() == OrderState::Filled);
    } else if (name == "buy_limit") {
        Order o(sample()); rejects<std::invalid_argument>([&]{ o.apply_fill(1, 151 * price_scale); });
        check(o.filled_quantity() == 0); o.apply_fill(1, 149 * price_scale); check(o.filled_quantity() == 1);
    } else if (name == "sell_limit") {
        Order o(sample(Side::Sell)); rejects<std::invalid_argument>([&]{ o.apply_fill(1, 149 * price_scale); });
        check(o.filled_quantity() == 0); o.apply_fill(1, 151 * price_scale); check(o.filled_quantity() == 1);
    } else if (name == "invalid_fill") {
        Order o(sample()); o.apply_fill(40, 145 * price_scale);
        rejects<std::invalid_argument>([&]{ o.apply_fill(61, 145 * price_scale); });
        rejects<std::invalid_argument>([&]{ o.apply_fill(0, 145 * price_scale); });
        rejects<std::invalid_argument>([&]{ o.apply_fill(1, 0); });
        check(o.filled_quantity() == 40 && o.open_quantity() == 60 && o.state() == OrderState::PartiallyFilled);
    } else if (name == "cancellation") {
        Order o(sample()); o.apply_fill(40, 145 * price_scale); o.cancel(); o.cancel();
        check(o.state() == OrderState::Cancelled && o.filled_quantity() == 40);
        check(o.remaining_quantity() == 60 && o.open_quantity() == 0 && !o.is_open());
        rejects<std::logic_error>([&]{ o.apply_fill(1, 145 * price_scale); });
        Order untouched(sample()); untouched.cancel(); check(untouched.open_quantity() == 0);
    } else if (name == "terminal_fill") {
        Order o(sample()); o.apply_fill(100, 150 * price_scale);
        rejects<std::logic_error>([&]{ o.apply_fill(1, 150 * price_scale); });
        rejects<std::logic_error>([&]{ o.cancel(); });
        check(o.state() == OrderState::Filled && o.filled_quantity() == 100);
    } else throw std::invalid_argument("Unknown test");
}
}
int main(int argc, char** argv) {
    if (argc != 2) return 2;
    try { run(argv[1]); return 0; }
    catch (const std::exception& e) { std::cerr << argv[1] << ": " << e.what() << '\n'; return 1; }
}
