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
    Temp(){ std::random_device r; for(int i=0;i<20;++i){dir=std::filesystem::temp_directory_path()/("qd-outcome-"+std::to_string(r())+"-"+std::to_string(r())); if(std::filesystem::create_directory(dir)){path=dir/"journal.log";return;}} throw std::runtime_error("Cannot create test directory"); }
    ~Temp(){std::error_code ec;std::filesystem::remove_all(dir,ec);}
};
LimitOrderRequest order(std::string id,std::string owner,Side side,Quantity qty=10, BookKey key=a){return {std::move(id),std::move(owner),std::move(key.session_id),std::move(key.instrument_id),side,1500000,qty};}
std::string read(const std::filesystem::path& p){std::ifstream in(p,std::ios::binary);check(static_cast<bool>(in));return {std::istreambuf_iterator<char>(in),{}};}
void write(const std::filesystem::path& p,const std::string& s){std::ofstream out(p,std::ios::binary|std::ios::trunc);out.write(s.data(),static_cast<std::streamsize>(s.size()));out.close();check(static_cast<bool>(out));}
std::uint64_t get(std::string_view s,std::size_t off,unsigned n){std::uint64_t x=0;for(unsigned i=0;i<n;++i)x|=static_cast<std::uint64_t>(static_cast<unsigned char>(s.at(off+i)))<<(8*i);return x;}
void put(std::string& s,std::size_t off,std::uint64_t x,unsigned n){for(unsigned i=0;i<n;++i)s.at(off+i)=static_cast<char>((x>>(8*i))&255);}
std::uint32_t crc(std::string_view s){std::uint32_t c=0xffffffffu;for(char ch:s){c^=static_cast<unsigned char>(ch);for(int i=0;i<8;++i)c=(c>>1)^((c&1u)?0xedb88320u:0u);}return ~c;}
constexpr auto v4=JournalFormat::OutcomesV4;
constexpr auto accepted=CommandOutcome::Accepted, rejected=CommandOutcome::DomainRejected, numeric=CommandOutcome::NumericRejected;
void retry(){Temp t;auto r=order("o","alice",Side::Buy);{
 DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);auto receipt=e.submit("s",r);check(receipt.outcome==rejected&&receipt.command_sequence==1&&!receipt.submission);(void)e.register_book("r",a);check(e.submit("s",r).outcome==rejected&&!e.view().find(a,"o","alice"));auto change=r;change.quantity=11;rejects<std::invalid_argument>([&]{(void)e.submit("s",change);});check(e.submit("new",r).outcome==accepted);}
 auto data=read(t.path);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v4);check(e.submit("s",r).command_sequence==1&&e.receipt("s")->outcome==rejected);auto page=e.delivery_after(epoch,0);check(page.high_watermark==3&&page.records.size()==3);const auto& d=page.records.at(0);check(d.kind==DeliveryKind::Submit&&d.outcome==rejected&&d.request&&d.request->quantity==r.quantity&&!d.state&&d.fills.empty());check(page.records.at(2).outcome==accepted&&e.view().find(a,"o","alice"));}check(read(t.path)==data);}
void atomic(){Temp t;DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);(void)e.register_book("r",a);(void)e.submit("s1",order("sell1","bob",Side::Sell,5));(void)e.submit("s2",order("sell2","alice",Side::Sell,5));check(e.submit("b",order("buy","alice",Side::Buy)).outcome==rejected);check(e.view().find(a,"sell1","bob")->open_quantity()==5&&e.view().find(a,"sell2","alice")->open_quantity()==5&&!e.view().find(a,"buy","alice")&&e.view().last_execution_sequence(a)==0);auto r=e.submit("good",order("buy2","charlie",Side::Buy));check(r.outcome==accepted&&r.submission->executions.size()==2);auto page=e.delivery_after(epoch,3,2);check(page.records.at(0).outcome==rejected&&page.records.at(0).fills.empty());check(page.records.at(1).fills.at(0).execution_time_utc_unix_micros.has_value()&&page.records.at(1).fills.at(0).execution_id==execution_uuid(epoch,5,0));}
void overflow(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);(void)e.register_book("r",a);auto r=order("huge","alice",Side::Buy,std::numeric_limits<Quantity>::max());r.limit_price=1;check(e.submit("huge",r).outcome==accepted);auto n=order("extra","bob",Side::Buy,1);n.limit_price=1;check(e.submit("extra",n).outcome==numeric);check(!e.view().find(a,"extra","bob")&&e.view().depth(a,Side::Buy).at(0).quantity==r.quantity);}DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v4);check(e.receipt("extra")->outcome==numeric);}
void cancel(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);(void)e.register_book("r",a);(void)e.submit("s",order("sell","alice",Side::Sell));check(e.cancel("bad",a,"sell","bob").outcome==rejected);check(e.view().find(a,"sell","alice")->is_open());check(e.cancel("good",a,"sell","alice").outcome==accepted);rejects<std::invalid_argument>([&]{(void)e.cancel("bad",a,"sell","alice");});}DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v4);auto page=e.delivery_after(epoch,2);check(page.records.at(0).outcome==rejected&&page.records.at(0).kind==DeliveryKind::Cancel&&page.records.at(0).account_id=="bob"&&page.records.at(1).outcome==accepted);}
void failed_append(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);}auto data=read(t.path);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v4,data.size());rejects<std::runtime_error>([&]{(void)e.submit("s",order("o","alice",Side::Buy));});check(!e.healthy());rejects([&]{(void)e.receipt("s");});}check(read(t.path)==data);{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v4);check(!e.receipt("s")&&e.submit("s",order("o","alice",Side::Buy)).outcome==rejected);}}
void isolation(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);(void)e.submit("s",order("o","alice",Side::Buy));}auto data=read(t.path);rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,JournalFormat::TimestampedV3);});rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,other,v4);});check(read(t.path)==data);Temp u;{DurableExchange e(u.path,JournalMode::CreateNew,epoch,JournalFormat::TimestampedV3);rejects<std::invalid_argument>([&]{(void)e.submit("s",order("o","alice",Side::Buy));});check(!e.receipt("s"));}auto old=read(u.path);rejects([&]{DurableExchange e(u.path,JournalMode::OpenExisting,epoch,v4);});check(read(u.path)==old);}
void corruption(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);(void)e.submit("s",order("o","alice",Side::Buy));}auto orig=read(t.path);const auto off=std::string("QD-JOURNAL-4\n"+epoch+"\n").size();const auto n=static_cast<std::size_t>(get(orig,off,4)),body=off+4;check(get(orig,body+16,1)==1);for(auto bad:{0ULL,2ULL,3ULL}){auto data=orig;put(data,body+16,bad,1);put(data,body+n,crc(std::string_view(data).substr(body,n)),4);write(t.path,data);rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v4);});check(read(t.path)==data);}auto torn=orig;torn.pop_back();write(t.path,torn);rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch,v4);});}
void malformed(){Temp t;DurableExchange e(t.path,JournalMode::CreateNew,epoch,v4);auto r=order("o","alice",Side::Buy,0);rejects<std::invalid_argument>([&]{(void)e.submit("s",r);});check(!e.receipt("s")&&e.delivery_after(epoch,0).high_watermark==0&&e.healthy());r.quantity=10;check(e.submit("s",r).outcome==rejected);}
}
int main(int argc,char** argv){const std::map<std::string,void(*)()> cases{{"retry",retry},{"atomic",atomic},{"overflow",overflow},{"cancel",cancel},{"failed_append",failed_append},{"isolation",isolation},{"corruption",corruption},{"malformed",malformed}};try{if(argc!=2)return 2;cases.at(argv[1])();return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
