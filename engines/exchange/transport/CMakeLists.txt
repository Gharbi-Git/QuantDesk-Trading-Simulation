cmake_minimum_required(VERSION 3.25)
project(QuantDeskExchangeTrading LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT EXISTS "${QD_REPO_ROOT}/engines/exchange/src/persistence/durable_exchange.cpp")
    message(FATAL_ERROR "Set QD_REPO_ROOT to the checked-out M06 repository")
endif()
set(BUILD_TESTING OFF CACHE BOOL "Do not build repository tests" FORCE)
add_subdirectory("${QD_REPO_ROOT}/engines/exchange" exchange-domain EXCLUDE_FROM_ALL)
find_package(Protobuf CONFIG REQUIRED)
find_package(gRPC CONFIG REQUIRED)
find_package(Threads REQUIRED)
set(gen "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${gen}")
add_custom_command(
    OUTPUT "${gen}/exchange.pb.cc" "${gen}/exchange.pb.h"
           "${gen}/exchange.grpc.pb.cc" "${gen}/exchange.grpc.pb.h"
    COMMAND $<TARGET_FILE:protobuf::protoc>
    ARGS "--proto_path=${QD_REPO_ROOT}/contracts/proto/quantdesk/v1"
         "--cpp_out=${gen}" "--grpc_out=${gen}"
         "--plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>"
         "${QD_REPO_ROOT}/contracts/proto/quantdesk/v1/exchange.proto"
    DEPENDS "${QD_REPO_ROOT}/contracts/proto/quantdesk/v1/exchange.proto" protobuf::protoc gRPC::grpc_cpp_plugin
    VERBATIM)
add_executable(qd_exchange_trading server.cpp "${gen}/exchange.pb.cc" "${gen}/exchange.grpc.pb.cc"
    "${gen}/exchange.pb.h" "${gen}/exchange.grpc.pb.h")
target_include_directories(qd_exchange_trading PRIVATE "${gen}")
target_link_libraries(qd_exchange_trading PRIVATE quantdesk_exchange_domain gRPC::grpc++ protobuf::libprotobuf)
add_executable(qd_exchange_adapter_tests adapter_tests.cpp)
target_link_libraries(qd_exchange_adapter_tests PRIVATE quantdesk_exchange_domain Threads::Threads)
enable_testing()
add_test(NAME exchange_adapter COMMAND qd_exchange_adapter_tests)
set_tests_properties(exchange_adapter PROPERTIES TIMEOUT 60)
add_test(NAME exchange_trading_loopback COMMAND qd_exchange_trading --self-test)
set_tests_properties(exchange_trading_loopback PROPERTIES TIMEOUT 45)
