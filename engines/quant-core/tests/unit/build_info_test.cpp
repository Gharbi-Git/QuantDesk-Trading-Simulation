#include "quantdesk/quant/build_info.hpp"
#include <string_view>
int main() {
    return quantdesk::quant::build_component_name() == std::string_view{"quant-core"} ? 0 : 1;
}