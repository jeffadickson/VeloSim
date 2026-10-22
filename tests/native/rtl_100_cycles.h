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
 rtlCycle(7,"overflow cannot wrap",[&]{check(text(select_indexed(a,word(UINT64_MAX,0,0,64),4,false))=="xxxx");});
 rtlCycle(8,"64bit full word",[]{auto w=parse(std::string(64,'z'));check(text(select_indexed(w,word(63,0,0,64),64,true))==std::string(64,'z'));});
 rtlCycle(9,"all widths independent string oracle",[]{for(int width=1;width<=64;++width){std::string input;for(int i=0;i<width;++i)input+="01xz"[(i+width)%4];for(int n:{1,2,8,32,64})for(int base:{0,1,width-1,width,width+7})for(bool down:{false,true}){std::string expected;for(int bit=n-1;bit>=0;--bit){int index=down?base-(n-1-bit):base+bit;expected+=index>=0&&index<width?input[width-1-index]:'x';}check(text(select_indexed(parse(input),word(base,0,0,64),n,down))==expected);}}});
 rtlCycle(10,"invalid result widths",[&]{for(int n:{0,65}){bool rejected=false;try{select_indexed(a,word(0,0,0,1),n,false);}catch(const std::invalid_argument&){rejected=true;}check(rejected);}});
}
