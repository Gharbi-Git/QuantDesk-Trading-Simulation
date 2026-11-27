#include "quantdesk/exchange/durable_exchange.hpp"
#include "quantdesk/exchange/execution_identity.hpp"
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <limits>
#include <stdexcept>
#include <string_view>
#include <utility>
#ifdef _WIN32
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <cerrno>
#include <fcntl.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace quantdesk::exchange {
namespace {
constexpr std::string_view legacy_header = "QD-JOURNAL-1\n";
std::string journal_header(const std::string& epoch, bool timestamped, bool outcomes) {
    if (epoch.empty()) return std::string(legacy_header);
    validate_journal_epoch(epoch);
    return std::string(outcomes ? "QD-JOURNAL-4\n" : timestamped ? "QD-JOURNAL-3\n" : "QD-JOURNAL-2\n") + epoch + "\n";
}
constexpr std::size_t max_record = 2048;
constexpr std::size_t max_file = 64 * 1024 * 1024;
[[noreturn]] void io_error() { throw std::runtime_error("Journal I/O failure; recovery required"); }
[[noreturn]] void corrupt() { throw std::runtime_error("Invalid or incomplete journal; recovery required"); }
class File {
#ifdef _WIN32
    HANDLE handle_ = INVALID_HANDLE_VALUE;
#else
    int handle_ = -1;
#endif
    std::size_t limit_;
public:
    File(const std::filesystem::path& path, JournalMode mode, std::size_t limit, std::string_view header) : limit_(limit) {
        if (limit < header.size() || limit > max_file) throw std::invalid_argument("Invalid journal byte limit");
#ifdef _WIN32
        handle_ = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            mode == JournalMode::CreateNew ? CREATE_NEW : OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle_ == INVALID_HANDLE_VALUE) io_error();
#else
        handle_ = ::open(path.c_str(), O_RDWR | O_CLOEXEC | (mode == JournalMode::CreateNew ? O_CREAT | O_EXCL : 0), 0600);
        if (handle_ < 0) io_error();
        if (::flock(handle_, LOCK_EX | LOCK_NB) != 0) { ::close(handle_); handle_ = -1; io_error(); }
#endif
        try {
            if (mode == JournalMode::CreateNew) {
                append(header);
#ifndef _WIN32
                const auto parent = path.parent_path().empty() ? std::filesystem::path(".") : path.parent_path();
                const int dir = ::open(parent.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
                if (dir < 0) io_error();
                const int result = ::fsync(dir);
                ::close(dir);
                if (result != 0) io_error();
#endif
            }
        } catch (...) { close(); throw; }
    }
    ~File() { close(); }
    File(const File&) = delete;
    File& operator=(const File&) = delete;
    void close() noexcept {
#ifdef _WIN32
        if (handle_ != INVALID_HANDLE_VALUE) { CloseHandle(handle_); handle_ = INVALID_HANDLE_VALUE; }
#else
        if (handle_ >= 0) { ::close(handle_); handle_ = -1; }
#endif
    }
    std::string read_all() {
#ifdef _WIN32
        LARGE_INTEGER size{}, zero{};
        if (!GetFileSizeEx(handle_, &size) || size.QuadPart < 0 || size.QuadPart > static_cast<LONGLONG>(limit_) ||
            !SetFilePointerEx(handle_, zero, nullptr, FILE_BEGIN)) io_error();
        std::string data(static_cast<std::size_t>(size.QuadPart), '\0');
        DWORD n = 0;
        if (!ReadFile(handle_, data.data(), static_cast<DWORD>(data.size()), &n, nullptr) || n != data.size()) io_error();
#else
        struct stat st{};
        if (::fstat(handle_, &st) != 0 || !S_ISREG(st.st_mode) || st.st_size < 0 ||
            st.st_size > static_cast<off_t>(limit_) || ::lseek(handle_, 0, SEEK_SET) < 0) io_error();
        std::string data(static_cast<std::size_t>(st.st_size), '\0');
        std::size_t offset = 0;
        while (offset < data.size()) {
            const auto n = ::read(handle_, data.data() + offset, data.size() - offset);
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) io_error();
            offset += static_cast<std::size_t>(n);
        }
#endif
        return data;
    }
    void append(std::string_view bytes) {
        if (bytes.size() > limit_) io_error();
#ifdef _WIN32
        LARGE_INTEGER zero{}, end{};
        if (!SetFilePointerEx(handle_, zero, &end, FILE_END) || end.QuadPart < 0 ||
            end.QuadPart > static_cast<LONGLONG>(limit_ - bytes.size())) io_error();
        DWORD n = 0;
        if (!WriteFile(handle_, bytes.data(), static_cast<DWORD>(bytes.size()), &n, nullptr) || n != bytes.size() || !FlushFileBuffers(handle_)) io_error();
#else
        const auto end = ::lseek(handle_, 0, SEEK_END);
        if (end < 0 || end > static_cast<off_t>(limit_ - bytes.size())) io_error();
        std::size_t offset = 0;
        while (offset < bytes.size()) {
            const auto n = ::write(handle_, bytes.data() + offset, bytes.size() - offset);
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) io_error();
            offset += static_cast<std::size_t>(n);
        }
        if (::fsync(handle_) != 0) io_error();
#endif
    }
};
void put(std::string& s, std::uint64_t value, unsigned width) {
    for (unsigned i = 0; i < width; ++i) s.push_back(static_cast<char>((value >> (8 * i)) & 255));
}
std::uint64_t get(std::string_view& s, unsigned width) {
    if (s.size() < width) corrupt();
    std::uint64_t result = 0;
    for (unsigned i = 0; i < width; ++i) result |= static_cast<std::uint64_t>(static_cast<unsigned char>(s[i])) << (8 * i);
    s.remove_prefix(width);
    return result;
}
void id(std::string& s, const std::string& value) {
    if (value.empty() || value.size() > 128 || value.find_first_of(" \t\r\n\f\v") != std::string::npos || value.find('\0') != std::string::npos)
        throw std::invalid_argument("Invalid journal identifier");
    put(s, value.size(), 2); s += value;
}
std::string read_id(std::string_view& s) {
    const auto n = get(s, 2);
    if (n == 0 || n > 128 || n > s.size()) corrupt();
    std::string result(s.substr(0, static_cast<std::size_t>(n)));
    s.remove_prefix(static_cast<std::size_t>(n));
    std::string checked; id(checked, result);
    return result;
}
std::uint32_t crc(std::string_view s) {
    std::uint32_t c = 0xffffffffu;
    for (char ch : s) {
        c ^= static_cast<unsigned char>(ch);
        for (int i = 0; i < 8; ++i) c = (c >> 1) ^ ((c & 1u) ? 0xedb88320u : 0u);
    }
    return ~c;
}
// Keep the range representable by the .NET UTC DateTimeOffset consumer.
constexpr std::uint64_t max_utc_micros = 253402300799999999ULL;
std::int64_t utc_now_micros() {
    const auto t = std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    if (t <= 0 || static_cast<std::uint64_t>(t) > max_utc_micros)
        throw std::runtime_error("UTC clock out of supported range");
    return static_cast<std::int64_t>(t);
}
std::string frame(std::int64_t sequence, std::string_view command, std::optional<std::int64_t> timestamp, std::optional<CommandOutcome> outcome = std::nullopt) {
    std::string body; put(body, static_cast<std::uint64_t>(sequence), 8);
    if (timestamp) put(body, static_cast<std::uint64_t>(*timestamp), 8);
    if (outcome) put(body, static_cast<std::uint64_t>(*outcome), 1);
    body += command;
    std::string result; put(result, body.size(), 4); result += body; put(result, crc(body), 4); return result;
}
std::string encode(char type, const std::string& command_id, const BookKey& key) {
    std::string result(1, type); id(result, command_id); id(result, key.session_id); id(result, key.instrument_id); return result;
}
struct Decoded {
    char type;
    std::string command_id;
    BookKey key;
    LimitOrderRequest request{};
};
Decoded decode(std::string_view s) {
    if (s.empty()) corrupt();
    Decoded d{}; d.type = s.front(); s.remove_prefix(1);
    d.command_id = read_id(s); d.key = {read_id(s), read_id(s)};
    if (d.type == 'S' || d.type == 'C') {
        d.request.order_id = read_id(s); d.request.account_id = read_id(s);
        d.request.session_id = d.key.session_id; d.request.instrument_id = d.key.instrument_id;
    }
    if (d.type == 'S') {
        const auto side = get(s, 1); if (side > 1) corrupt();
        d.request.side = side == 0 ? Side::Buy : Side::Sell;
        const auto price = get(s, 8), qty = get(s, 8);
        if (price > static_cast<std::uint64_t>(std::numeric_limits<Price>::max()) || qty > static_cast<std::uint64_t>(std::numeric_limits<Quantity>::max())) corrupt();
        d.request.limit_price = static_cast<Price>(price); d.request.quantity = static_cast<Quantity>(qty);
    } else if (d.type != 'C' && d.type != 'R') corrupt();
    if (!s.empty()) corrupt();
    return d;
}
}
struct DurableExchange::Impl {
    const std::string epoch;
    const bool timestamped;
    const bool outcomes;
    const std::string header;
    File file;
    Exchange exchange;
    struct Entry { std::string command; CommandReceipt receipt; std::optional<std::int64_t> timestamp; };
    std::map<std::string, Entry> entries;
    // Map nodes are stable; index is rebuilt deterministically on restart.
    std::vector<const Entry*> ordered;
    std::int64_t sequence = 0;
    bool ok = true;
    Impl(const std::filesystem::path& path, JournalMode mode, std::size_t limit, std::string journal_epoch, bool with_timestamps = false, bool with_outcomes = false)
        : epoch(std::move(journal_epoch)), timestamped(with_timestamps), outcomes(with_outcomes),
          header(journal_header(epoch, timestamped, outcomes)), file(path, mode, limit, header) {
        auto data = file.read_all(); std::string_view input(data);
        if (!input.starts_with(header)) corrupt();
        input.remove_prefix(header.size());
        while (!input.empty()) {
            const auto n = get(input, 4);
            if (n < (outcomes ? 18u : timestamped ? 17u : 9u) || n > max_record || n > input.size()) corrupt();
            auto body = input.substr(0, static_cast<std::size_t>(n)); input.remove_prefix(static_cast<std::size_t>(n));
            if (get(input, 4) != crc(body)) corrupt();
            if (get(body, 8) != static_cast<std::uint64_t>(sequence + 1)) corrupt();
            std::optional<std::int64_t> timestamp;
            if (timestamped) {
                const auto raw = get(body, 8);
                if (raw == 0 || raw > max_utc_micros) corrupt();
                timestamp = static_cast<std::int64_t>(raw);
            }
            auto expected = CommandOutcome::Accepted;
            if (outcomes) {
                const auto raw = get(body, 1);
                if (raw > static_cast<std::uint64_t>(CommandOutcome::NumericRejected)) corrupt();
                expected = static_cast<CommandOutcome>(raw);
            }
            auto d = decode(body);
            if (entries.contains(d.command_id)) corrupt();
            if (d.type == 'S') (void)Order(d.request);
            auto result = decide(d, sequence + 1);
            if (result.outcome != expected) corrupt();
            const auto it = entries.emplace(d.command_id, Entry{std::string(body), std::move(result), timestamp}).first;
            ordered.push_back(&it->second); ++sequence;
        }
    }
    void check() const { if (!ok) throw std::runtime_error("Exchange is poisoned; restart and recover"); }
    CommandReceipt apply(const Decoded& d, std::int64_t seq) {
        CommandReceipt receipt{seq, std::nullopt};
        if (d.type == 'R') exchange.register_book(d.key);
        else if (d.type == 'S') receipt.submission = exchange.submit(d.request);
        else {
            const auto* order = exchange.find(d.key, d.request.order_id, d.request.account_id);
            if (!order || order->state() == OrderState::Filled) throw std::invalid_argument("Order not cancellable");
            exchange.cancel(d.key, d.request.order_id, d.request.account_id);
        }
        return receipt;
    }
    CommandReceipt decide(const Decoded& d, std::int64_t seq) {
        // Only documented atomic domain failures can become definitive rejections.
        // No clock, storage, allocation or unexpected failure becomes a rejection.
        try { return apply(d, seq); }
        catch (const std::invalid_argument&) {
            if (!outcomes) throw;
            return {seq, std::nullopt, CommandOutcome::DomainRejected};
        }
        catch (const std::overflow_error&) {
            if (!outcomes) throw;
            return {seq, std::nullopt, CommandOutcome::NumericRejected};
        }
    }
    CommandReceipt execute(std::string command) {
        check();
        const auto d = decode(command);
        if (const auto it = entries.find(d.command_id); it != entries.end()) {
            if (it->second.command != command) throw std::invalid_argument("Command ID reused with different payload");
            return it->second.receipt;
        }
        // Only a NEW command samples the clock; duplicate equality is still payload-only.
        // Sample before applying anything, so a bad clock cannot mutate the book.
        std::optional<std::int64_t> timestamp;
        if (timestamped) timestamp = utc_now_micros();
        // Domain rejects are atomic by the existing book contract. Unexpected
        // failures are never acknowledged, and all future operations fail closed.
        CommandReceipt result{sequence + 1, std::nullopt};
        try { result = decide(d, sequence + 1); }
        catch (const std::invalid_argument&) { throw; }
        catch (const std::overflow_error&) { throw; }
        catch (...) { ok = false; throw; }
        try {
            const auto bytes = frame(sequence + 1, command, timestamp,
                outcomes ? std::optional{result.outcome} : std::nullopt);
            file.append(bytes);
            const auto it = entries.emplace(d.command_id, Entry{std::move(command), result, timestamp}).first;
            ordered.push_back(&it->second);
            ++sequence;
        } catch (...) { ok = false; throw; }
        return result;
    }
};
DurableExchange::DurableExchange(const std::filesystem::path& p, JournalMode m, std::size_t limit) : impl_(std::make_unique<Impl>(p, m, limit, std::string{})) {}
DurableExchange::DurableExchange(const std::filesystem::path& p, JournalMode m, const std::string& epoch, std::size_t limit) {
    validate_journal_epoch(epoch);
    impl_ = std::make_unique<Impl>(p, m, limit, epoch);
}
DurableExchange::DurableExchange(const std::filesystem::path& p, JournalMode m, const std::string& epoch, JournalFormat format, std::size_t limit) {
    validate_journal_epoch(epoch);
    if (format != JournalFormat::EpochV2 && format != JournalFormat::TimestampedV3 && format != JournalFormat::OutcomesV4)
        throw std::invalid_argument("Unsupported journal format");
    impl_ = std::make_unique<Impl>(p, m, limit, epoch, format != JournalFormat::EpochV2, format == JournalFormat::OutcomesV4);
}
DurableExchange::~DurableExchange() = default;
bool DurableExchange::healthy() const noexcept { return impl_->ok; }
const Exchange& DurableExchange::view() const { impl_->check(); return impl_->exchange; }
std::optional<CommandReceipt> DurableExchange::receipt(const std::string& command_id) const {
    impl_->check(); const auto it = impl_->entries.find(command_id);
    if (it == impl_->entries.end()) return std::nullopt;
    return it->second.receipt;
}
DeliveryBatch DurableExchange::delivery_after(const std::string& epoch, std::int64_t after, std::size_t max_commands) const {
    impl_->check();
    if (impl_->epoch.empty() || epoch != impl_->epoch) throw std::invalid_argument("Delivery epoch mismatch or legacy journal");
    if (after < 0 || after > impl_->sequence || max_commands == 0 || max_commands > 256)
        throw std::invalid_argument("Invalid delivery cursor or page size");
    DeliveryBatch batch{impl_->epoch, impl_->sequence, {}};
    const auto start = static_cast<std::size_t>(after);
    const auto count = std::min(max_commands, impl_->ordered.size() - start);
    batch.records.reserve(count);
    for (std::size_t i = start; i < start + count; ++i) {
        const auto& entry = *impl_->ordered.at(i);
        const auto d = decode(entry.command);
        DeliveryRecord record{entry.receipt.command_sequence, d.command_id,
            d.type == 'S' ? DeliveryKind::Submit : d.type == 'C' ? DeliveryKind::Cancel : DeliveryKind::RegisterBook,
            d.key, d.request.order_id, d.request.account_id, std::nullopt, std::nullopt, 0, 0, {}};
        record.outcome = entry.receipt.outcome;
        // Even a rejected submission carries its full immutable order payload.
        if (d.type == 'S') record.request = d.request;
        if (entry.receipt.submission) {
            const auto& sub = *entry.receipt.submission;
            record.request = d.request;
            record.state = sub.state;
            record.filled_quantity = sub.filled_quantity;
            record.open_quantity = sub.open_quantity;
            record.fills.reserve(sub.executions.size());
            for (std::size_t j = 0; j < sub.executions.size(); ++j)
                record.fills.push_back({execution_uuid(impl_->epoch, record.command_sequence, static_cast<std::uint64_t>(j)), sub.executions[j], entry.timestamp});
        }
        batch.records.push_back(std::move(record));
    }
    return batch;
}
CommandReceipt DurableExchange::register_book(const std::string& command_id, const BookKey& key) { return impl_->execute(encode('R', command_id, key)); }
CommandReceipt DurableExchange::submit(const std::string& command_id, const LimitOrderRequest& r) {
    (void)Order(r); // validate signed values before encoding
    auto bytes = encode('S', command_id, {r.session_id, r.instrument_id}); id(bytes, r.order_id); id(bytes, r.account_id);
    put(bytes, r.side == Side::Buy ? 0 : 1, 1); put(bytes, static_cast<std::uint64_t>(r.limit_price), 8); put(bytes, static_cast<std::uint64_t>(r.quantity), 8);
    return impl_->execute(std::move(bytes));
}
CommandReceipt DurableExchange::cancel(const std::string& command_id, const BookKey& key, const std::string& order_id, const std::string& account_id) {
    auto bytes = encode('C', command_id, key); id(bytes, order_id); id(bytes, account_id); return impl_->execute(std::move(bytes));
}
}
