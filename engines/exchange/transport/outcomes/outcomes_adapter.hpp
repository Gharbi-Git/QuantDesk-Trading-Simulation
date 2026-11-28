#pragma once
#include "quantdesk/exchange/durable_exchange.hpp"
#include <mutex>
#include <iostream>
#include <charconv>
#include <stdexcept>
#include <string_view>
#include <utility>
namespace qd_outcomes {
namespace d = quantdesk::exchange;
enum class Fault { Invalid, WrongEpoch, Rejected, Conflict, Capacity, Unavailable };
struct Failure : std::runtime_error {
    Fault code;
    Failure(Fault c, const char* s) : std::runtime_error(s), code(c) {}
};
inline void uuid(std::string_view s) {
    if (s.size()!=36) throw Failure(Fault::Invalid,"Expected canonical nonzero UUID");
    bool nonzero=false;
    for (std::size_t i=0;i<s.size();++i) {
        const char c=s[i];
        if (i==8||i==13||i==18||i==23) { if(c!='-') throw Failure(Fault::Invalid,"Invalid UUID"); }
        else { if (!((c>='0'&&c<='9')||(c>='a'&&c<='f'))) throw Failure(Fault::Invalid,"Invalid UUID"); nonzero |= c!='0'; }
    }
    if (!nonzero) throw Failure(Fault::Invalid,"Nil UUID not allowed");
}
inline void book(const d::BookKey& b) {
    uuid(b.session_id);
    // Development restriction, not a market-data catalogue or tick-size check.
    if(b.instrument_id.empty()||b.instrument_id.size()>128) throw Failure(Fault::Invalid,"Invalid instrument length");
    for(unsigned char c:b.instrument_id) if(c<33||c>126) throw Failure(Fault::Invalid,"Instrument must be printable ASCII without spaces in this dev slice");
}
struct Identity { std::string epoch, command; };
struct Status { std::string epoch; std::int64_t high; };
// Owns the sole engine instance, explicitly V4. One lock covers every read/write.
// Bounded lifetime ceiling protects the unchanged 1 MiB wire contract.
// Every fill exhausts at least one order, so total fills <= total submissions.
// Conservative protobuf bounds: 448 bytes/fill, 384 bytes/non-fill record,
// 128 bytes/page envelope. Includes nested length prefixes.
inline constexpr std::int64_t default_capacity=512;
inline constexpr std::int64_t hard_capacity=2048;
static_assert(hard_capacity*448+256*384+128 < 1024*1024);
inline std::int64_t parse_capacity(std::string_view arg) {
    constexpr std::string_view prefix="--max-commands=";
    if(!arg.starts_with(prefix)) throw std::invalid_argument("Expected --max-commands=N");
    arg.remove_prefix(prefix.size());
    std::int64_t n=0;
    const auto [end,err]=std::from_chars(arg.data(),arg.data()+arg.size(),n);
    if(err!=std::errc{}||end!=arg.data()+arg.size()||n<default_capacity||n>hard_capacity)
        throw std::invalid_argument("Command capacity must be 512..2048");
    return n;
}
class Adapter final {
public:
    Adapter(const std::filesystem::path& file,d::JournalMode mode,std::string epoch,
            std::int64_t capacity=default_capacity)
        : epoch_(std::move(epoch)), capacity_(checked_capacity(capacity)),
          engine_(file,mode,epoch_,d::JournalFormat::OutcomesV4, byte_budget(capacity_)) {
        auto s=status();
        if(s.high>capacity_) throw Failure(Fault::Capacity,"Journal exceeds configured command ceiling");
        report_capacity(s.high);
        // Reject history outside the dev contract; never silently rewrite old IDs.
        for(std::int64_t cursor=0;cursor<s.high;) {
            auto page=delivery(epoch_,cursor,64);
            for(const auto& r:page.records) {
                uuid(r.command_id); book(r.key);
                if(r.kind!=d::DeliveryKind::RegisterBook) { uuid(r.order_id); uuid(r.account_id); }
                for(const auto& f:r.fills) if(!f.execution_time_utc_unix_micros) throw Failure(Fault::Unavailable,"Missing timestamp");
                cursor=r.command_sequence;
            }
        }
    }
    Status status() { std::scoped_lock lock(mu_); return {epoch_,high()}; }
    d::CommandReceipt register_book(const Identity& i,const d::BookKey& b) {
        std::scoped_lock lock(mu_); check(i); book(b);
        return mutate(i,[&]{return engine_.register_book(i.command,b);});
    }
    d::CommandReceipt submit(const Identity& i,const d::LimitOrderRequest& r) {
        std::scoped_lock lock(mu_); check(i); book({r.session_id,r.instrument_id}); uuid(r.order_id); uuid(r.account_id);
        return mutate(i,[&]{return engine_.submit(i.command,r);});
    }
    d::CommandReceipt cancel(const Identity& i,const d::BookKey& b,const std::string& order,const std::string& account) {
        std::scoped_lock lock(mu_); check(i); book(b); uuid(order); uuid(account);
        return mutate(i,[&]{return engine_.cancel(i.command,b,order,account);});
    }
    std::optional<d::CommandReceipt> receipt(const Identity& i) {
        std::scoped_lock lock(mu_); check(i); return engine_.receipt(i.command);
    }
    d::DeliveryBatch delivery(const std::string& epoch,std::int64_t after,std::size_t count) {
        std::scoped_lock lock(mu_); check_epoch(epoch);
        if(after<0||after>high()||count<1||count>256) throw Failure(Fault::Invalid,"Invalid cursor or page size");
        return engine_.delivery_after(epoch_,after,count);
    }
private:
    std::string epoch_; const std::int64_t capacity_; d::DurableExchange engine_; std::mutex mu_;
    static std::int64_t checked_capacity(std::int64_t n) {
        if(n<default_capacity||n>hard_capacity) throw std::invalid_argument("Command capacity must be 512..2048");
        return n;
    }
    // Core V4 max frame = 2048 body + 8 framing bytes; header < 128.
    static std::size_t byte_budget(std::int64_t n) { return 128+static_cast<std::size_t>(n)*2056; }
    void report_capacity(std::int64_t n) const noexcept {
        try { std::clog << "V4 journal commands=" << n << "/" << capacity_
                       << "; configured byte ceiling=" << byte_budget(capacity_) << '\n'; } catch(...) {}
    }
    void check_epoch(const std::string& epoch) {
        if(!engine_.healthy()) throw Failure(Fault::Unavailable,"Journal requires recovery");
        if(epoch!=epoch_) throw Failure(Fault::WrongEpoch,"Journal epoch mismatch");
    }
    void check(const Identity& i) { check_epoch(i.epoch); uuid(i.command); }
    std::int64_t high() { check_epoch(epoch_); return engine_.delivery_after(epoch_,0,1).high_watermark; }
    template<class F> d::CommandReceipt mutate(const Identity& i,F&& f) {
        const bool known=engine_.receipt(i.command).has_value();
        if(!known && high()>=capacity_) throw Failure(Fault::Capacity,"Configured journal command ceiling reached; outcome not recorded");
        try {
            auto result=f();
            if(!known && (result.command_sequence==capacity_*3/4 || result.command_sequence==capacity_*9/10 || result.command_sequence==capacity_)) report_capacity(result.command_sequence);
            return result;
        }
        catch(const std::invalid_argument&) {
            if(!engine_.healthy()) throw Failure(Fault::Unavailable,"Journal requires recovery");
            throw Failure(known?Fault::Conflict:Fault::Rejected,known?"Existing command differs or payload invalid":"Domain rejected command");
        } catch(const std::overflow_error&) {
            if(!engine_.healthy()) throw Failure(Fault::Unavailable,"Journal requires recovery");
            throw Failure(known?Fault::Conflict:Fault::Rejected,"Numeric limit exceeded");
        }
    }
};
}
