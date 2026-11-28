#include "exchange_outcomes.grpc.pb.h"
#include "outcomes_adapter.hpp"
#include "../test_support.hpp"
#include <grpcpp/grpcpp.h>
#include <chrono>
#include <iostream>
#include <memory>
#include <fstream>
#include <iterator>
#include <grpc/grpc_security_constants.h>
#include <grpcpp/security/auth_context.h>
#include <grpcpp/security/server_credentials.h>
namespace w=quantdesk::v1::exchange;
namespace d=quantdesk::exchange;
namespace a=qd_outcomes;
namespace v=quantdesk::v2::exchange;
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
void accepted(w::DeliveryRecord* p,const d::DeliveryRecord& r){
    if(r.outcome!=d::CommandOutcome::Accepted)throw std::runtime_error("Not accepted");
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
void record(v::DecisionRecord* p,const d::DeliveryRecord& r){
    if(r.outcome==d::CommandOutcome::Accepted){accepted(p->mutable_accepted(),r);return;}
    if(r.state||!r.fills.empty()||r.filled_quantity!=0||r.open_quantity!=0)throw std::runtime_error("Rejected record has effects");
    auto* q=p->mutable_rejected();q->set_command_id(r.command_id);q->set_command_sequence(r.command_sequence);
    switch(r.outcome){
    case d::CommandOutcome::DomainRejected:q->set_reason(v::REJECTION_REASON_DOMAIN_REJECTED);break;
    case d::CommandOutcome::NumericRejected:q->set_reason(v::REJECTION_REASON_NUMERIC_REJECTED);break;
    default:throw std::runtime_error("Unknown decision");}
    switch(r.kind){
    case d::DeliveryKind::RegisterBook:key(q->mutable_rejected_registration()->mutable_book(),r.key);break;
    case d::DeliveryKind::Submit:if(!r.request)throw std::runtime_error("Missing rejected payload");order(q->mutable_rejected_submit(),*r.request);break;
    case d::DeliveryKind::Cancel:{auto* c=q->mutable_rejected_cancel();key(c->mutable_book(),r.key);c->set_order_id(r.order_id);c->set_account_id(r.account_id);break;}
    default:throw std::runtime_error("Unknown rejected payload");}
}
void receipt(a::Adapter& adapter,v::CommandReceipt* p,const a::Identity& i,const d::CommandReceipt& r){
    auto page=adapter.delivery(i.epoch,r.command_sequence-1,1);
    if(page.records.size()!=1||page.records[0].command_id!=i.command||page.records[0].command_sequence!=r.command_sequence||page.records[0].outcome!=r.outcome)throw std::runtime_error("Receipt and journal disagree");
    p->set_journal_epoch(i.epoch);record(p->mutable_record(),page.records[0]);
}
template<class F> Status handle(F f){try{f();return Status::OK;}catch(const a::Failure& e){
    StatusCode code=StatusCode::UNAVAILABLE;
    switch(e.code){case a::Fault::Invalid:code=StatusCode::INVALID_ARGUMENT;break;case a::Fault::WrongEpoch:case a::Fault::Rejected:code=StatusCode::FAILED_PRECONDITION;break;case a::Fault::Conflict:code=StatusCode::ALREADY_EXISTS;break;case a::Fault::Capacity:code=StatusCode::RESOURCE_EXHAUSTED;break;case a::Fault::Unavailable:break;}
    return Status(code,e.what());
}catch(...){return Status(StatusCode::UNAVAILABLE,"Operation outcome unknown; recover or retry identical command");}}
// Compare PEM bodies, ignoring line-ending and wrapping differences only.
std::string canonical_pem(std::string text){
    std::string result; for(char c:text)if(c!='\r'&&c!='\n'&&c!=' '&&c!='\t')result+=c;
    return result;
}
std::string read_pem(const std::filesystem::path& file){
    std::ifstream in(file,std::ios::binary);if(!in)throw std::runtime_error("Missing TLS credential file");
    std::string s((std::istreambuf_iterator<char>(in)),std::istreambuf_iterator<char>());
    if(s.empty()||s.size()>64*1024)throw std::runtime_error("Invalid TLS credential file size");
    return s;
}
struct TlsConfig {
    std::shared_ptr<grpc::ServerCredentials> credentials;
    std::string allowed_peer;
};
TlsConfig tls_config(const std::filesystem::path& dir){
    grpc::SslServerCredentialsOptions options;
    options.pem_root_certs=read_pem(dir/"client-ca.pem");
    options.pem_key_cert_pairs.push_back({read_pem(dir/"server-key.pem"),read_pem(dir/"server.pem")});
    options.client_certificate_request=GRPC_SSL_REQUEST_AND_REQUIRE_CLIENT_CERTIFICATE_AND_VERIFY;
    auto credentials=grpc::SslServerCredentials(options);
    if(!credentials)throw std::runtime_error("TLS credentials unavailable");
    auto peer=canonical_pem(read_pem(dir/"backend.pem"));
    if(peer.find("-----BEGINCERTIFICATE-----")!=0||peer.find("-----ENDCERTIFICATE-----")==std::string::npos)
        throw std::runtime_error("Invalid pinned backend certificate");
    return {std::move(credentials),std::move(peer)};
}
class Service final:public v::ExchangeService::Service{
    a::Adapter& adapter_;
    const std::string allowed_peer_;
    template<class F> Status authorized(grpc::ServerContext* ctx,F f){
        if(!allowed_peer_.empty()){
            auto auth=ctx->auth_context();
            if(!auth||!auth->IsPeerAuthenticated())return Status(StatusCode::UNAUTHENTICATED,"Client certificate required");
            auto cert=auth->FindPropertyValues(GRPC_X509_PEM_CERT_PROPERTY_NAME);
            if(cert.size()!=1||canonical_pem(std::string(cert[0].data(),cert[0].size()))!=allowed_peer_)
                return Status(StatusCode::PERMISSION_DENIED,"Backend certificate not authorized");
        }
        return handle(f);
    }
public:
    explicit Service(a::Adapter& a,std::string peer):adapter_(a),allowed_peer_(std::move(peer)){}
    Status GetStatus(grpc::ServerContext* ctx,const w::StatusRequest*,v::StatusReply* out)override{return authorized(ctx,[&]{auto s=adapter_.status();out->set_journal_epoch(s.epoch);out->set_healthy(true);out->set_journal_format(4);out->set_high_watermark(s.high);});}
    Status RegisterBook(grpc::ServerContext* ctx,const w::RegisterBookRequest* r,v::CommandReceipt* out)override{return authorized(ctx,[&]{need(r->has_identity()&&r->has_book());auto i=identity(r->identity());receipt(adapter_,out,i,adapter_.register_book(i,key(r->book())));});}
    Status SubmitOrder(grpc::ServerContext* ctx,const w::SubmitOrderRequest* r,v::CommandReceipt* out)override{return authorized(ctx,[&]{need(r->has_identity()&&r->has_order()&&r->order().has_book());auto i=identity(r->identity());const auto& p=r->order();auto b=key(p.book());receipt(adapter_,out,i,adapter_.submit(i,{p.order_id(),p.account_id(),b.session_id,b.instrument_id,side(p.side()),p.limit_price_units(),p.quantity()}));});}
    Status CancelOrder(grpc::ServerContext* ctx,const w::CancelOrderRequest* r,v::CommandReceipt* out)override{return authorized(ctx,[&]{need(r->has_identity()&&r->has_book());auto i=identity(r->identity());receipt(adapter_,out,i,adapter_.cancel(i,key(r->book()),r->order_id(),r->account_id()));});}
    Status GetCommandReceipt(grpc::ServerContext* ctx,const w::ReceiptRequest* r,v::ReceiptReply* out)override{return authorized(ctx,[&]{need(r->has_identity());auto i=identity(r->identity());auto x=adapter_.receipt(i);if(x)receipt(adapter_,out->mutable_receipt(),i,*x);});}
    Status GetDeliveryBatch(grpc::ServerContext* ctx,const w::DeliveryRequest* r,v::DeliveryBatch* out)override{return authorized(ctx,[&]{auto page=adapter_.delivery(r->expected_journal_epoch(),r->after_command_sequence(),r->max_commands());out->set_journal_epoch(page.journal_epoch);out->set_high_watermark(page.high_watermark);for(const auto& x:page.records)record(out->add_records(),x);if(out->ByteSizeLong()>message_limit){out->Clear();throw a::Failure(a::Fault::Capacity,"Full page exceeds byte limit; request a smaller page, never skip a command");}});}
};
struct Running {
    Service service;int port=0;std::unique_ptr<grpc::Server> server;
    explicit Running(a::Adapter& a,const TlsConfig& tls):service(a,tls.allowed_peer){
        grpc::ServerBuilder b;b.SetMaxReceiveMessageSize(64*1024);b.SetMaxSendMessageSize(message_limit);
        b.AddListeningPort("127.0.0.1:0",tls.credentials,&port);b.RegisterService(&service);
        server=b.BuildAndStart();qt::require(server&&port>0,"Server start failed");
    }
    ~Running(){if(server){server->Shutdown();server->Wait();}}
};
}
int main(int argc,char** argv){try{
    const std::string mode=argc>1?argv[1]:"";
    const bool secure=mode=="--create-secure-journal"||mode=="--open-secure-journal";
    const bool plain=mode=="--create-dev-journal"||mode=="--open-dev-journal";
    const int required=secure?5:4;
    if((!secure&&!plain)||(argc!=required&&argc!=required+1)){
        std::cerr<<"Usage: qd_exchange_outcomes --create-secure-journal|--open-secure-journal <isolated-test-file> <UUIDv4> <TLS-directory> [--max-commands=512..2048]\n"
                 <<"Legacy plaintext test modes: --create-dev-journal|--open-dev-journal <isolated-test-file> <UUIDv4> [--max-commands=512..2048]\n";return 2;
    }
    const auto capacity=argc==required+1?a::parse_capacity(argv[required]):a::default_capacity;
    const auto tls=secure?tls_config(argv[4]):TlsConfig{grpc::InsecureServerCredentials(),{}};
    const bool create=mode=="--create-secure-journal"||mode=="--create-dev-journal";
    a::Adapter adapter(argv[2],create?d::JournalMode::CreateNew:d::JournalMode::OpenExisting,argv[3],capacity);
    Running server(adapter,tls);
    std::cout<<(secure?"MTLS V2 ONLY: authenticated exchange at 127.0.0.1:":"DEV V2 ONLY: unencrypted exchange at 127.0.0.1:")<<server.port<<"; journal V4"<<std::endl;
    server.server->Wait();return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
