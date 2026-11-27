#include "quantdesk/exchange/durable_exchange.hpp"
#include "quantdesk/exchange/execution_identity.hpp"
#include <iostream>
#include <chrono>
#include <fstream>
#include <iterator>
#include <string_view>
#include <limits>
#include <random>
#include <set>
#include <stdexcept>
using namespace quantdesk::exchange;
namespace {
const std::string epoch="12345678-1234-4abc-8def-1234567890ab", other="12345678-1234-4abc-9def-1234567890ab";
const BookKey a{"s1","ABC"}, b{"s2","ABC"};
void check(bool ok) { if(!ok) throw std::runtime_error("Assertion failed"); }
template<class E=std::exception, class F> void rejects(F f) { try { f(); } catch(const E&) { return; } throw std::runtime_error("Expected exception"); }
struct Temp {
    std::filesystem::path dir,path;
    Temp(){ std::random_device r; for(int i=0;i<20;++i){dir=std::filesystem::temp_directory_path()/("qd-timestamp-"+std::to_string(r())+"-"+std::to_string(r())); if(std::filesystem::create_directory(dir)){path=dir/"journal.log";return;}} throw std::runtime_error("Cannot create test directory"); }
    ~Temp(){std::error_code ec;std::filesystem::remove_all(dir,ec);}
};
LimitOrderRequest order(std::string id,std::string owner,Side side,Quantity qty=10, BookKey key=a){return {std::move(id),std::move(owner),std::move(key.session_id),std::move(key.instrument_id),side,1500000,qty};}
void populate(DurableExchange& e){
    (void)e.register_book("r1",a);(void)e.register_book("r2",b);
    (void)e.submit("s1",order("sell1","alice",Side::Sell,6));
    (void)e.submit("s2",order("sell2","bob",Side::Sell,8));
    (void)e.submit("buy",order("buy1","charlie",Side::Buy,10));
    (void)e.cancel("cancel",a,"sell2","bob");
}
constexpr auto v3=JournalFormat::TimestampedV3;
std::vector<DeliveredFill> fills(DurableExchange& e){return e.delivery_after(epoch,4,1).records.at(0).fills;}
std::string read(const std::filesystem::path& p){std::ifstream in(p,std::ios::binary);check(static_cast<bool>(in));return {std::istreambuf_iterator<char>(in),{}};}
void write(const std::filesystem::path& p,const std::string& s){std::ofstream out(p,std::ios::binary|std::ios::trunc);out.write(s.data(),static_cast<std::streamsize>(s.size()));out.close();check(static_cast<bool>(out));}
std::uint64_t get(std::string_view s,std::size_t off,unsigned n){std::uint64_t x=0;for(unsigned i=0;i<n;++i)x|=static_cast<std::uint64_t>(static_cast<unsigned char>(s.at(off+i)))<<(8*i);return x;}
void put(std::string& s,std::size_t off,std::uint64_t x,unsigned n){for(unsigned i=0;i<n;++i)s.at(off+i)=static_cast<char>((x>>(8*i))&255);}
std::uint32_t crc(std::string_view s){std::uint32_t c=0xffffffffu;for(char ch:s){c^=static_cast<unsigned char>(ch);for(int i=0;i<8;++i)c=(c>>1)^((c&1u)?0xedb88320u:0u);}return ~c;}
// Test-only manipulation of closed temporary journal; never edit real journals.
void set_timestamp(std::string& data,std::uint64_t timestamp){
    const std::string header="QD-JOURNAL-3\n"+epoch+"\n";check(data.starts_with(header));
    std::size_t off=header.size();for(int i=1;i<5;++i)off+=8+static_cast<std::size_t>(get(data,off,4));
    auto n=static_cast<std::size_t>(get(data,off,4));auto body=off+4;check(get(data,body,8)==5);
    put(data,body+8,timestamp,8);put(data,body+n,crc(std::string_view(data).substr(body,n)),4);
}
void multiple_fills(){Temp t;DurableExchange e(t.path,JournalMode::CreateNew,epoch,v3);populate(e);auto f=fills(e);check(f.size()==2&&f[0].execution_time_utc_unix_micros&&*f[0].execution_time_utc_unix_micros>0&&f[1].execution_time_utc_unix_micros==f[0].execution_time_utc_unix_micros);check(f[0].execution_id!=f[1].execution_id&&f[0].execution.quantity==6&&f[1].execution.quantity==4);}
void restart_retry(){Temp t;std::vector<DeliveredFill> orig;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v3);populate(e);orig=fills(e);check(orig.at(0).execution_time_utc_unix_micros.has_value());}
    auto data=read(t.path);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);check(e.submit("buy",order("buy1","charlie",Side::Buy,10)).command_sequence==5);auto f=fills(e);for(std::size_t i=0;i<orig.size();++i)check(f.at(i).execution_time_utc_unix_micros==orig[i].execution_time_utc_unix_micros&&f.at(i).execution_id==orig[i].execution_id);rejects<std::invalid_argument>([&]{(void)e.submit("buy",order("buy1","charlie",Side::Buy,9));});check(e.delivery_after(epoch,6).records.empty());}check(read(t.path)==data);}
