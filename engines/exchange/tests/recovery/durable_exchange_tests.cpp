#include "quantdesk/exchange/durable_exchange.hpp"
#include <chrono>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <random>
#include <stdexcept>
#include <string>
#include <utility>
using namespace quantdesk::exchange;
namespace {
void check(bool ok) { if (!ok) throw std::runtime_error("Assertion failed"); }
template<class E = std::exception, class F> void rejects(F f) {
    try { f(); } catch (const E&) { return; }
    throw std::runtime_error("Expected exception");
}
struct Temp {
    std::filesystem::path dir, path;
    Temp() {
        std::random_device r;
        for (int i = 0; i < 20; ++i) {
            dir = std::filesystem::temp_directory_path() / ("qd-journal-" + std::to_string(r()) + "-" + std::to_string(r()));
            if (std::filesystem::create_directory(dir)) { path = dir / "commands.log"; return; }
        }
        throw std::runtime_error("Cannot create test directory");
    }
    ~Temp() { std::error_code ec; std::filesystem::remove_all(dir, ec); }
};
const BookKey a{"s1", "ABC"}, b{"s2", "ABC"};
LimitOrderRequest order(std::string id, std::string owner, Side side, Quantity qty = 10, Price price = 1500000, BookKey key = a) {
    return {std::move(id), std::move(owner), std::move(key.session_id), std::move(key.instrument_id), side, price, qty};
}
void replay() {
    Temp t;
    { DurableExchange e(t.path, JournalMode::CreateNew);
      check(e.register_book("r1", a).command_sequence == 1);
      (void)e.register_book("r2", b);
      (void)e.submit("s1", order("sell1", "alice", Side::Sell, 6));
      (void)e.submit("s2", order("sell2", "bob", Side::Sell, 8));
      auto r = e.submit("buy", order("buy1", "charlie", Side::Buy, 10));
      check(r.command_sequence == 5 && r.submission->executions.size() == 2);
      check(r.submission->executions[0].sell_order_id == "sell1" && r.submission->executions[1].quantity == 4);
      (void)e.submit("other", order("other1", "dave", Side::Buy, 10, 1500000, b)); }
    { DurableExchange e(t.path, JournalMode::OpenExisting);
      check(e.view().book_count() == 2 && e.view().order_count() == 4);
      auto r = e.receipt("buy"); check(r && r->command_sequence == 5 && r->submission->executions.size() == 2);
      check(r->submission->executions[0].sequence == 1 && r->submission->executions[1].sequence == 2);
      check(e.view().depth(a, Side::Sell).at(0).quantity == 4 && e.view().depth(b, Side::Buy).at(0).quantity == 10);
      auto next = e.submit("next", order("buy2", "charlie", Side::Buy, 4));
      check(next.command_sequence == 7 && next.submission->executions.at(0).sequence == 3); }
}
void duplicate() {
    Temp t;
    auto req = order("buy", "buyer", Side::Buy);
    { DurableExchange e(t.path, JournalMode::CreateNew); (void)e.register_book("r", a);
      (void)e.submit("s", order("sell", "seller", Side::Sell));
      auto first = e.submit("c", req); auto retry = e.submit("c", req);
      check(first.command_sequence == retry.command_sequence && retry.submission->executions.size() == 1);
      check(e.view().last_execution_sequence(a) == 1); }
    { DurableExchange e(t.path, JournalMode::OpenExisting); auto retry = e.submit("c", req);
      check(retry.command_sequence == 3 && retry.submission->executions.at(0).sequence == 1);
      check(e.view().last_execution_sequence(a) == 1); }
}
void conflict() {
    Temp t; DurableExchange e(t.path, JournalMode::CreateNew); (void)e.register_book("r", a);
    (void)e.submit("c", order("buy", "buyer", Side::Buy));
    rejects<std::invalid_argument>([&]{ (void)e.submit("c", order("buy", "buyer", Side::Buy, 11)); });
    rejects<std::invalid_argument>([&]{ (void)e.register_book("c", b); });
    check(e.healthy() && e.view().order_count() == 1);
}
void cancellation() {
    Temp t;
    { DurableExchange e(t.path, JournalMode::CreateNew); (void)e.register_book("r", a);
      (void)e.submit("s", order("sell", "alice", Side::Sell));
      (void)e.submit("b", order("buy", "bob", Side::Buy, 4));
      check(e.cancel("c", a, "sell", "alice").command_sequence == 4); }
    { DurableExchange e(t.path, JournalMode::OpenExisting); auto p = e.view().find(a, "sell", "alice");
      check(p && p->state() == OrderState::Cancelled && p->filled_quantity() == 4 && p->open_quantity() == 0);
      check(e.cancel("c", a, "sell", "alice").command_sequence == 4 && e.view().depth(a, Side::Sell).empty()); }
}
void rejection() {
    Temp t;
    { DurableExchange e(t.path, JournalMode::CreateNew); (void)e.register_book("r", a);
      (void)e.submit("s", order("sell", "alice", Side::Sell));
      rejects<std::invalid_argument>([&]{ (void)e.submit("bad", order("buy", "alice", Side::Buy)); });
      rejects<std::invalid_argument>([&]{ (void)e.cancel("bad2", a, "sell", "bob"); });
      check(e.healthy() && !e.receipt("bad"));
      check(e.submit("bad", order("buy", "bob", Side::Buy)).command_sequence == 3); }
    { DurableExchange e(t.path, JournalMode::OpenExisting); check(e.view().last_execution_sequence(a) == 1 && !e.receipt("bad2")); }
}
void corrupt_record() {
    Temp t; { DurableExchange e(t.path, JournalMode::CreateNew); (void)e.register_book("r", a); }
    { std::fstream f(t.path, std::ios::binary | std::ios::in | std::ios::out); f.seekg(-1, std::ios::end); char c{}; f.get(c); f.seekp(-1, std::ios::end); f.put(static_cast<char>(c ^ 1)); }
    rejects([&]{ DurableExchange e(t.path, JournalMode::OpenExisting); });
}
void torn_record() {
    Temp t; { DurableExchange e(t.path, JournalMode::CreateNew); (void)e.register_book("r", a); }
    std::filesystem::resize_file(t.path, std::filesystem::file_size(t.path) - 1);
    rejects([&]{ DurableExchange e(t.path, JournalMode::OpenExisting); });
}
void exclusive_and_modes() {
    Temp t;
    rejects([&]{ DurableExchange e(t.path, JournalMode::OpenExisting); });
    { DurableExchange e(t.path, JournalMode::CreateNew);
      rejects([&]{ DurableExchange other(t.path, JournalMode::CreateNew); });
      rejects([&]{ DurableExchange other(t.path, JournalMode::OpenExisting); }); }
    { DurableExchange e(t.path, JournalMode::OpenExisting); check(e.view().book_count() == 0); }
}
void fail_closed() {
    Temp t;
    { DurableExchange e(t.path, JournalMode::CreateNew, 64); (void)e.register_book("r", a);
      rejects([&]{ (void)e.submit("s", order("sell", "alice", Side::Sell)); });
      check(!e.healthy()); rejects([&]{ (void)e.view(); }); rejects([&]{ (void)e.receipt("r"); });
      rejects([&]{ (void)e.register_book("r", a); }); }
    { DurableExchange e(t.path, JournalMode::OpenExisting); check(e.view().book_count() == 1 && e.view().order_count() == 0);
      check(e.submit("s", order("sell", "alice", Side::Sell)).command_sequence == 2); }
}
void invalid_header() {
    Temp t; { std::ofstream f(t.path, std::ios::binary); f << "QD-JOURNAL-2\n"; }
    rejects([&]{ DurableExchange e(t.path, JournalMode::OpenExisting); });
}
}
int main(int argc, char** argv) {
    const std::map<std::string, void(*)()> cases{{"replay", replay}, {"duplicate", duplicate}, {"conflict", conflict}, {"cancellation", cancellation}, {"rejection", rejection}, {"corrupt_record", corrupt_record}, {"torn_record", torn_record}, {"exclusive_and_modes", exclusive_and_modes}, {"fail_closed", fail_closed}, {"invalid_header", invalid_header}};
    try { if (argc != 2) return 2; cases.at(argv[1])(); return 0; }
    catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 1; }
}
