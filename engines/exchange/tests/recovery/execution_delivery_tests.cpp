#include "quantdesk/exchange/durable_exchange.hpp"
#include "quantdesk/exchange/execution_identity.hpp"
#include <iostream>
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
    Temp(){ std::random_device r; for(int i=0;i<20;++i){dir=std::filesystem::temp_directory_path()/("qd-delivery-"+std::to_string(r())+"-"+std::to_string(r())); if(std::filesystem::create_directory(dir)){path=dir/"journal.log";return;}} throw std::runtime_error("Cannot create test directory"); }
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
void uuid_vectors(){
    check(execution_uuid(epoch,1,0)=="3eeee0c9-626e-512b-abc9-286a9bf7e276");
    check(execution_uuid(epoch,5,0)=="eb095432-507f-5c05-9936-06649e996efd");
    check(execution_uuid(epoch,5,1)=="9a04bb34-3295-5864-943e-12221188a4da");
    check(execution_uuid(epoch,std::numeric_limits<std::int64_t>::max(),std::numeric_limits<std::uint64_t>::max())=="0d8bb8ce-7875-5158-ac85-07daaa6c7753");
    rejects<std::invalid_argument>([]{(void)execution_uuid(epoch,0,0);});
    rejects<std::invalid_argument>([]{validate_journal_epoch("bad");});
}
void ordered_page(){Temp t; DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);
    auto p=e.delivery_after(epoch,0,2);check(p.high_watermark==6&&p.journal_epoch==epoch&&p.records.size()==2&&p.records[0].kind==DeliveryKind::RegisterBook);
    p=e.delivery_after(epoch,2,2);check(p.records.size()==2&&p.records[0].command_sequence==3&&p.records[1].command_sequence==4&&p.records[0].fills.empty());
    p=e.delivery_after(epoch,4,2);check(p.records.size()==2&&p.records[0].command_sequence==5&&p.records[1].command_sequence==6);
    check(e.delivery_after(epoch,6).records.empty());
}
void fill_payload(){Temp t; DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);auto p=e.delivery_after(epoch,4,1);const auto& r=p.records.at(0);
    check(r.command_id=="buy"&&r.kind==DeliveryKind::Submit&&r.key.session_id=="s1"&&r.key.instrument_id=="ABC"&&r.account_id=="charlie"&&r.order_id=="buy1");
    check(r.request&&r.request->quantity==10&&r.state==OrderState::Filled&&r.filled_quantity==10&&r.open_quantity==0&&r.fills.size()==2);
    const auto& f=r.fills[0];check(f.execution_id==execution_uuid(epoch,5,0)&&f.execution.buy_order_id=="buy1"&&f.execution.sell_order_id=="sell1"&&f.execution.price==1500000&&f.execution.quantity==6&&f.execution.buyer_account_id=="charlie"&&f.execution.seller_account_id=="alice");
    check(r.fills[1].execution.quantity==4&&r.fills[1].execution.sequence==2&&r.fills[0].execution_id!=r.fills[1].execution_id);
}
void restart_retry(){Temp t;std::vector<std::string> ids;
    {DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);auto p=e.delivery_after(epoch,4);for(const auto& f:p.records[0].fills)ids.push_back(f.execution_id);}
    {DurableExchange e(t.path,JournalMode::OpenExisting,epoch);auto p=e.delivery_after(epoch,4);check(p.high_watermark==6&&p.records.size()==2);for(std::size_t i=0;i<ids.size();++i)check(ids[i]==p.records[0].fills.at(i).execution_id);
    check(e.submit("buy",order("buy1","charlie",Side::Buy,10)).command_sequence==5);check(e.delivery_after(epoch,6).records.empty());}
}
void cancel_payload(){Temp t;DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);const auto p=e.delivery_after(epoch,5);const auto& r=p.records.at(0);
    check(r.kind==DeliveryKind::Cancel&&r.command_id=="cancel"&&r.order_id=="sell2"&&r.account_id=="bob"&&r.key.session_id=="s1"&&r.key.instrument_id=="ABC"&&!r.request&&!r.state&&r.fills.empty());
}
void cursor_errors(){Temp t;DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);
    rejects<std::invalid_argument>([&]{(void)e.delivery_after(epoch,-1);});rejects<std::invalid_argument>([&]{(void)e.delivery_after(epoch,7);});rejects<std::invalid_argument>([&]{(void)e.delivery_after(epoch,0,0);});rejects<std::invalid_argument>([&]{(void)e.delivery_after(epoch,0,257);});rejects<std::invalid_argument>([&]{(void)e.delivery_after(other,0);});check(e.healthy());
}
void epoch_binding(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);}
    rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,other);});rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting);});
    {DurableExchange e(t.path,JournalMode::OpenExisting,epoch);check(e.delivery_after(epoch,0).records.size()==6);}
    Temp t2;rejects<std::invalid_argument>([&]{DurableExchange e(t2.path,JournalMode::CreateNew,std::string{});});check(!std::filesystem::exists(t2.path));
}
void legacy(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew);(void)e.register_book("r",a);rejects<std::invalid_argument>([&]{(void)e.delivery_after(epoch,0);});}
    rejects([&]{DurableExchange e(t.path,JournalMode::OpenExisting,epoch);});DurableExchange e(t.path,JournalMode::OpenExisting);check(e.receipt("r").has_value());
}
void independent_ids(){Temp t1,t2;DurableExchange e(t1.path,JournalMode::CreateNew,epoch),f(t2.path,JournalMode::CreateNew,other);populate(e);populate(f);
    auto x=e.delivery_after(epoch,4),y=f.delivery_after(other,4);check(x.records[0].fills[0].execution_id!=y.records[0].fills[0].execution_id);
    (void)e.submit("bs",order("bs","alice",Side::Sell,1,b));(void)e.submit("bb",order("bb","bob",Side::Buy,1,b));
    auto z=e.delivery_after(epoch,7);check(z.records[0].fills[0].execution.sequence==1&&z.records[0].fills[0].execution_id!=x.records[0].fills[0].execution_id);
}
void failed_write(){Temp t;{DurableExchange e(t.path,JournalMode::CreateNew,epoch,100);(void)e.register_book("r",a);rejects([&]{(void)e.submit("s",order("sell","alice",Side::Sell));});check(!e.healthy());rejects([&]{(void)e.delivery_after(epoch,0);});}
    {DurableExchange e(t.path,JournalMode::OpenExisting,epoch);auto p=e.delivery_after(epoch,0);check(p.high_watermark==1&&p.records.size()==1);}
}
void consumer_restart(){Temp t;std::set<std::string> applied;std::int64_t cursor=4;int settled=0;
    {DurableExchange e(t.path,JournalMode::CreateNew,epoch);populate(e);auto p=e.delivery_after(epoch,cursor,1);check(p.records.size()==1);check(applied.insert(p.records[0].fills[0].execution_id).second);++settled;}
    {DurableExchange e(t.path,JournalMode::OpenExisting,epoch);auto p=e.delivery_after(epoch,cursor,1);for(const auto& f:p.records[0].fills)if(applied.insert(f.execution_id).second)++settled;cursor=p.records[0].command_sequence;check(settled==2&&cursor==5);check(e.delivery_after(epoch,cursor,1).records[0].kind==DeliveryKind::Cancel);}
}
}
int main(int argc,char** argv){const std::map<std::string,void(*)()> cases{{"uuid_vectors",uuid_vectors},{"ordered_page",ordered_page},{"fill_payload",fill_payload},{"restart_retry",restart_retry},{"cancel_payload",cancel_payload},{"cursor_errors",cursor_errors},{"epoch_binding",epoch_binding},{"legacy",legacy},{"independent_ids",independent_ids},{"failed_write",failed_write},{"consumer_restart",consumer_restart}};try{if(argc!=2)return 2;cases.at(argv[1])();return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