void persisted_clock(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v3);populate(e);}auto data=read(t.path);constexpr std::int64_t known=946684800123456;set_timestamp(data,known);write(t.path,data);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);check(e.submit("buy",order("buy1","charlie",Side::Buy,10)).command_sequence==5);for(const auto& f:fills(e))check(f.execution_time_utc_unix_micros==known);}check(read(t.path)==data);}
void legacy_isolation(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);}auto original=read(t.path);rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);});check(read(t.path)==original);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch);for(const auto& f:fills(e))check(!f.execution_time_utc_unix_micros);}Temp u;{DurableExchange e(u.path,JournalMode::CreateNew,epoch,v3);populate(e);}auto new_data=read(u.path);rejects([&]{DurableExchange e(u.path,JournalMode::OpenExisting,epoch);});check(read(u.path)==new_data);}
void invalid_timestamp(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v3);populate(e);}auto original=read(t.path);for(auto bad:{0ULL,253402300800000000ULL,18446744073709551615ULL}){auto data=original;set_timestamp(data,bad);write(t.path,data);rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);});check(read(t.path)==data);}}
void failed_append(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v3);(void)e.register_book("r",a);}auto before=read(t.path);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3,before.size());rejects([&]{(void)e.submit("s",order("sell","alice",Side::Sell));});check(!e.healthy());rejects([&]{(void)e.delivery_after(epoch,1);});}check(read(t.path)==before);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);check(!e.receipt("s"));check(e.submit("s",order("sell","alice",Side::Sell)).command_sequence==2);}}
void torn_and_crc(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v3);populate(e);}auto original=read(t.path);auto data=original;data.pop_back();write(t.path,data);rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);});data=original;data.back()^=1;write(t.path,data);rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);});}
void reject_no_append(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v3);(void)e.register_book("r",a);(void)e.submit("s",order("sell","alice",Side::Sell));}auto original=read(t.path);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);rejects<std::invalid_argument>([&]{(void)e.submit("b",order("buy","alice",Side::Buy));});check(e.healthy()&&!e.receipt("b")&&e.delivery_after(epoch,2).records.empty());}check(read(t.path)==original);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v3);check(e.submit("b",order("buy","bob",Side::Buy)).command_sequence==3);check(e.delivery_after(epoch,2,1).records.at(0).fills.at(0).execution_time_utc_unix_micros.has_value());}}
}
int main(int argc,char** argv){const std::map<std::string,void(*)()> cases{{"multiple_fills",multiple_fills},{"restart_retry",restart_retry},{"persisted_clock",persisted_clock},{"legacy_isolation",legacy_isolation},{"invalid_timestamp",invalid_timestamp},{"failed_append",failed_append},{"torn_and_crc",torn_and_crc},{"reject_no_append",reject_no_append}};try{if(argc!=2)return 2;cases.at(argv[1])();return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
