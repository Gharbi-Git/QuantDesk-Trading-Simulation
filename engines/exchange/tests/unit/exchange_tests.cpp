#include "quantdesk/exchange/exchange.hpp"
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string_view>

using namespace quantdesk::exchange;
namespace {
#define CHECK(c) do { if (!(c)) throw std::runtime_error("Failed: " #c); } while(false)
template<class E, class F> void rejects(F f) {
    try { f(); } catch (const E&) { return; }
    throw std::runtime_error("Expected exception not thrown");
}
const BookKey a{"match-a","TEST"}, b{"match-b","TEST"}, c{"match-a","OTHER"};
SubmitResult add(Exchange& x, const BookKey& k, std::string id, Side s, Price p=100,
                 Quantity q=10, std::string owner="") {
    if (owner.empty()) owner="account-"+id;
    return x.submit({id,owner,k.session_id,k.instrument_id,s,p,q});
}
void run(std::string_view name) {
    Exchange x;
    if (name=="registration") {
        x.register_book(a); (void)add(x,a,"b",Side::Buy); x.register_book(a);
        CHECK(x.book_count()==1 && x.order_count()==1 && x.best_bid(a)==100);
        rejects<std::invalid_argument>([&]{x.register_book({"","TEST"});});
        CHECK(x.book_count()==1);
        return;
    }
    x.register_book(a); x.register_book(b); x.register_book(c);
    if(name=="unknown_book") {
        BookKey unknown{"missing","TEST"};
        rejects<std::invalid_argument>([&]{(void)add(x,unknown,"id",Side::Buy);});
        rejects<std::invalid_argument>([&]{(void)x.depth(unknown,Side::Buy);});
        CHECK(x.book_count()==3 && x.order_count()==0);
        CHECK(add(x,a,"id",Side::Buy).executions.empty());
    } else if(name=="session_isolation") {
        (void)add(x,a,"s",Side::Sell);
        CHECK(add(x,b,"b",Side::Buy).executions.empty());
        CHECK(x.best_ask(a)==100 && x.best_bid(b)==100);
        auto r=add(x,a,"b2",Side::Buy);
        CHECK(r.executions.size()==1 && r.executions[0].session_id==a.session_id);
        CHECK(x.best_bid(b)==100 && !x.best_ask(a));
    } else if(name=="instrument_isolation") {
        (void)add(x,a,"s",Side::Sell);
        CHECK(add(x,c,"b",Side::Buy).executions.empty());
        CHECK(x.best_ask(a)==100 && x.best_bid(c)==100);
        CHECK(add(x,c,"s2",Side::Sell).executions.at(0).instrument_id==c.instrument_id);
        CHECK(x.best_ask(a)==100);
    } else if(name=="global_order_id") {
        (void)add(x,a,"id",Side::Buy); x.cancel(a,"id","account-id");
        rejects<std::invalid_argument>([&]{(void)add(x,b,"id",Side::Buy);});
        rejects<std::invalid_argument>([&]{(void)add(x,c,"id",Side::Sell);});
        CHECK(x.order_count()==1 && !x.best_bid(b) && !x.best_ask(c));
        CHECK(x.find(a,"id","account-id")->state()==OrderState::Cancelled);
    } else if(name=="scoped_lookup_cancel") {
        (void)add(x,a,"id",Side::Buy);
        CHECK(!x.find(b,"id","account-id") && !x.find(c,"id","account-id") && !x.find(a,"id","wrong"));
        rejects<std::invalid_argument>([&]{x.cancel(b,"id","account-id");});
        rejects<std::invalid_argument>([&]{x.cancel(c,"id","account-id");});
        rejects<std::invalid_argument>([&]{x.cancel(a,"id","wrong");});
        CHECK(x.best_bid(a)==100); x.cancel(a,"id","account-id"); x.cancel(a,"id","account-id");
        CHECK(!x.best_bid(a));
    } else if(name=="rejection_index_rollback") {
        (void)add(x,a,"s",Side::Sell,100,10,"same");
        rejects<std::invalid_argument>([&]{(void)add(x,a,"bad",Side::Buy,100,10,"same");});
        rejects<std::invalid_argument>([&]{(void)add(x,a,"bad",Side::Buy,0);});
        CHECK(x.order_count()==1 && x.last_execution_sequence(a)==0 && x.best_ask(a)==100);
        CHECK(add(x,b,"bad",Side::Buy).executions.empty() && x.order_count()==2);
    } else if(name=="overflow_index_rollback") {
        (void)add(x,a,"big",Side::Buy,1,std::numeric_limits<Quantity>::max());
        rejects<std::overflow_error>([&]{(void)add(x,a,"overflow",Side::Buy,1,1);});
        CHECK(x.order_count()==1 && x.depth(a,Side::Buy).at(0).quantity==std::numeric_limits<Quantity>::max());
        CHECK(add(x,c,"overflow",Side::Buy,1,1).executions.empty());
    } else if(name=="scoped_sequence") {
        (void)add(x,a,"s1",Side::Sell); (void)add(x,b,"s2",Side::Sell);
        auto e1=add(x,a,"b1",Side::Buy,100,4).executions.at(0);
        auto e2=add(x,b,"b2",Side::Buy).executions.at(0);
        auto e3=add(x,a,"b3",Side::Buy,100,6).executions.at(0);
        CHECK(e1.sequence==1 && e2.sequence==1 && e3.sequence==2);
        CHECK(e1.session_id!=e2.session_id && x.last_execution_sequence(a)==2 && x.last_execution_sequence(b)==1);
    } else if(name=="stable_index") {
        (void)add(x,a,"id",Side::Buy);
        const auto* before=x.find(a,"id","account-id");
        for(int i=0;i<200;++i) { BookKey key{"extra-"+std::to_string(i),"TEST"}; x.register_book(key); (void)add(x,key,"extra-"+std::to_string(i),Side::Buy); }
        CHECK(x.find(a,"id","account-id")==before && x.book_count()==203 && x.order_count()==201);
        x.cancel(a,"id","account-id"); CHECK(!x.best_bid(a));
    } else throw std::invalid_argument("Unknown test");
}
}
int main(int argc,char** argv) {
    if(argc!=2) return 2;
    try {run(argv[1]);return 0;} catch(const std::exception& e) {std::cerr<<argv[1]<<": "<<e.what()<<'\n';return 1;}
}
