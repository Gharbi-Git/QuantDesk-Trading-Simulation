#pragma once
#include "quantdesk/exchange/durable_exchange.hpp"
#include <mutex>
#include <stdexcept>
#include <string_view>
#include <utility>
namespace qd_transport {
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
// Owns the sole engine instance, explicitly V3. One lock covers every read/write.
// Dev lifetime ceiling bounds worst-case matching fanout and retained state.
class Adapter final {
public:
    static constexpr std::int64_t max_commands=512;
    Adapter(const std::filesystem::path& file,d::JournalMode mode,std::string epoch)
        : epoch_(std::move(epoch)), engine_(file,mode,epoch_,d::JournalFormat::TimestampedV3,1024*1024) {
        auto s=status();
        if(s.high>max_commands) throw Failure(Fault::Capacity,"Journal exceeds dev command ceiling");
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
    std::string epoch_; d::DurableExchange engine_; std::mutex mu_;
    void check_epoch(const std::string& epoch) {
        if(!engine_.healthy()) throw Failure(Fault::Unavailable,"Journal requires recovery");
        if(epoch!=epoch_) throw Failure(Fault::WrongEpoch,"Journal epoch mismatch");
    }
    void check(const Identity& i) { check_epoch(i.epoch); uuid(i.command); }
    std::int64_t high() { check_epoch(epoch_); return engine_.delivery_after(epoch_,0,1).high_watermark; }
    template<class F> d::CommandReceipt mutate(const Identity& i,F&& f) {
        const bool known=engine_.receipt(i.command).has_value();
        if(!known && high()>=max_commands) throw Failure(Fault::Capacity,"Dev journal command ceiling reached");
        try { return f(); }
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
