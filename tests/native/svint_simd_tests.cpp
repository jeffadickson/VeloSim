#include "../../native/numeric/svint_simd.h"
#include "../../native/rtl/runtime.h"
#include <chrono>
#include <iostream>
#include <random>
#include <string>

namespace {
using vsnumeric::Backend;
using vsnumeric::BitOperation;
using slang::SVInt;
void require(bool condition, const std::string& message) { if (!condition) throw std::runtime_error(message); }
SVInt parse(const std::string& text, bool sign) { return SVInt::fromString(std::to_string(text.size()) + (sign ? "'sb" : "'b") + text); }
char expected(char a, char b, BitOperation op) {
    // Independent per-bit four-state table; X and Z are unknown operands.
    if (op == BitOperation::and_) return a == '0' || b == '0' ? '0' : a == '1' && b == '1' ? '1' : 'x';
    if (op == BitOperation::or_) return a == '1' || b == '1' ? '1' : a == '0' && b == '0' ? '0' : 'x';
    return (a != '0' && a != '1') || (b != '0' && b != '1') ? 'x' : a == b ? '0' : '1';
}
SVInt upstream(const SVInt& a, const SVInt& b, BitOperation op) {
    return op == BitOperation::and_ ? a & b : op == BitOperation::or_ ? a | b : a ^ b;
}
size_t cases = 0;
size_t currentCases = 0;
void verify(const std::string& a, const std::string& b, bool sign) {
    const auto left = parse(a, sign), right = parse(b, sign);
    for (auto op : {BitOperation::and_, BitOperation::or_, BitOperation::xor_}) {
        std::string bits(a.size(), '0');
        for (size_t i = 0; i < a.size(); ++i) bits[i] = expected(a[i], b[i], op);
        const auto oracle = parse(bits, sign), reference = upstream(left, right, op);
        require(exactlyEqual(reference, oracle), "upstream disagrees with truth table");
        if(a.size()<=256) {
            const auto current=vsrtl::bit_binary(vsrtl::parse(a),vsrtl::parse(b),int(a.size()),
                op==BitOperation::and_?0:op==BitOperation::or_?1:2);
            require(vsrtl::text(current)==bits,"existing VeloSim runtime disagrees with truth table");
            ++currentCases;
        }
        for (auto backend : {Backend::scalar, Backend::automatic, Backend::avx128}) {
            if (backend == Backend::avx128 && !vsnumeric::avx128Available()) continue;
            const auto result = vsnumeric::bitwise(left, right, op, backend);
            require(exactlyEqual(result, oracle) && result.isSigned() == reference.isSigned() && result.getBitWidth() == left.getBitWidth(),
                    "SIMD mismatch case=" + std::to_string(cases) + " width=" + std::to_string(a.size()));
            ++cases;
        }
        // Identical operands exercise the same raw-plane pointers in both inputs.
        const auto alias = vsnumeric::bitwise(left, left, op);
        require(exactlyEqual(alias, upstream(left, left, op)), "self-alias mismatch");
    }
}
template<class Error, class F> void rejects(F action) {
    try { action(); } catch (const Error&) { return; }
    throw std::runtime_error("missing prototype rejection");
}
long long measure(auto action) {
    auto start = std::chrono::steady_clock::now(); action();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count();
}
}
int main() { try {
    constexpr unsigned seed = 20261028;
    std::mt19937 random(seed);
    for (bool sign : {false, true}) {
        for (char a : std::string("01xz")) for (char b : std::string("01xz")) verify(std::string(1,a), std::string(1,b), sign);
        for (unsigned width : {1,2,63,64,65,127,128,129,191,192,193,255,256,257,511,512,513,1024,4096}) {
            for (char a : std::string("01xz")) for (char b : std::string("01xz")) verify(std::string(width,a), std::string(width,b), sign);
            for (unsigned sample = 0; sample < 64; ++sample) {
                std::string a(width,'0'), b(width,'0');
                // Include two-state, mixed known/unknown and both-unknown planes.
                for (unsigned i = 0; i < width; ++i) {
                    a[i] = "01xz"[random() % (sample % 3 ? 4 : 2)];
                    b[i] = "01xz"[random() % (sample % 3 == 2 ? 4 : 2)];
                }
                verify(a, b, sign);
            }
        }
    }
    const auto one = parse("1",false), two = parse("01",false);
    rejects<std::invalid_argument>([&]{vsnumeric::bitwise(one,two,BitOperation::and_);});
    rejects<std::invalid_argument>([&]{vsnumeric::bitwise(one,parse("1",true),BitOperation::and_);});
    rejects<std::invalid_argument>([&]{vsnumeric::bitwise(one,one,static_cast<BitOperation>(99));});
    rejects<std::invalid_argument>([&]{vsnumeric::bitwise(one,one,BitOperation::and_,static_cast<Backend>(99));});
    if (!vsnumeric::avx128Available()) rejects<std::runtime_error>([&]{vsnumeric::bitwise(two,two,BitOperation::xor_,Backend::avx128);});

    // Compare complete adapters, including allocation/result-copy overhead.
    // A changing checksum prevents the compiler removing the work. This is not
    // an isolated instruction-throughput or full-model speed measurement.
    std::cout << "{\"status\":\"pass\",\"seed\":" << seed << ",\"cases\":" << cases
              << ",\"current_runtime_cases\":" << currentCases << ",\"avx128\":" << (vsnumeric::avx128Available() ? "true" : "false") << ",\"timings\":[";
    bool first = true;
    for (unsigned width : {128,256,1024,4096}) {
        const auto a = parse(std::string(width,'1'),false), b = parse(std::string(width,'0').replace(0,1,"1"),false);
        constexpr unsigned iterations = 20000;
        for (unsigned trial = 0; trial < 4; ++trial) {
            SVInt reference, scalar, simd;
            auto run = [&](auto function, SVInt& result) { auto value=a; for(unsigned i=0;i<iterations;++i) value=function(value,b); result=std::move(value); };
            auto baseNs = measure([&]{run([](const auto& x,const auto& y){return x^y;},reference);});
            auto scalarNs = measure([&]{run([](const auto& x,const auto& y){return vsnumeric::bitwise(x,y,BitOperation::xor_,Backend::scalar);},scalar);});
            auto simdNs = measure([&]{run([](const auto& x,const auto& y){return vsnumeric::bitwise(x,y,BitOperation::xor_);},simd);});
            require(exactlyEqual(reference,scalar) && exactlyEqual(reference,simd) && exactlyEqual(reference,a),"benchmark checksum mismatch");
            if (!trial) continue;
            if (!first) std::cout << ','; first=false;
            std::cout << "{\"width\":" << width << ",\"iterations\":" << iterations << ",\"upstream_ns\":" << baseNs << ",\"scalar_adapter_ns\":" << scalarNs << ",\"dispatched_adapter_ns\":" << simdNs << '}';
        }
    }
    std::cout << "]}\n";
    return 0;
} catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; } }
