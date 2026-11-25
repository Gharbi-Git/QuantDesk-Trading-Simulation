#pragma once

#include <cstdint>
#include <string>

namespace quantdesk::exchange {

// One unit = 0.0001 USD. Never pass a floating-point price here.
using Price = std::int64_t;
using Quantity = std::int64_t;
inline constexpr Price price_scale = 10'000;

enum class Side { Buy, Sell };
enum class OrderState { Accepted, PartiallyFilled, Filled, Cancelled };

struct LimitOrderRequest {
    std::string order_id;
    std::string account_id;
    std::string session_id;
    std::string instrument_id;
    Side side;
    Price limit_price;
    Quantity quantity;
};

// In-memory order lifecycle only. Financial reservations belong to M07.
class Order final {
public:
    explicit Order(LimitOrderRequest request);

    [[nodiscard]] const LimitOrderRequest& request() const noexcept { return request_; }
    [[nodiscard]] Quantity filled_quantity() const noexcept { return filled_; }
    // Unexecuted quantity is retained after cancellation for audit purposes.
    [[nodiscard]] Quantity remaining_quantity() const noexcept { return request_.quantity - filled_; }
    [[nodiscard]] OrderState state() const noexcept { return state_; }
    [[nodiscard]] bool is_open() const noexcept;
    [[nodiscard]] Quantity open_quantity() const noexcept { return is_open() ? remaining_quantity() : 0; }

    void apply_fill(Quantity quantity, Price execution_price);
    // Repeated cancellation is harmless; a fully filled order cannot be cancelled.
    void cancel();

private:
    LimitOrderRequest request_;
    Quantity filled_ = 0;
    OrderState state_ = OrderState::Accepted;
};

} // namespace quantdesk::exchange
