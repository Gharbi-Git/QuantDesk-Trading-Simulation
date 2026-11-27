#include "exchange.grpc.pb.h"
#include "exchange_adapter.hpp"
#include "test_support.hpp"
#include <grpcpp/grpcpp.h>
#include <chrono>
#include <iostream>
#include <memory>
namespace w=quantdesk::v1::exchange;
namespace d=quantdesk::exchange;
namespace a=qd_transport;
using grpc::Status; using grpc::StatusCode;
namespace {
constexpr int message_limit=1024*1024;
void need(bool ok){if(!ok)throw a::Failure(a::Fault::Invalid,"Required field missing or invalid");}
a::Identity identity(const w::CommandIdentity& i){return {i.expected_journal_epoch(),i.command_id()};}
d::BookKey key(const w::BookKey& b){return {b.session_id(),b.instrument_id()};}
void key(w::BookKey* b,const d::BookKey& k){b->set_session_id(k.session_id);b->set_instrument_id(k.instrument_id);}
d::Side side(w::Side s){switch(s){case w::SIDE_BUY:return d::Side::Buy;case w::SIDE_SELL:return d::Side::Sell;default:throw a::Failure(a::Fault::Invalid,"Invalid side");}}
w::Side side(d::Side s){switch(s){case d::Side::Buy:return w::SIDE_BUY;case d::Side::Sell:return w::SIDE_SELL;}throw std::runtime_error("Unknown domain side");}
w::OrderState state(d::OrderState s){switch(s){case d::OrderState::Accepted:return w::ORDER_STATE_ACCEPTED;case d::OrderState::PartiallyFilled:return w::ORDER_STATE_PARTIALLY_FILLED;case d::OrderState::Filled:return w::ORDER_STATE_FILLED;case d::OrderState::Cancelled:return w::ORDER_STATE_CANCELLED;}throw std::runtime_error("Unknown domain state");}
void order(w::EquityLimitOrder* p,const d::LimitOrderRequest& r){key(p->mutable_book(),{r.session_id,r.instrument_id});p->set_order_id(r.order_id);p->set_account_id(r.account_id);p->set_side(side(r.side));p->set_limit_price_units(r.limit_price);p->set_quantity(r.quantity);}
void receipt(w::AcceptedReceipt* p,const a::Identity& i,const d::CommandReceipt& r){p->set_journal_epoch(i.epoch);p->set_command_id(i.command);p->set_command_sequence(r.command_sequence);}
void record(w::DeliveryRecord* p,const d::DeliveryRecord& r){
    p->set_command_sequence(r.command_sequence);p->set_command_id(r.command_id);
    switch(r.kind){
    case d::DeliveryKind::RegisterBook:key(p->mutable_registered()->mutable_book(),r.key);break;
    case d::DeliveryKind::Cancel:{auto* c=p->mutable_cancelled();key(c->mutable_book(),r.key);c->set_order_id(r.order_id);c->set_account_id(r.account_id);break;}
    case d::DeliveryKind::Submit:{
        if(!r.request||!r.state)throw std::runtime_error("Incomplete committed submission");
        auto* s=p->mutable_submitted();order(s->mutable_order(),*r.request);s->set_state(state(*r.state));s->set_filled_quantity(r.filled_quantity);s->set_open_quantity(r.open_quantity);
        for(const auto& f:r.fills){if(!f.execution_time_utc_unix_micros)throw std::runtime_error("Missing committed fill timestamp");
            auto* q=s->add_fills();const auto& x=f.execution;
            q->set_execution_id(f.execution_id);key(q->mutable_book(),{x.session_id,x.instrument_id});q->set_book_execution_sequence(x.sequence);q->set_buy_order_id(x.buy_order_id);q->set_sell_order_id(x.sell_order_id);q->set_buyer_account_id(x.buyer_account_id);q->set_seller_account_id(x.seller_account_id);q->set_execution_price_units(x.price);q->set_quantity(x.quantity);q->set_execution_time_utc_unix_micros(*f.execution_time_utc_unix_micros);
        }break;}
    default:throw std::runtime_error("Unknown delivery kind");
    }
}
template<class F> Status handle(F f){try{f();return Status::OK;}catch(const a::Failure& e){
    StatusCode code=StatusCode::UNAVAILABLE;
    switch(e.code){case a::Fault::Invalid:code=StatusCode::INVALID_ARGUMENT;break;case a::Fault::WrongEpoch:case a::Fault::Rejected:code=StatusCode::FAILED_PRECONDITION;break;case a::Fault::Conflict:code=StatusCode::ALREADY_EXISTS;break;case a::Fault::Capacity:code=StatusCode::RESOURCE_EXHAUSTED;break;case a::Fault::Unavailable:break;}
    return Status(code,e.what());
}catch(...){return Status(StatusCode::UNAVAILABLE,"Operation outcome unknown; recover or retry identical command");}}
class Service final:public w::ExchangeService::Service{
    a::Adapter& adapter_;
public:
    explicit Service(a::Adapter& a):adapter_(a){}
    Status GetStatus(grpc::ServerContext*,const w::StatusRequest*,w::StatusReply* out)override{return handle([&]{auto s=adapter_.status();out->set_journal_epoch(s.epoch);out->set_healthy(true);out->set_high_watermark(s.high);});}
    Status RegisterBook(grpc::ServerContext*,const w::RegisterBookRequest* r,w::AcceptedReceipt* out)override{return handle([&]{need(r->has_identity()&&r->has_book());auto i=identity(r->identity());receipt(out,i,adapter_.register_book(i,key(r->book())));});}
    Status SubmitOrder(grpc::ServerContext*,const w::SubmitOrderRequest* r,w::AcceptedReceipt* out)override{return handle([&]{need(r->has_identity()&&r->has_order()&&r->order().has_book());auto i=identity(r->identity());const auto& p=r->order();auto b=key(p.book());receipt(out,i,adapter_.submit(i,{p.order_id(),p.account_id(),b.session_id,b.instrument_id,side(p.side()),p.limit_price_units(),p.quantity()}));});}
    Status CancelOrder(grpc::ServerContext*,const w::CancelOrderRequest* r,w::AcceptedReceipt* out)override{return handle([&]{need(r->has_identity()&&r->has_book());auto i=identity(r->identity());receipt(out,i,adapter_.cancel(i,key(r->book()),r->order_id(),r->account_id()));});}
    Status GetCommandReceipt(grpc::ServerContext*,const w::ReceiptRequest* r,w::ReceiptReply* out)override{return handle([&]{need(r->has_identity());auto i=identity(r->identity());auto x=adapter_.receipt(i);if(x)receipt(out->mutable_receipt(),i,*x);});}
    Status GetDeliveryBatch(grpc::ServerContext*,const w::DeliveryRequest* r,w::DeliveryBatch* out)override{return handle([&]{auto page=adapter_.delivery(r->expected_journal_epoch(),r->after_command_sequence(),r->max_commands());out->set_journal_epoch(page.journal_epoch);out->set_high_watermark(page.high_watermark);for(const auto& x:page.records)record(out->add_records(),x);if(out->ByteSizeLong()>message_limit){out->Clear();throw a::Failure(a::Fault::Capacity,"Full page exceeds dev byte limit; request a smaller page, never skip a command");}});}
};
struct Running {
    Service service;int port=0;std::unique_ptr<grpc::Server> server;
    explicit Running(a::Adapter& a):service(a){grpc::ServerBuilder b;b.SetMaxReceiveMessageSize(64*1024);b.SetMaxSendMessageSize(message_limit);b.AddListeningPort("127.0.0.1:0",grpc::InsecureServerCredentials(),&port);b.RegisterService(&service);server=b.BuildAndStart();qt::require(server&&port>0,"Server start failed");}
    ~Running(){if(server){server->Shutdown();server->Wait();}}
    auto stub(){grpc::ChannelArguments args;args.SetMaxReceiveMessageSize(message_limit);return w::ExchangeService::NewStub(grpc::CreateCustomChannel("127.0.0.1:"+std::to_string(port),grpc::InsecureChannelCredentials(),args));}
};
// A fresh context and deadline for each synchronous RPC.
#define CALL(method,req,reply) ([&]{grpc::ClientContext ctx;ctx.set_deadline(std::chrono::system_clock::now()+std::chrono::seconds(5));return stub->method(&ctx,req,&reply);}())
void ident(w::CommandIdentity* i,unsigned n){i->set_expected_journal_epoch(qt::epoch);i->set_command_id(qt::id(n));}
int self_test(){
    using qt::require;using qt::id;qt::Temp temp;auto file=temp.dir/"journal";std::string execution;std::int64_t micros=0;
    d::BookKey b{id(10000),"TEST-EQUITY"};
    {a::Adapter adapter(file,d::JournalMode::CreateNew,qt::epoch);Running server(adapter);auto stub=server.stub();
    w::StatusRequest sr;w::StatusReply sp;require(CALL(GetStatus,sr,sp).ok()&&sp.high_watermark()==0&&sp.healthy(),"Initial status");
    w::RegisterBookRequest reg;w::AcceptedReceipt ack;ident(reg.mutable_identity(),1);key(reg.mutable_book(),b);
    require(CALL(RegisterBook,reg,ack).ok()&&ack.command_sequence()==1&&ack.journal_epoch()==qt::epoch,"Register");
    w::SubmitOrderRequest sell;ident(sell.mutable_identity(),2);order(sell.mutable_order(),{id(10001),id(10002),b.session_id,b.instrument_id,d::Side::Sell,1490000,100});
    require(CALL(SubmitOrder,sell,ack).ok()&&ack.command_sequence()==2,"Sell");
    w::SubmitOrderRequest buy;ident(buy.mutable_identity(),3);order(buy.mutable_order(),{id(10003),id(10004),b.session_id,b.instrument_id,d::Side::Buy,1500000,40});
    require(CALL(SubmitOrder,buy,ack).ok()&&ack.command_sequence()==3,"Buy");
    require(CALL(SubmitOrder,buy,ack).ok()&&ack.command_sequence()==3,"Duplicate");
    auto changed=buy;changed.mutable_order()->set_quantity(41);require(CALL(SubmitOrder,changed,ack).error_code()==StatusCode::ALREADY_EXISTS,"Conflict");
    changed.mutable_identity()->set_expected_journal_epoch(id(99));require(CALL(SubmitOrder,changed,ack).error_code()==StatusCode::FAILED_PRECONDITION,"Epoch fence");
    w::SubmitOrderRequest invalid;require(CALL(SubmitOrder,invalid,ack).error_code()==StatusCode::INVALID_ARGUMENT,"Missing fields");
    w::CancelOrderRequest cancel;ident(cancel.mutable_identity(),4);key(cancel.mutable_book(),b);cancel.set_order_id(id(10001));cancel.set_account_id(id(10004));
    require(CALL(CancelOrder,cancel,ack).error_code()==StatusCode::FAILED_PRECONDITION,"Wrong owner");cancel.set_account_id(id(10002));require(CALL(CancelOrder,cancel,ack).ok()&&ack.command_sequence()==4,"Cancel");
    w::ReceiptRequest rq;w::ReceiptReply rp;ident(rq.mutable_identity(),99);require(CALL(GetCommandReceipt,rq,rp).ok()&&!rp.has_receipt(),"Absent receipt");ident(rq.mutable_identity(),3);require(CALL(GetCommandReceipt,rq,rp).ok()&&rp.receipt().command_sequence()==3,"Receipt lookup");
    w::DeliveryRequest dr;w::DeliveryBatch dp;dr.set_expected_journal_epoch(qt::epoch);dr.set_after_command_sequence(0);dr.set_max_commands(2);require(CALL(GetDeliveryBatch,dr,dp).ok()&&dp.high_watermark()==4&&dp.records_size()==2&&dp.records(0).has_registered()&&dp.records(1).has_submitted(),"Page 1");
    dr.set_after_command_sequence(2);require(CALL(GetDeliveryBatch,dr,dp).ok()&&dp.records_size()==2&&dp.records(1).has_cancelled(),"Page 2");const auto& s=dp.records(0).submitted();require(s.state()==w::ORDER_STATE_FILLED&&s.filled_quantity()==40&&s.open_quantity()==0&&s.fills_size()==1,"Submission state");const auto& f=s.fills(0);require(f.execution_price_units()==1490000&&f.quantity()==40&&f.buy_order_id()==id(10003)&&f.sell_order_id()==id(10001)&&f.buyer_account_id()==id(10004)&&f.seller_account_id()==id(10002)&&f.book_execution_sequence()==1&&f.execution_time_utc_unix_micros()>0,"Execution fields");execution=f.execution_id();micros=f.execution_time_utc_unix_micros();dr.set_after_command_sequence(4);require(CALL(GetDeliveryBatch,dr,dp).ok()&&dp.records_size()==0,"End cursor");dr.set_after_command_sequence(5);require(CALL(GetDeliveryBatch,dr,dp).error_code()==StatusCode::INVALID_ARGUMENT,"Future cursor");}
    {a::Adapter adapter(file,d::JournalMode::OpenExisting,qt::epoch);Running server(adapter);auto stub=server.stub();w::DeliveryRequest dr;w::DeliveryBatch dp;dr.set_expected_journal_epoch(qt::epoch);dr.set_after_command_sequence(2);dr.set_max_commands(1);require(CALL(GetDeliveryBatch,dr,dp).ok()&&dp.high_watermark()==4&&dp.records_size()==1,"Replay page");auto f=dp.records(0).submitted().fills(0);require(f.execution_id()==execution&&f.execution_time_utc_unix_micros()==micros,"Replay identity/time");}
    std::cout<<"All six RPCs, real matching, idempotency, cancellation, fencing and restart passed\n";return 0;
}
#undef CALL
}
int main(int argc,char** argv){try{
    if(argc==2&&std::string(argv[1])=="--self-test")return self_test();
    if(argc!=4||(std::string(argv[1])!="--create-dev-journal"&&std::string(argv[1])!="--open-dev-journal")){std::cerr<<"Usage: qd_exchange_trading --self-test OR --create-dev-journal|--open-dev-journal <isolated-test-file> <UUIDv4>\n";return 2;}
    // Never point at a live/production journal. Loopback plaintext is dev-only.
    a::Adapter adapter(argv[2],std::string(argv[1])=="--create-dev-journal"?d::JournalMode::CreateNew:d::JournalMode::OpenExisting,argv[3]);Running server(adapter);std::cout<<"DEV ONLY: unencrypted exchange at 127.0.0.1:"<<server.port<<"; no risk checks or settlement"<<std::endl;server.server->Wait();return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
