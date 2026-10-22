#include "../../native/rtl/runtime.h"
#include <cstdlib>
#include <iostream>
using namespace vsrtl;
void check(bool b){if(!b){std::cerr<<"RTL runtime assertion failed\n";std::abort();}}
#include "rtl_100_cycles.h"
#include "concat_target_cycles.h"
#include "signed_wide_tests.h"
int main(){
 signedWideTests();
 indexedReadCycles();
 writeRuntimeCycles();
 concatTargetCycles();
 // Independent string-position oracle: no runtime masks/shifts in expected values.
 for(int width=1;width<=64;++width){
  std::string input;for(int i=0;i<width;++i)input+="01xz"[(i+width)%4];auto data=parse(input);
  for(uint64_t index=0;index<=65;++index){
   auto expected=index<uint64_t(width)?std::string(1,input[width-1-int(index)]):"x";
   check(text(select_bit(data,word(index,0,0,64)))==expected);
  }
  for(auto index:{parse("x"),parse("z"),parse("1x"),parse("z0"),word(UINT64_MAX,0,0,64)})check(text(select_bit(data,index))=="x");
  for(int right:{0,1,width-1,width,65,2147483584})for(int size:{1,2,8,32,64}){
   int left=right+size-1;std::string expected;
   for(int bit=left;bit>=right;--bit)expected+=bit<width?input[width-1-bit]:'x';
   check(text(select_part(data,left,right))==expected);
  }
 }
 for(char value:std::string("01xz")){
  auto data=parse(std::string(64,value));check(text(select_part(data,63,0))==std::string(64,value));
  check(text(select_part(data,64,1))=="x"+std::string(63,value));
  check(text(select_bit(data,word(63,0,0,64)))==std::string(1,value));
 }
 for(auto a:{parse("0"),parse("1"),parse("x"),parse("z")})for(auto b:{parse("0"),parse("1"),parse("x"),parse("z")}){
  auto and_value=logical_and(a,b);check(text(and_value)==(text(a)=="0"||text(b)=="0"?"0":text(a)=="1"&&text(b)=="1"?"1":"x"));
  check(text(logical_not(a))==(text(a)=="0"?"1":text(a)=="1"?"0":"x"));
  auto l=text(a),r=text(b);
  check(text(bit_not(a,1))==(l=="0"?"1":l=="1"?"0":"x"));
  check(text(bit_binary(a,b,1,0))==(l=="0"||r=="0"?"0":l=="1"&&r=="1"?"1":"x"));
  check(text(bit_binary(a,b,1,1))==(l=="1"||r=="1"?"1":l=="0"&&r=="0"?"0":"x"));
  check(text(bit_binary(a,b,1,2))==((l=="x"||l=="z"||r=="x"||r=="z")?"x":l==r?"0":"1"));
  check(exact(a,b)==(text(a)==text(b)));check(text(concat(a,b))==text(a)+text(b));
  check(text(logical_or(a,b))==(l=="1"||r=="1"?"1":l=="0"&&r=="0"?"0":"x"));
  check(text(conditional(parse("x"),a,b,1))==(l==r?l:"x"));
  check(text(conditional(parse("z"),a,b,1))==(l==r?l:"x"));
  check(exact(conditional(parse("1"),a,b,1),a));check(exact(conditional(parse("0"),a,b,1),b));
 }
 // Independent known-value relational/product oracle over every small operand pair.
 for(uint64_t a=0;a<16;++a)for(uint64_t b=0;b<16;++b){
  W x=word(a,0,0,4),y=word(b,0,0,4);
  check(compare(x,y,0).v==uint64_t(a<b));check(compare(x,y,1).v==uint64_t(a<=b));
  check(compare(x,y,2).v==uint64_t(a>b));check(compare(x,y,3).v==uint64_t(a>=b));
  check(product(x,y,4,0).v==((a*b)%16));
  if(b){check(product(x,y,4,1).v==a/b);check(product(x,y,4,2).v==a%b);}
  else{check(text(product(x,y,4,1))=="xxxx");check(text(product(x,y,4,2))=="xxxx");}
 }
 for(int op=0;op<4;++op)check(text(compare(parse("0x"),parse("11"),op))=="x");
 struct ReductionCase{const char* input;const char* and_result;const char* or_result;const char* xor_result;};
 for(auto c:{ReductionCase{"00","0","0","0"},{"01","0","1","1"},{"11","1","1","0"},{"0x","0","x","x"},{"1x","x","1","x"},{"xx","x","x","x"},{"0z","0","x","x"},{"1z","x","1","x"},{"zz","x","x","x"}}){
  auto a=parse(c.input);check(text(reduce(a,0))==c.and_result);check(text(reduce(a,2))==c.or_result);check(text(reduce(a,4))==c.xor_result);
  for(int op:{0,2,4})check(exact(reduce(a,op+1),logical_not(reduce(a,op))));
 }
 check(text(shift(parse("1xz0"),parse("01"),4,false))=="xz00");check(text(shift(parse("1xz0"),parse("01"),4,true))=="01xz");
 check(text(shift(parse("1xz0"),parse("x"),4,true))=="xxxx");
 for(uint64_t n:std::vector<uint64_t>{0,1,63,64,65,UINT64_MAX}){
  auto amount=word(n,0,0,64),a=word(UINT64_MAX,0,0,64);
  check(shift(a,amount,64,true).v==(n>=64?0:UINT64_MAX>>n));
  check(shift(a,amount,64,false).v==(n>=64?0:UINT64_MAX<<n));
 }
 check(text(product(parse("x"),parse("0"),4,0))=="xxxx");
 check(product(word(UINT64_MAX,0,0,64),word(2,0,0,64),64,0).v==UINT64_MAX-1);
 check(product(word(UINT64_MAX,0,0,64),word(1,0,0,64),64,1).v==UINT64_MAX);
 check(text(conditional(parse("x"),parse("1z0x"),parse("1z1x"),4))=="1zxx");
 check(text(conditional(parse("1x"),parse("z"),parse("0"),4))=="000z");
 check(text(resize(parse("xz"),4))=="00xz");check(text(equal(parse("x0"),parse("11")))=="0");check(text(equal(parse("x0"),parse("10")))=="x");
 check(text(bit_not(parse("z1"),4))=="11x0");check(text(bit_binary(parse("z"),parse("1010"),4,0))=="0000");
 check(text(bit_not(parse(std::string(64,'0')),64))==std::string(64,'1'));
 check(text(arithmetic(parse("1111"),parse("1"),4,false))=="0000");check(text(arithmetic(parse("0000"),parse("1"),4,true))=="1111");
 check(text(arithmetic(parse("x000"),parse("1"),2,false))=="xx");
 check(text(arithmetic(parse(std::string(64,'1')),parse("1"),64,false))==std::string(64,'0'));
 std::vector<W> m(4,unknown(8));check(text(read(m,parse("xx"),8))=="xxxxxxxx");check(text(read(m,parse("100"),8))=="xxxxxxxx");
 write(m,parse("x"),parse("1"),8);check(text(m[0])=="xxxxxxxx");write(m,parse("100"),parse("1"),8);check(text(m[0])=="xxxxxxxx");
 write(m,parse("01"),parse("xz"),8);check(text(read(m,parse("01"),8))=="000000xz");
 bool invalid=false;try{parse("bad");}catch(const std::exception&){invalid=true;}check(invalid);
}

