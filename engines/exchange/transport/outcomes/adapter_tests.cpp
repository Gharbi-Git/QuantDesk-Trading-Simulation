#include "outcomes_adapter.hpp"
#include "../test_support.hpp"
#include <iostream>
namespace a=qd_outcomes;namespace d=quantdesk::exchange;
int main(){try{
    qt::Temp t;auto file=t.dir/"journal";d::BookKey book{qt::id(10),"TEST"};
    d::LimitOrderRequest order{qt::id(11),qt::id(12),book.session_id,book.instrument_id,d::Side::Buy,10000,1};
    a::Identity submit{qt::epoch,qt::id(1)};a::Identity reg{qt::epoch,qt::id(2)};
    {a::Adapter x(file,d::JournalMode::CreateNew,qt::epoch);
    auto r=x.submit(submit,order);qt::require(r.outcome==d::CommandOutcome::DomainRejected&&r.command_sequence==1,"Rejected submit");
    qt::require(x.register_book(reg,book).outcome==d::CommandOutcome::Accepted,"Register");
    qt::require(x.submit(submit,order).outcome==r.outcome,"Stable rejection after register");
    auto bad=order;bad.quantity=2;bool conflict=false;try{(void)x.submit(submit,bad);}catch(const a::Failure& e){conflict=e.code==a::Fault::Conflict;}qt::require(conflict,"Changed retry");
    auto cancel=x.cancel({qt::epoch,qt::id(3)},book,order.order_id,order.account_id);qt::require(cancel.outcome==d::CommandOutcome::DomainRejected,"Rejected cancel");}
    {a::Adapter x(file,d::JournalMode::OpenExisting,qt::epoch);auto p=x.delivery(qt::epoch,0,3);qt::require(p.high_watermark==3&&p.records.size()==3&&p.records[0].request&&p.records[0].request->order_id==order.order_id&&p.records[0].outcome==d::CommandOutcome::DomainRejected&&p.records[0].fills.empty()&&p.records[2].outcome==d::CommandOutcome::DomainRejected,"Recovery payload");qt::require(x.receipt(submit)->command_sequence==1,"Lookup");}
    bool fenced=false;try{d::DurableExchange old(file,d::JournalMode::OpenExisting,qt::epoch,d::JournalFormat::TimestampedV3);}catch(const std::exception&){fenced=true;}qt::require(fenced,"V3 format fence");
    const auto large=t.dir/"large";
    const d::BookKey big{qt::id(20000),std::string(128,'X')};
    const a::Identity registration{qt::epoch,qt::id(20001)};
    const a::Identity sweep{qt::epoch,qt::id(25000)};
    const d::LimitOrderRequest taker{qt::id(25001),qt::id(25002),big.session_id,big.instrument_id,d::Side::Buy,10000,2046};
    qt::require(a::parse_capacity("--max-commands=2048")==2048,"Capacity parsing");
    for(const auto* invalid:{"--max-commands=511","--max-commands=2049","--max-commands=2048x","--max-commands=","--unknown=512"}){
        bool failed=false;try{(void)a::parse_capacity(invalid);}catch(const std::invalid_argument&){failed=true;}qt::require(failed,"Invalid capacity");
    }
    {a::Adapter x(large,d::JournalMode::CreateNew,qt::epoch,2048);
     (void)x.register_book(registration,big);
     for(unsigned i=0;i<2046;++i){
        d::LimitOrderRequest maker{qt::id(30000+i),qt::id(25003),big.session_id,big.instrument_id,d::Side::Sell,10000,1};
        auto r=x.submit({qt::epoch,qt::id(40000+i)},maker);
        qt::require(r.outcome==d::CommandOutcome::Accepted,"Maker accepted");
     }
     auto fill=x.submit(sweep,taker);qt::require(fill.submission&&fill.submission->executions.size()==2046,"Maximum fanout");
     bool full=false;try{(void)x.register_book({qt::epoch,qt::id(25004)},big);}catch(const a::Failure& e){full=e.code==a::Fault::Capacity;}
     qt::require(full&&x.status().high==2048&&!x.receipt({qt::epoch,qt::id(25004)}),"Capacity has no effects");
     qt::require(x.submit(sweep,taker).command_sequence==2048,"Retry at capacity");
     auto different=taker;different.quantity--;
     bool conflict_at_full=false;try{(void)x.submit(sweep,different);}catch(const a::Failure& e){conflict_at_full=e.code==a::Fault::Conflict;}
     qt::require(conflict_at_full,"Conflict at capacity");
     auto page=x.delivery(qt::epoch,1792,256);qt::require(page.records.size()==256&&page.records.back().fills.size()==2046,"Full page and fills");
    }
    bool too_small=false;try{a::Adapter x(large,d::JournalMode::OpenExisting,qt::epoch);}catch(const a::Failure& e){too_small=e.code==a::Fault::Capacity;}
    qt::require(too_small,"Smaller configured ceiling rejected");
    {a::Adapter x(large,d::JournalMode::OpenExisting,qt::epoch,2048);
     qt::require(x.status().high==2048&&x.receipt(sweep)->command_sequence==2048,"Large journal restart");
     qt::require(x.delivery(qt::epoch,2047,1).records.front().fills.size()==2046,"Recovered maximum fanout");}
    std::cout<<"PASS: configurable capacity, 2046 fills, full history, retry and restart\n";
    std::cout<<"PASS: V4 adapter rejection, lookup, replay, payload and format fencing\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
