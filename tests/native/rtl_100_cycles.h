#pragma once
template<class F> void rtlCycle(int id,const char* label,F f){try{f();std::cout<<"RTL_CYCLE "<<id<<" PASS "<<label<<'\n';}catch(...){std::cerr<<"RTL_CYCLE "<<id<<" FAIL "<<label<<'\n';throw;}}
inline void indexedReadCycles(){
 const auto a=parse("10xz0011");
 rtlCycle(1,"ascending in range",[&]{check(text(select_indexed(a,word(0,0,0,64),4,false))=="0011");});
 rtlCycle(2,"descending in range",[&]{check(text(select_indexed(a,word(7,0,0,64),4,true))=="10xz");});
 rtlCycle(3,"ascending partial high",[&]{check(text(select_indexed(a,word(6,0,0,64),4,false))=="xx10");});
 rtlCycle(4,"descending partial low",[&]{check(text(select_indexed(a,word(0,0,0,64),4,true))=="1xxx");});
 rtlCycle(5,"descending partial high",[&]{check(text(select_indexed(a,word(10,0,0,64),4,true))=="xxx1");});
 rtlCycle(6,"unknown base",[&]{for(auto b:{parse("x"),parse("z"),parse("1x")})check(text(select_indexed(a,b,4,false))=="xxxx");});
 rtlCycle(7,"unsupported huge base cannot wrap",[&]{for(auto base:{uint64_t(2147483648ULL),UINT64_MAX}){bool rejected=false;try{select_indexed(a,word(base,0,0,64),4,false);}catch(const std::out_of_range&){rejected=true;}check(rejected);}});
 rtlCycle(8,"64bit full word",[]{auto w=parse(std::string(64,'z'));check(text(select_indexed(w,word(63,0,0,64),64,true))==std::string(64,'z'));});
 rtlCycle(9,"all widths independent string oracle",[]{for(int width=1;width<=64;++width){std::string input;for(int i=0;i<width;++i)input+="01xz"[(i+width)%4];for(int n:{1,2,8,32,64})for(int base:{0,1,width-1,width,width+7})for(bool down:{false,true}){std::string expected;for(int bit=n-1;bit>=0;--bit){int index=down?base-(n-1-bit):base+bit;expected+=index>=0&&index<width?input[width-1-index]:'x';}check(text(select_indexed(parse(input),word(base,0,0,64),n,down))==expected);}}});
 rtlCycle(10,"invalid result widths",[&]{for(int n:{0,65}){bool rejected=false;try{select_indexed(a,word(0,0,0,1),n,false);}catch(const std::invalid_argument&){rejected=true;}check(rejected);}});
}
#pragma once
#include <stdexcept>
template<class F> void writeCycle(int id,const char* label,F f){f();std::cout<<"RTL_CYCLE "<<id<<" PASS "<<label<<'\n';}
inline void writeRuntimeCycles(){
 writeCycle(31,"replace X with known one",[]{check(text(deposit_indexed(parse("xxxx"),word(1,0,0,64),1,false,parse("1")))=="xx1x");});
 writeCycle(32,"replace X with known zero",[]{check(text(deposit_indexed(parse("xxxx"),word(0,0,0,64),1,false,parse("0")))=="xxx0");});
 writeCycle(33,"replace known with Z",[]{check(text(deposit_indexed(parse("1111"),word(2,0,0,64),1,false,parse("z")))=="1z11");});
 writeCycle(34,"unknown base ignores write",[]{for(auto base:{parse("x"),parse("z"),parse("1x")})check(text(deposit_indexed(parse("10xz"),base,4,false,parse("0000")))=="10xz");});
 writeCycle(35,"out of range cannot wrap",[]{check(text(deposit_indexed(parse(std::string(64,'z')),word(64,0,0,64),4,false,parse("1111")))==std::string(64,'z'));for(auto base:{uint64_t(2147483648ULL),UINT64_MAX}){bool rejected=false;try{deposit_indexed(parse("0000"),word(base,0,0,64),4,false,parse("1111"));}catch(const std::out_of_range&){rejected=true;}check(rejected);}});
 writeCycle(36,"bit63 mask replacement",[]{auto q=deposit_indexed(parse(std::string(64,'x')),word(63,0,0,64),1,false,parse("0"));check(text(q)=="0"+std::string(63,'x'));});
 writeCycle(37,"partial high write",[]{check(text(deposit_indexed(parse("00000000"),word(6,0,0,64),4,false,parse("1010")))=="10000000");});
 writeCycle(38,"partial low descending write",[]{check(text(deposit_indexed(parse("00000000"),word(0,0,0,64),4,true,parse("1010")))=="00000001");});
 writeCycle(39,"all widths independent string oracle",[]{for(int w=1;w<=64;++w){std::string input;for(int i=0;i<w;++i)input+="01xz"[i%4];for(int n:{1,4,8,64}){std::string rhs;for(int i=0;i<n;++i)rhs+="zx10"[i%4];for(int base:{0,1,w-1,w,w+2})for(bool down:{false,true}){auto expected=input;for(int bit=0;bit<n;++bit){int index=down?base-(n-1-bit):base+bit;if(index>=0&&index<w)expected[w-1-index]=rhs[n-1-bit];}check(text(deposit_indexed(parse(input),word(base,0,0,64),n,down,parse(rhs)))==expected);}}}});
 writeCycle(40,"write width guard and RHS extension",[]{for(int n:{0,65}){bool rejected=false;try{deposit_indexed(parse("0000"),word(0,0,0,1),n,false,parse("1"));}catch(const std::invalid_argument&){rejected=true;}check(rejected);}check(text(deposit_indexed(parse("1111"),word(0,0,0,1),4,false,parse("z")))=="000z");});
}

