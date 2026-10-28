#include "Vtimed_counter.h"
#include <iostream>

int main() {
    try {
        Vtimed_counter model;
        model.eval();
        while (model.eventsPending()) {
            const auto next = model.nextTimeSlot();
            if (!next || *next > 12) break;
            model.timeInc(*next - model.time());
            model.eval();
            std::cout << model.time() << ' ' << model.count.bits() << ' ' << model.delayed.bits() << '\n';
        }
        model.final();
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
