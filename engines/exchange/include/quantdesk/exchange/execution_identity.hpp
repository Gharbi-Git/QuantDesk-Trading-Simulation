#pragma once
#include <cstdint>
#include <string>
#include <string_view>
namespace quantdesk::exchange {
// Epoch: canonical lowercase RFC variant UUIDv4, provisioned uniquely per journal.
void validate_journal_epoch(std::string_view epoch);
// UUIDv5(namespace=epoch, name=16 binary bytes: uint64 BE command sequence,
// uint64 BE zero-based fill index). Stable across replay and platforms.
[[nodiscard]] std::string execution_uuid(std::string_view epoch, std::int64_t command_sequence,
                                         std::uint64_t fill_index);
}
