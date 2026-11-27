#pragma once
#include <filesystem>
#include <random>
#include <stdexcept>
#include <string>
namespace qt {
inline void require(bool b,const char* s){if(!b)throw std::runtime_error(s);}
struct Temp {
    std::filesystem::path dir;
    Temp(){std::random_device r;for(int n=0;n<16;++n){auto p=std::filesystem::temp_directory_path()/("qd-trading-test-"+std::to_string(r())+"-"+std::to_string(r()));if(std::filesystem::create_directory(p)){dir=p;return;}}throw std::runtime_error("Cannot create isolated directory");}
    ~Temp(){std::error_code e;std::filesystem::remove_all(dir,e);}
};
inline const std::string epoch="12345678-1234-4234-8234-123456789abc";
inline std::string id(unsigned n){auto s=std::to_string(n);return "12345678-0000-4000-8000-"+std::string(12-s.size(),'0')+s;}
}
