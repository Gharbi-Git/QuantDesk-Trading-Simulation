#include "quantdesk/exchange/order_book.hpp"
#include <algorithm>
#include <iostream>
#include <limits>
#include <random>
#include <stdexcept>
#include <string_view>

using namespace quantdesk::exchange;
namespace {
#define CHECK(condition) do { if (!(condition)) throw std::runtime_error("Failed: " #condition); } while(false)
template<class E, class F> void rejects(F f) {
    try { f(); } catch (const E&) { return; }
    throw std::runtime_error("Expected exception not thrown");
}
LimitOrderRequest req(std::string id, Side side, Price price, Quantity quantity, std::string account = "") {
    if (account.empty()) account = "account-" + id;
    return {id, account, "session", "TEST", side, price, quantity};
}
SubmitResult add(OrderBook& book, std::string id, Side side, Price price, Quantity quantity, std::string account = "") {
    return book.submit(req(id, side, price, quantity, account));
}
void run(std::string_view name) {
    OrderBook b("session", "TEST");
    if (name == "resting_and_depth") {
        CHECK(!b.best_bid() && !b.best_ask());
        CHECK(add(b,"b1",Side::Buy,100,10).executions.empty());
        (void)add(b,"b2",Side::Buy,100,20);
        (void)add(b,"b3",Side::Buy,99,5);
        (void)add(b,"s",Side::Sell,101,7);
        auto d = b.depth(Side::Buy);
        CHECK(b.best_bid()==100 && b.best_ask()==101 && d.size()==2);
        CHECK(d[0].quantity==30 && d[0].order_count==2 && d[1].price==99);
        CHECK(b.last_execution_sequence()==0);
    } else if (name == "buy_price_priority") {
        (void)add(b,"s1",Side::Sell,105,10); (void)add(b,"s2",Side::Sell,100,10);
        auto r=add(b,"b",Side::Buy,110,15);
        CHECK(r.executions.size()==2 && r.executions[0].sell_order_id=="s2" && r.executions[1].sell_order_id=="s1");
        CHECK(r.executions[0].price==100 && r.executions[1].price==105 && r.executions[1].quantity==5);
        CHECK(b.best_ask()==105 && b.find("s1")->open_quantity()==5 && r.state==OrderState::Filled);
    } else if (name == "sell_price_priority") {
        (void)add(b,"b1",Side::Buy,100,10); (void)add(b,"b2",Side::Buy,105,10);
        auto r=add(b,"s",Side::Sell,99,15);
        CHECK(r.executions.size()==2 && r.executions[0].buy_order_id=="b2" && r.executions[1].buy_order_id=="b1");
        CHECK(r.executions[0].price==105 && r.executions[1].price==100 && b.best_bid()==100);
    } else if (name == "fifo_both_sides") {
        for (auto side : {Side::Buy,Side::Sell}) {
            OrderBook book("session","TEST");
            (void)add(book,"first",side,100,10); (void)add(book,"second",side,100,10);
            auto other=side==Side::Buy?Side::Sell:Side::Buy;
            auto r=add(book,"taker",other,100,12);
            CHECK(r.executions.size()==2 && r.executions[0].quantity==10 && r.executions[1].quantity==2);
            CHECK(book.find("first")->state()==OrderState::Filled && book.find("second")->open_quantity()==8);
        }
    } else if (name == "partial_rest_and_priority") {
        (void)add(b,"s",Side::Sell,100,4);
        auto r=add(b,"b",Side::Buy,100,10);
        CHECK(r.state==OrderState::PartiallyFilled && r.open_quantity==6 && r.filled_quantity==4);
        (void)add(b,"later",Side::Buy,100,10);
        auto t=add(b,"s2",Side::Sell,100,7);
        CHECK(t.executions.size()==2 && t.executions[0].buy_order_id=="b" && t.executions[0].quantity==6);
        CHECK(b.find("later")->open_quantity()==9 && b.find("b")->state()==OrderState::Filled);
    } else if (name == "limit_boundary") {
        (void)add(b,"s1",Side::Sell,100,3); (void)add(b,"s2",Side::Sell,101,8);
        auto r=add(b,"b",Side::Buy,100,10);
        CHECK(r.executions.size()==1 && r.open_quantity==7 && b.best_bid()==100 && b.best_ask()==101);
        CHECK(b.find("s2")->filled_quantity()==0);
    } else if (name == "cancel_and_fifo") {
        (void)add(b,"s1",Side::Sell,100,2); (void)add(b,"s2",Side::Sell,100,3); (void)add(b,"s3",Side::Sell,100,4);
        b.cancel("s2","account-s2"); b.cancel("s2","account-s2");
        CHECK(b.depth(Side::Sell)[0].quantity==6);
        auto r=add(b,"b",Side::Buy,100,6);
        CHECK(r.executions.size()==2 && r.executions[0].sell_order_id=="s1" && r.executions[1].sell_order_id=="s3");
        CHECK(!b.best_ask() && b.find("s2")->state()==OrderState::Cancelled);
    } else if (name == "cancel_partial_and_ownership") {
        (void)add(b,"b",Side::Buy,100,10); (void)add(b,"s",Side::Sell,100,4);
        rejects<std::invalid_argument>([&]{b.cancel("b","wrong");});
        rejects<std::invalid_argument>([&]{b.cancel("missing","wrong");});
        rejects<std::logic_error>([&]{b.cancel("s","account-s");});
        CHECK(b.find("b")->open_quantity()==6);
        b.cancel("b","account-b");
        CHECK(!b.best_bid() && b.find("b")->filled_quantity()==4 && b.find("b")->remaining_quantity()==6);
    } else if (name == "duplicate_and_invalid") {
        (void)add(b,"b",Side::Buy,100,10); b.cancel("b","account-b");
        rejects<std::invalid_argument>([&]{(void)add(b,"b",Side::Sell,100,10);});
        rejects<std::invalid_argument>([&]{(void)add(b,"invalid",Side::Buy,0,1);});
        CHECK(!b.best_bid() && !b.best_ask() && !b.find("invalid") && b.last_execution_sequence()==0);
    } else if (name == "session_instrument_isolation") {
        (void)add(b,"s",Side::Sell,100,3);
        auto r=req("b",Side::Buy,100,5); r.session_id="other";
        rejects<std::invalid_argument>([&]{(void)b.submit(r);});
        r.session_id="session"; r.instrument_id="OTHER";
        rejects<std::invalid_argument>([&]{(void)b.submit(r);});
        CHECK(b.find("s")->open_quantity()==3 && !b.find("b"));
        OrderBook other("other","TEST"); r.instrument_id="TEST"; r.session_id="other";
        CHECK(other.submit(r).executions.empty() && b.best_ask()==100);
    } else if (name == "self_trade_atomic_rejection") {
        (void)add(b,"s1",Side::Sell,99,2,"other"); (void)add(b,"s2",Side::Sell,100,3,"same");
        rejects<std::invalid_argument>([&]{(void)add(b,"b",Side::Buy,100,5,"same");});
        CHECK(b.find("s1")->open_quantity()==2 && b.find("s2")->open_quantity()==3 && !b.find("b") && b.last_execution_sequence()==0);
        auto r=add(b,"b",Side::Buy,100,5,"different");
        CHECK(r.executions.size()==2 && r.executions[0].sequence==1 && r.executions[1].sequence==2);
    } else if (name == "execution_fields_and_sequence") {
        (void)add(b,"b",Side::Buy,100,10,"buyer");
        auto r=add(b,"s",Side::Sell,90,4,"seller"); const auto& e=r.executions.at(0);
        CHECK(e.sequence==1 && e.session_id=="session" && e.instrument_id=="TEST" && e.buy_order_id=="b" && e.sell_order_id=="s");
        CHECK(e.buyer_account_id=="buyer" && e.seller_account_id=="seller" && e.price==100 && e.quantity==4);
        auto t=add(b,"s2",Side::Sell,100,6); CHECK(t.executions.at(0).sequence==2 && b.last_execution_sequence()==2);
    } else if (name == "depth_overflow_atomic") {
        (void)add(b,"big",Side::Buy,1,std::numeric_limits<Quantity>::max());
        rejects<std::overflow_error>([&]{(void)add(b,"overflow",Side::Buy,1,1);});
        CHECK(!b.find("overflow") && b.depth(Side::Buy)[0].quantity==std::numeric_limits<Quantity>::max());
    } else if (name == "randomized_conservation") {
        std::mt19937 rng(2026); Quantity total_bought=0,total_sold=0,matched=0; std::int64_t sequence=0;
        std::vector<std::string> ids;
        for(int i=0;i<1000;++i) {
            auto id=std::to_string(i); auto side=(rng()%2==0)?Side::Buy:Side::Sell;
            auto result=add(b,id,side,static_cast<Price>(90+rng()%21),static_cast<Quantity>(1+rng()%30));
            ids.push_back(id);
            for(const auto& e:result.executions) {
                CHECK(e.sequence==++sequence && e.quantity>0);
                CHECK(e.price<=b.find(e.buy_order_id)->request().limit_price && e.price>=b.find(e.sell_order_id)->request().limit_price);
                CHECK(e.buyer_account_id!=e.seller_account_id); matched+=e.quantity;
            }
            if(i%7==0) { const auto& chosen=ids[rng()%ids.size()]; if(b.find(chosen)->is_open()) b.cancel(chosen,"account-"+chosen); }
            CHECK(!b.best_bid() || !b.best_ask() || *b.best_bid()<*b.best_ask());
        }
        Quantity open_buy=0,open_sell=0;
        for(const auto& id:ids) { auto* o=b.find(id); CHECK(o->filled_quantity()+o->remaining_quantity()==o->request().quantity);
            if(o->request().side==Side::Buy) { total_bought+=o->filled_quantity(); open_buy+=o->open_quantity(); }
            else {total_sold+=o->filled_quantity(); open_sell+=o->open_quantity();} }
        for(const auto& level:b.depth(Side::Buy)) open_buy-=level.quantity;
        for(const auto& level:b.depth(Side::Sell)) open_sell-=level.quantity;
        CHECK(total_bought==matched && total_sold==matched && open_buy==0 && open_sell==0 && sequence==b.last_execution_sequence());
    } else throw std::invalid_argument("Unknown test");
}
}
int main(int argc,char** argv) {
    if(argc!=2) return 2;
    try {run(argv[1]); return 0;} catch(const std::exception& e) {std::cerr<<argv[1]<<": "<<e.what()<<'\n';return 1;}
}
