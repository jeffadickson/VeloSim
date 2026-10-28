#include "../../native/numeric/svint_simd.h"
#include <cstdlib>
#include <string>

// libFuzzer owns mutation/minimization. This adapter supplies only bounded
// SVInt inputs and differential assertions; it never changes production state.
extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data,size_t size) {
    if(size<4) return 0;
    const unsigned width=1+(unsigned(data[0])+(unsigned(data[1])<<8))%4096;
    const bool sign=(data[2]&1)!=0;
    const auto operation=static_cast<vsnumeric::BitOperation>(data[3]%3);
    std::string a(width,'0'),b(width,'0');
    for(unsigned i=0;i<width;++i){a[i]="01xz"[data[(4+i)%size]&3];b[i]="01xz"[(data[(5+i)%size]>>2)&3];}
    const auto prefix=std::to_string(width)+(sign?"'sb":"'b");
    const auto left=slang::SVInt::fromString(prefix+a),right=slang::SVInt::fromString(prefix+b);
    const auto reference=operation==vsnumeric::BitOperation::and_?left&right:operation==vsnumeric::BitOperation::or_?left|right:left^right;
    for(auto backend:{vsnumeric::Backend::scalar,vsnumeric::Backend::automatic}) {
        const auto result=vsnumeric::bitwise(left,right,operation,backend);
        if(!exactlyEqual(result,reference)||result.isSigned()!=reference.isSigned()) std::abort();
    }
    return 0;
}
