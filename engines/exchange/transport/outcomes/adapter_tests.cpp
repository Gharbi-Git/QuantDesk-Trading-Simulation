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
    std::cout<<"PASS: V4 adapter rejection, lookup, replay, payload and format fencing\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
