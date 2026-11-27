#include "exchange_adapter.hpp"
#include "test_support.hpp"
#include <iostream>
#include <thread>
#include <atomic>
namespace a=qd_transport; namespace d=quantdesk::exchange;
using qt::require; using qt::id; using qt::epoch;
static a::Identity cmd(unsigned n){return {epoch,id(n)};}
template<class F> void reject(a::Fault expected,F f){try{f();}catch(const a::Failure& e){require(e.code==expected,"Wrong error");return;}throw std::runtime_error("Expected failure");}
int main(){try{
    qt::Temp temp;auto path=temp.dir/"journal"; d::BookKey book{id(10000),"TEST-EQUITY"};
    d::LimitOrderRequest sell{id(10001),id(10002),book.session_id,book.instrument_id,d::Side::Sell,1490000,100};
    auto buy=sell;buy.order_id=id(10003);buy.account_id=id(10004);buy.side=d::Side::Buy;buy.limit_price=1500000;buy.quantity=40;
    std::string execution;std::int64_t time=0;
    {a::Adapter e(path,d::JournalMode::CreateNew,epoch);
    require(e.status().high==0,"Initial status");reject(a::Fault::WrongEpoch,[&]{e.register_book({id(99),id(1)},book);});
    require(e.register_book(cmd(1),book).command_sequence==1,"Register");
    require(e.submit(cmd(2),sell).command_sequence==2,"Sell");
    require(e.submit(cmd(3),buy).command_sequence==3,"Buy");
    require(e.submit(cmd(3),buy).command_sequence==3,"Retry");
    auto changed=buy;changed.quantity=41;reject(a::Fault::Conflict,[&]{e.submit(cmd(3),changed);});
    require(!e.receipt(cmd(99)),"Absent receipt");
    reject(a::Fault::Rejected,[&]{e.cancel(cmd(4),book,sell.order_id,buy.account_id);});
    require(e.cancel(cmd(4),book,sell.order_id,sell.account_id).command_sequence==4,"Cancel");
    auto page=e.delivery(epoch,2,1);require(page.high_watermark==4&&page.records.size()==1,"Page");
    auto fill=page.records.at(0).fills.at(0);require(fill.execution.price==1490000&&fill.execution.quantity==40&&fill.execution_time_utc_unix_micros.has_value(),"Fill fields");
    execution=fill.execution_id;time=*fill.execution_time_utc_unix_micros;
    reject(a::Fault::Invalid,[&]{e.delivery(epoch,5,1);});reject(a::Fault::Invalid,[&]{e.delivery(epoch,0,0);});
    std::atomic<unsigned> ok{0};std::thread t1([&]{try{if(e.register_book(cmd(5),book).command_sequence==5)++ok;}catch(...){}});std::thread t2([&]{try{if(e.register_book(cmd(5),book).command_sequence==5)++ok;}catch(...){}});t1.join();t2.join();require(ok==2&&e.status().high==5,"Concurrent retry");
    for(unsigned n=6;n<=512;++n)e.register_book(cmd(n),book);
    reject(a::Fault::Capacity,[&]{e.register_book(cmd(513),book);});require(e.register_book(cmd(1),book).command_sequence==1,"Retry at capacity");}
    {a::Adapter e(path,d::JournalMode::OpenExisting,epoch);auto r=e.delivery(epoch,2,1).records.at(0).fills.at(0);require(r.execution_id==execution&&r.execution_time_utc_unix_micros==time&&e.status().high==512,"Replay");}
    std::cout<<"Adapter matching, fencing, retries, capacity, concurrency, replay passed\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
