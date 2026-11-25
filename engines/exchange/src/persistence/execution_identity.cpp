#include "quantdesk/exchange/execution_identity.hpp"
#include <array>
#include <bit>
#include <stdexcept>
namespace quantdesk::exchange {
namespace {
unsigned hex(char c) {
    if (c >= '0' && c <= '9') return static_cast<unsigned>(c - '0');
    if (c >= 'a' && c <= 'f') return static_cast<unsigned>(c - 'a' + 10);
    throw std::invalid_argument("Epoch must be a canonical lowercase UUIDv4");
}
std::array<unsigned char, 16> parse(std::string_view s) {
    if (s.size() != 36 || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-' || s[14] != '4' ||
        (s[19] != '8' && s[19] != '9' && s[19] != 'a' && s[19] != 'b'))
        throw std::invalid_argument("Epoch must be a canonical lowercase UUIDv4");
    std::array<unsigned char, 16> result{};
    std::size_t j = 0;
    for (std::size_t i = 0; i < s.size();) {
        if (i == 8 || i == 13 || i == 18 || i == 23) { ++i; continue; }
        result[j++] = static_cast<unsigned char>((hex(s[i]) << 4) | hex(s[i+1]));
        i += 2;
    }
    return result;
}
}
void validate_journal_epoch(std::string_view epoch) { (void)parse(epoch); }
std::string execution_uuid(std::string_view epoch, std::int64_t seq, std::uint64_t index) {
    if (seq <= 0) throw std::invalid_argument("Invalid command sequence");
    const auto ns = parse(epoch);
    // SHA-1 for UUIDv5 naming only, not authentication. Input is exactly 32 bytes,
    // so the padded message is a single 512-bit block (FIPS 180-4).
    std::array<unsigned char,64> block{};
    for (std::size_t i = 0; i < ns.size(); ++i) block[i] = ns[i];
    for (unsigned i = 0; i < 8; ++i) {
        block[16+i] = static_cast<unsigned char>(static_cast<std::uint64_t>(seq) >> (56-8*i));
        block[24+i] = static_cast<unsigned char>(index >> (56-8*i));
    }
    block[32] = 0x80; block[62] = 1; // bit length 256, big endian
    std::array<std::uint32_t,80> w{};
    for (unsigned i = 0; i < 16; ++i) for (unsigned j = 0; j < 4; ++j)
        w[i] = (w[i] << 8) | block[4*i+j];
    for (unsigned i = 16; i < 80; ++i) w[i] = std::rotl(w[i-3]^w[i-8]^w[i-14]^w[i-16],1);
    constexpr std::array<std::uint32_t,5> initial{0x67452301u,0xefcdab89u,0x98badcfeu,0x10325476u,0xc3d2e1f0u};
    auto a=initial[0], b=initial[1], c=initial[2], d=initial[3], e=initial[4];
    for (unsigned i=0; i<80; ++i) {
        std::uint32_t f{},k{};
        if(i<20){f=(b&c)|(~b&d);k=0x5a827999u;}
        else if(i<40){f=b^c^d;k=0x6ed9eba1u;}
        else if(i<60){f=(b&c)|(b&d)|(c&d);k=0x8f1bbcdcu;}
        else{f=b^c^d;k=0xca62c1d6u;}
        const auto t=std::rotl(a,5)+f+e+k+w[i]; e=d;d=c;c=std::rotl(b,30);b=a;a=t;
    }
    const std::array<std::uint32_t,4> h{a+initial[0],b+initial[1],c+initial[2],d+initial[3]};
    std::array<unsigned char,16> bytes{};
    for(unsigned i=0;i<16;++i) bytes[i]=static_cast<unsigned char>(h[i/4]>>(24-8*(i%4)));
    bytes[6]=static_cast<unsigned char>((bytes[6]&0x0f)|0x50);
    bytes[8]=static_cast<unsigned char>((bytes[8]&0x3f)|0x80);
    constexpr char digits[]="0123456789abcdef";
    std::string result; result.reserve(36);
    for(unsigned i=0;i<16;++i){if(i==4||i==6||i==8||i==10)result+='-';result+=digits[bytes[i]>>4];result+=digits[bytes[i]&15];}
    return result;
}
}
