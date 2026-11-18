#include "quantdesk/quant/build_info.hpp"
#include <iostream>
int main() {
    std::cout << "QuantDesk backtest scaffold (no replay implemented yet), linked to "
              << quantdesk::quant::build_component_name() << '\n';
    return 0;
}