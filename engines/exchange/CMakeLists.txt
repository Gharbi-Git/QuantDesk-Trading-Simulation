add_executable(quantdesk_exchange src/main.cpp)
target_compile_features(quantdesk_exchange PRIVATE cxx_std_20)
add_test(NAME exchange_bootstrap COMMAND quantdesk_exchange)

# M06: standalone order domain and lifecycle tests.
add_library(quantdesk_exchange_domain STATIC src/commands/order.cpp)
target_include_directories(quantdesk_exchange_domain PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_features(quantdesk_exchange_domain PUBLIC cxx_std_20)
target_link_libraries(quantdesk_exchange PRIVATE quantdesk_exchange_domain)

if(BUILD_TESTING)
    add_executable(quantdesk_exchange_order_tests tests/unit/order_tests.cpp)
    target_link_libraries(quantdesk_exchange_order_tests PRIVATE quantdesk_exchange_domain)
    foreach(case IN ITEMS valid_order invalid_request notional_overflow partial_and_full_fill buy_limit sell_limit invalid_fill cancellation terminal_fill)
        add_test(NAME exchange_order_${case} COMMAND quantdesk_exchange_order_tests ${case})
    endforeach()
endif()

# M06: in-memory limit order book and matching tests.
target_sources(quantdesk_exchange_domain PRIVATE src/orderbook/order_book.cpp)
if(BUILD_TESTING)
    add_executable(quantdesk_exchange_book_tests tests/unit/order_book_tests.cpp)
    target_link_libraries(quantdesk_exchange_book_tests PRIVATE quantdesk_exchange_domain)
    foreach(case IN ITEMS resting_and_depth buy_price_priority sell_price_priority fifo_both_sides partial_rest_and_priority limit_boundary cancel_and_fifo cancel_partial_and_ownership duplicate_and_invalid session_instrument_isolation self_trade_atomic_rejection execution_fields_and_sequence depth_overflow_atomic randomized_conservation)
        add_test(NAME exchange_book_${case} COMMAND quantdesk_exchange_book_tests ${case})
    endforeach()
endif()

# M06: multi-session and instrument routing.
target_sources(quantdesk_exchange_domain PRIVATE src/commands/exchange.cpp)
if(BUILD_TESTING)
    add_executable(quantdesk_exchange_router_tests tests/unit/exchange_tests.cpp)
    target_link_libraries(quantdesk_exchange_router_tests PRIVATE quantdesk_exchange_domain)
    foreach(case IN ITEMS registration unknown_book session_isolation instrument_isolation global_order_id scoped_lookup_cancel rejection_index_rollback overflow_index_rollback scoped_sequence stable_index)
        add_test(NAME exchange_router_${case} COMMAND quantdesk_exchange_router_tests ${case})
    endforeach()
endif()

# M06: durable command journal and deterministic recovery.
target_sources(quantdesk_exchange_domain PRIVATE src/persistence/durable_exchange.cpp)
if(BUILD_TESTING)
    add_executable(quantdesk_exchange_recovery_tests tests/recovery/durable_exchange_tests.cpp)
    target_link_libraries(quantdesk_exchange_recovery_tests PRIVATE quantdesk_exchange_domain)
    foreach(case IN ITEMS replay duplicate conflict cancellation rejection corrupt_record torn_record exclusive_and_modes fail_closed invalid_header)
        add_test(NAME exchange_recovery_${case} COMMAND quantdesk_exchange_recovery_tests ${case})
    endforeach()
endif()

# M06: durable execution identities and ordered delivery feed.
target_sources(quantdesk_exchange_domain PRIVATE src/persistence/execution_identity.cpp)
if(BUILD_TESTING)
    add_executable(quantdesk_exchange_delivery_tests tests/recovery/execution_delivery_tests.cpp)
    target_link_libraries(quantdesk_exchange_delivery_tests PRIVATE quantdesk_exchange_domain)
    foreach(case IN ITEMS uuid_vectors ordered_page fill_payload restart_retry cancel_payload cursor_errors epoch_binding legacy independent_ids failed_write consumer_restart)
        add_test(NAME exchange_delivery_${case} COMMAND quantdesk_exchange_delivery_tests ${case})
    endforeach()
endif()

# M06: V3 durable UTC execution timestamps.
if(BUILD_TESTING)
    add_executable(quantdesk_exchange_timestamp_tests tests/recovery/timestamp_tests.cpp)
    target_link_libraries(quantdesk_exchange_timestamp_tests PRIVATE quantdesk_exchange_domain)
    foreach(case IN ITEMS multiple_fills restart_retry persisted_clock legacy_isolation invalid_timestamp failed_append torn_and_crc reject_no_append)
        add_test(NAME exchange_timestamp_${case} COMMAND quantdesk_exchange_timestamp_tests ${case})
    endforeach()
endif()
