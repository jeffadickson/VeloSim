#include "../../native/numeric/svint_simd.h"
#include "../../native/rtl/runtime.h"
#include <array>
#include <chrono>
#include <iostream>
#include <random>
#include <string>

namespace {
using slang::SVInt;
using vsnumeric::BitOperation;
struct Pair { SVInt a,b; };
struct Sample { int64_t ns; uint64_t checksum; };
volatile uint64_t observed=0;

SVInt upstream(const Pair& pair,BitOperation operation) {
    return operation==BitOperation::and_?pair.a&pair.b:operation==BitOperation::or_?pair.a|pair.b:pair.a^pair.b;
}
SVInt parse(const std::string& text) { return SVInt::fromString(std::to_string(text.size())+"'b"+text); }
char truth(char a,char b,BitOperation op) {
    if(op==BitOperation::and_) return a=='0'||b=='0'?'0':a=='1'&&b=='1'?'1':'x';
    if(op==BitOperation::or_) return a=='1'||b=='1'?'1':a=='0'&&b=='0'?'0':'x';
    return (a!='0'&&a!='1')||(b!='0'&&b!='1')?'x':a==b?'0':'1';
}

// Observe every result word, including its unknown plane and metadata. Hashing
// is identical for all SVInt backends and included in the measured operation.
// Parsing/input preparation are outside the timed loop; result allocation,
// conversion/copy and destruction remain inside it.
template<class Action> Sample measure(const std::vector<Pair>& inputs,unsigned iterations,Action action) {
    uint64_t checksum=1469598103934665603ULL;
    const auto start=std::chrono::steady_clock::now();
    for(unsigned i=0;i<iterations;++i) {
        auto value=action(inputs[(i*13+7)%inputs.size()]);
        checksum=(checksum^value.getBitWidth())*1099511628211ULL;
        checksum=(checksum^uint64_t(value.hasUnknown()))*1099511628211ULL;
        for(unsigned word=0;word<value.getNumWords();++word)
            checksum=(checksum^value.getRawPtr()[word])*1099511628211ULL;
    }
    const auto ns=std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now()-start).count();
    observed=checksum;
    return {ns,checksum};
}
void array(const std::vector<int64_t>& values) {
    std::cout<<'[';for(size_t i=0;i<values.size();++i){if(i)std::cout<<',';std::cout<<values[i];}std::cout<<']';
}
}

int main(){try{
#if !defined(NDEBUG) || VELOSIM_BENCH_INSTRUMENTED
    throw std::runtime_error("Use an uninstrumented Release build for timing");
#endif
    if(!vsnumeric::avx128Available())throw std::runtime_error("AVX128 is unavailable; no SIMD timing can be claimed");
    constexpr unsigned seed=20261029,warmups=2,repeats=7;
    std::mt19937 random(seed);
    size_t comparisons=0,currentComparisons=0;
    std::cout<<"{\"schema\":\"velosim.svint.simd.benchmark.v1\",\"seed\":"<<seed
             <<",\"warmups\":"<<warmups<<",\"repeats\":"<<repeats<<",\"avx128\":true,\"cases\":[";
    bool first=true;
    for(unsigned width:{127,128,256,1024,4096,16384})for(unsigned workload=0;workload<3;++workload) {
        std::vector<Pair> inputs;
        std::vector<std::pair<std::string,std::string>> texts;
        for(unsigned row=0;row<64;++row) {
            std::string a(width,'0'),b(width,'0');
            for(unsigned bit=0;bit<width;++bit) {
                a[bit]="01xz"[random()%(workload?4:2)];
                b[bit]="01xz"[random()%(workload==2?4:2)];
            }
            // Include unknown-cancelling AND/OR cases, not only random masks.
            if(row==0)b.assign(width,'0');if(row==1)b.assign(width,'1');
            texts.emplace_back(a,b);inputs.push_back({parse(a),parse(b)});
        }
        for(auto operation:{BitOperation::and_,BitOperation::or_,BitOperation::xor_}) {
            for(size_t row=0;row<inputs.size();++row) {
                auto expected=texts[row].first;
                for(size_t bit=0;bit<width;++bit)expected[bit]=truth(texts[row].first[bit],texts[row].second[bit],operation);
                const auto oracle=parse(expected),reference=upstream(inputs[row],operation);
                if(!exactlyEqual(reference,oracle))throw std::runtime_error("Upstream truth-table mismatch");
                for(auto backend:{vsnumeric::Backend::scalar,vsnumeric::Backend::avx128}) {
                    const auto value=vsnumeric::bitwise(inputs[row].a,inputs[row].b,operation,backend);
                    if(!exactlyEqual(value,oracle)||value.getBitWidth()!=width||value.isSigned())throw std::runtime_error("Adapter truth-table mismatch");
                    ++comparisons;
                }
                if(width<=256) {
                    const auto current=vsrtl::bit_binary(vsrtl::parse(texts[row].first),vsrtl::parse(texts[row].second),int(width),
                        operation==BitOperation::and_?0:operation==BitOperation::or_?1:2);
                    if(vsrtl::text(current)!=expected)throw std::runtime_error("Current runtime truth-table mismatch");
                    ++currentComparisons;
                }
            }
            const auto calibration=measure(inputs,4096,[&](const Pair& p){return upstream(p,operation);});
            const auto scaled=uint64_t(3000000)*4096/uint64_t(std::max<int64_t>(calibration.ns,1));
            const unsigned iterations=unsigned(std::clamp<uint64_t>(scaled,10000,1000000));
            std::array<std::vector<int64_t>,3> samples;
            uint64_t checksum=0;
            for(unsigned trial=0;trial<warmups+repeats;++trial) {
                std::array<Sample,3> results;
                // Rotate backend order to reduce consistent cache/frequency bias.
                for(unsigned slot=0;slot<3;++slot) {
                    const unsigned backend=(slot+trial)%3;
                    if(backend==0)results[0]=measure(inputs,iterations,[&](const Pair& p){return upstream(p,operation);});
                    else if(backend==1)results[1]=measure(inputs,iterations,[&](const Pair& p){return vsnumeric::bitwise(p.a,p.b,operation,vsnumeric::Backend::scalar);});
                    else results[2]=measure(inputs,iterations,[&](const Pair& p){return vsnumeric::bitwise(p.a,p.b,operation,vsnumeric::Backend::avx128);});
                }
                if(results[0].checksum!=results[1].checksum||results[0].checksum!=results[2].checksum)throw std::runtime_error("Timed checksum mismatch");
                checksum=results[0].checksum;
                if(trial>=warmups)for(size_t backend=0;backend<3;++backend)samples[backend].push_back(results[backend].ns);
            }
            if(!first)std::cout<<',';first=false;
            std::cout<<"{\"width\":"<<width<<",\"workload\":\""<<(workload==0?"known":workload==1?"mixed_left":"mixed_both")
                     <<"\",\"operation\":\""<<(operation==BitOperation::and_?"and":operation==BitOperation::or_?"or":"xor")
                     <<"\",\"iterations\":"<<iterations<<",\"checksum\":\""<<checksum<<"\",\"upstream_ns\":";
            array(samples[0]);std::cout<<",\"scalar_adapter_ns\":";array(samples[1]);std::cout<<",\"avx128_adapter_ns\":";array(samples[2]);std::cout<<'}';
        }
    }
    std::cout<<"],\"correctness_comparisons\":"<<comparisons<<",\"current_runtime_comparisons\":"<<currentComparisons<<",\"status\":\"pass\"}\n";
    return 0;
}catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}}
