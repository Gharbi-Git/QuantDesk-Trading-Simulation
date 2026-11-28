cmake_minimum_required(VERSION 3.25)
project(QuantDeskExchangeOutcomes LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT EXISTS "${QD_REPO_ROOT}/engines/exchange/src/persistence/durable_exchange.cpp")
  message(FATAL_ERROR "Set QD_REPO_ROOT to the M06 repository")
endif()
set(BUILD_TESTING OFF CACHE BOOL "Do not build repository tests" FORCE)
add_subdirectory("${QD_REPO_ROOT}/engines/exchange" exchange-domain EXCLUDE_FROM_ALL)
find_package(Protobuf CONFIG REQUIRED)
find_package(gRPC CONFIG REQUIRED)
find_package(Threads REQUIRED)
set(gen "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${gen}")
set(v1 "${QD_REPO_ROOT}/contracts/proto/quantdesk/v1")
set(v2 "${QD_REPO_ROOT}/contracts/proto/quantdesk/v2")
add_custom_command(OUTPUT "${gen}/exchange.pb.cc" "${gen}/exchange.pb.h"
    "${gen}/exchange_outcomes.pb.cc" "${gen}/exchange_outcomes.pb.h"
    "${gen}/exchange_outcomes.grpc.pb.cc" "${gen}/exchange_outcomes.grpc.pb.h"
  COMMAND $<TARGET_FILE:protobuf::protoc>
  ARGS "--proto_path=${v1}" "--proto_path=${v2}" "--cpp_out=${gen}"
       "${v1}/exchange.proto" "${v2}/exchange_outcomes.proto"
  COMMAND $<TARGET_FILE:protobuf::protoc>
  ARGS "--proto_path=${v1}" "--proto_path=${v2}" "--grpc_out=${gen}"
       "--plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>" "${v2}/exchange_outcomes.proto"
  DEPENDS "${v1}/exchange.proto" "${v2}/exchange_outcomes.proto" protobuf::protoc gRPC::grpc_cpp_plugin VERBATIM)
add_executable(qd_exchange_outcomes server.cpp "${gen}/exchange.pb.cc" "${gen}/exchange_outcomes.pb.cc" "${gen}/exchange_outcomes.grpc.pb.cc" "${gen}/exchange.pb.h" "${gen}/exchange_outcomes.pb.h" "${gen}/exchange_outcomes.grpc.pb.h")
target_include_directories(qd_exchange_outcomes PRIVATE "${gen}")
target_link_libraries(qd_exchange_outcomes PRIVATE quantdesk_exchange_domain gRPC::grpc++ protobuf::libprotobuf Threads::Threads)
add_executable(qd_outcomes_adapter_tests adapter_tests.cpp)
target_link_libraries(qd_outcomes_adapter_tests PRIVATE quantdesk_exchange_domain Threads::Threads)
enable_testing()
add_test(NAME outcomes_adapter COMMAND qd_outcomes_adapter_tests)
set_tests_properties(outcomes_adapter PROPERTIES TIMEOUT 180)
