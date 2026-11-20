#include "../../native/rtl/runtime.h"
#include <cstdlib>
#include <iostream>
using namespace vsrtl;
void check(bool b){if(!b){std::cerr<<"RTL runtime assertion failed\n";std::abort();}}
#include "rtl_100_cycles.h"
#include "concat_target_cycles.h"
#include "signed_wide_tests.h"
#include "input_event_cycles.h"
#include "evolution_tests.h"
int main(){
 // Runtime allocation validation and state preservation supplement HDL traces.
 std::vector<W> dynamic;
 dynamic_allocate(dynamic,word(2,0,0,32),8,false,false,false);check(dynamic.size()==2);dynamic[0]=parse("10100101");
 dynamic_allocate(dynamic,word(3,0,0,32),8,false,true,false);check(dynamic.size()==3&&text(dynamic[0])=="10100101"&&text(dynamic[2])=="xxxxxxxx");
 for(auto bad:{word(257,0,0,32),parse("x"),parse("z"),parse("11111111")}){
  auto before=dynamic;bool rejected=false;try{dynamic_allocate(dynamic,bad,8,false,false,true);}catch(const std::exception&){rejected=true;}
  check(rejected&&dynamic.size()==before.size()&&text(dynamic[0])==text(before[0]));
 }
 for(int invalidWidth:{0,257}){bool rejected=false;try{dynamic_allocate(dynamic,word(1,0,0,32),invalidWidth,false,false,false);}catch(const std::invalid_argument&){rejected=true;}check(rejected);}
 auto badCount=word(0,0,0,32);badCount.w=0;bool rejectedCount=false;try{dynamic_allocate(dynamic,badCount,8,false,false,false);}catch(const std::invalid_argument&){rejectedCount=true;}check(rejectedCount);
 dynamic_allocate(dynamic,word(1,0,0,32),8,true,false,false);check(text(dynamic[0])=="00000000"&&text(dynamic_read(dynamic,parse("x"),8,true))=="00000000");
 check(text(dynamic_read(dynamic,word(2,0,0,32),8,false))=="xxxxxxxx"&&text(dynamic_read(dynamic,word(0,0,0,32),8,true))=="00000000");
 dynamic_allocate(dynamic,word(0,0,0,32),8,false,true,false);check(dynamic.empty());
 evolutionRuntimeTests();
 inputEventCycles();
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
   int left=right+(size-1);std::string expected;
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
 // Verify compile-time lit(), parse_span(), append_text(), MemLog, and SSE2/AVX2 SIMD kernels.
 check(exact(lit(0x5ULL,4),parse("0101")));
 check(exact(lit(0x8ULL,0x1ULL,0x2ULL,0x0ULL,0x1ULL,0x0ULL,65),parse("1"+std::string(60,'0')+"10xz")));
 check(exact(parse_span("10xz",4),parse("10xz")));
 {
  std::string buf;append_text(buf,parse("10xz01"));check(buf=="10xz01");
  std::vector<std::vector<W>> mems{std::vector<W>(8,unknown(8))};
  MemLog<4> log;
  for(int i=0;i<6;++i)log.push(0,mems[0].size(),lit(uint64_t(i),8),lit(uint64_t(i+10),8),8);
  log.push(0,mems[0].size(),lit(1ULL,8),lit(99ULL,8),8); // Later write to same slot wins.
  log.push(0,mems[0].size(),parse("x"),lit(77ULL,8),8); // Unknown index ignored.
  log.push(0,mems[0].size(),lit(8ULL,8),lit(77ULL,8),8); // OOB index ignored.
  log.commit(mems);
  check(mems[0][0].v==10&&mems[0][1].v==99&&mems[0][5].v==15&&mems[0][6].x==0xff);
 }
 {
  constexpr size_t N=11;
  uint64_t av[N],ax[N],bv[N],bx[N],rv[N],rx[N];
  const uint64_t m=mask64_unchecked(17);
  for(size_t i=0;i<N;++i){
   av[i]=(0x12345ULL*(i+1))&m;ax[i]=((i%3==0)?0x55ULL:0ULL)&m;
   bv[i]=(0x0fedcULL*(i+3))&m;bx[i]=((i%4==0)?0xaaULL:0ULL)&m;
   av[i]&=~ax[i];bv[i]&=~bx[i];
  }
  for(int op=0;op<3;++op){
   simd::bit_binary_u64_batch(av,ax,bv,bx,rv,rx,N,op,m);
   for(size_t i=0;i<N;++i){
    auto exp=bit_binary(word(av[i],ax[i],0,17),word(bv[i],bx[i],0,17),17,op);
    check(rv[i]==uint64_t(exp.v)&&rx[i]==uint64_t(exp.x));
   }
  }
  for(bool sub:{false,true}){
   simd::add_sub_u64_batch(av,ax,bv,bx,rv,rx,N,sub,m);
   for(size_t i=0;i<N;++i){
    auto exp=arithmetic(word(av[i],ax[i],0,17),word(bv[i],bx[i],0,17),17,sub);
    check(rv[i]==uint64_t(exp.v)&&rx[i]==uint64_t(exp.x));
   }
  }
  // 128-bit SSE2 bitwise & conditional merge vs independent 4-state expectations.
  auto w128a=parse(std::string(32,'1')+std::string(32,'0')+std::string(32,'x')+std::string(32,'z'));
  auto w128b=parse(std::string(64,'1')+std::string(32,'0')+std::string(32,'x'));
  check(text(bit_not(w128a,128))==std::string(32,'0')+std::string(32,'1')+std::string(64,'x'));
  check(text(bit_binary(w128a,w128b,128,0))==std::string(32,'1')+std::string(32,'0')+std::string(32,'0')+std::string(32,'x'));
  check(text(bit_binary(w128a,w128b,128,1))==std::string(64,'1')+std::string(64,'x'));
  check(text(bit_binary(w128a,w128b,128,2))==std::string(32,'0')+std::string(32,'1')+std::string(64,'x'));
  auto cond128=conditional(parse("x"),w128a,w128a,128);
  check(exact(cond128,w128a));
 }
 // Verify narrow 32-byte vsrtl::w64 matches 256-bit vsrtl::W across widths 1..64 and 4-state cases.
 {
  static_assert(sizeof(w64::W64)<=32);
  auto same=[](w64::W64 a,const W& b){return exact(w64::from(a),b)&&a.w==b.w;};
  const W samples[]={parse("0"),parse("1"),parse("x"),parse("z"),parse("1011"),parse("1x0z"),parse("1111111111111101"),parse("0000000000000111"),parse(std::string(64,'1')),parse("1"+std::string(62,'0')+"1"),parse("10xz"+std::string(60,'1'))};
  for(const W& a:samples){
   auto a64=w64::to(a);
   check(w64::truth(a64)==truth(a)&&w64::yes(a64)==yes(a));
   check(same(w64::lnot(a64),logical_not(a)));
   check(same(w64::two_state(a64),two_state(a)));
   for(int rw:{1,4,16,32,64}){
    check(same(w64::resize(a64,rw),resize(a,rw)));
    check(same(w64::sresize(a64,rw),signed_resize(a,rw)));
    check(same(w64::bnot(a64,rw),bit_not(a,rw)));
   }
   for(int op=0;op<6;++op)check(same(w64::red(a64,op),reduce(a,op)));
   for(const W& b:samples){
    auto b64=w64::to(b);
    check(same(w64::land(a64,b64),logical_and(a,b)));
    check(same(w64::lor(a64,b64),logical_or(a,b)));
    check(same(w64::eq(a64,b64),equal(a,b)));
    check(w64::exact(a64,b64)==exact(a,b));
    if(a.w==b.w){
     for(int op=0;op<4;++op){
      check(same(w64::cmp(a64,b64,op),compare(a,b,op)));
      check(same(w64::scmp(a64,b64,op),signed_compare(a,b,op)));
     }
    }
    int mw=std::max(a.w,b.w);
    for(int op=0;op<3;++op){
     check(same(w64::bbin(a64,b64,mw,op),bit_binary(a,b,mw,op)));
     check(same(w64::product(a64,b64,mw,op),product(a,b,mw,op)));
     check(same(w64::sproduct(a64,b64,mw,op),signed_product(a,b,mw,op)));
    }
    check(same(w64::arith(a64,b64,mw,false),arithmetic(a,b,mw,false)));
    check(same(w64::arith(a64,b64,mw,true),arithmetic(a,b,mw,true)));
    check(same(w64::cond(w64::lit(0,1,0,1),a64,b64,mw),conditional(parse("x"),a,b,mw)));
    if(a.w+b.w<=64)check(same(w64::concat(a64,b64),concat(a,b)));
    if(a.w==1&&b.w==1){
     check(w64::edge(a64,b64,true)==edge_event(a,b,true));
     check(w64::edge(a64,b64,false)==edge_event(a,b,false));
    }
   }
  }
  std::vector<std::vector<W>> mems{std::vector<W>(4,unknown(8))};
  w64::MemLog64<2> log64;
  log64.push(0,mems[0].size(),w64::lit(0,2),w64::lit(0xab,8),8);
  log64.push(0,mems[0].size(),w64::lit(1,2),w64::lit(0xcd,8),8);
  log64.push(0,mems[0].size(),w64::lit(2,2),w64::lit(0xef,8),8);
  log64.push(0,mems[0].size(),w64::lit(1,2),w64::lit(0x42,8),8);
  log64.commit(mems);
  check(mems[0][0].v==0xab&&mems[0][1].v==0x42&&mems[0][2].v==0xef&&mems[0][3].x==0xff);
 }
 // Verify opt-in 64-bit two-state vsrtl::w2 matches 256-bit vsrtl::W on all known operands plus edge cases.
 {
  auto same2=[](uint64_t v,int w,const W& b){return exact(w2::from(v,w),b);};
  const W samples[]={
   parse("0"),parse("1"),parse("1011"),parse("0100"),
   parse("1111111111111101"),parse("0000000000000111"),
   parse(std::string(32,'1')),parse("1"+std::string(31,'0')),
   parse(std::string(64,'1')),parse("1"+std::string(63,'0')),
   parse("1"+std::string(62,'0')+"1"),parse(std::string(64,'0'))
  };
  for(const W& a:samples){
   uint64_t a2=w2::to(a);
   check(w2::yes(a2)==yes(a));
   check(same2(w2::lnot(a2),1,logical_not(a)));
   for(int rw:{1,4,16,32,64}){
    check(same2(w2::resize(a2,rw),rw,resize(a,rw)));
    check(same2(w2::sresize(a2,a.w,rw),rw,signed_resize(a,rw)));
    check(same2(w2::bnot(a2,rw),rw,bit_not(a,rw)));
   }
   for(int op=0;op<6;++op)check(same2(w2::red(a2,a.w,op),1,reduce(a,op)));
   for(const W& b:samples){
    uint64_t b2=w2::to(b);
    check(same2(w2::land(a2,b2),1,logical_and(a,b)));
    check(same2(w2::lor(a2,b2),1,logical_or(a,b)));
    check(same2(w2::eq(a2,b2),1,equal(a,b)));
    if(a.w==b.w){
     for(int op=0;op<4;++op){
      check(same2(w2::cmp(a2,b2,op),1,compare(a,b,op)));
      check(same2(w2::scmp(a2,b2,a.w,op),1,signed_compare(a,b,op)));
     }
    }
    int mw=std::max(a.w,b.w);
    uint64_t ae=w2::resize(a2,mw),be=w2::resize(b2,mw);
    uint64_t sae=w2::sresize(a2,a.w,mw),sbe=w2::sresize(b2,b.w,mw);
    for(int op=0;op<3;++op){
     check(same2(w2::bbin(ae,be,mw,op),mw,bit_binary(a,b,mw,op)));
     if(op==0||be!=0)check(same2(w2::product(ae,be,mw,op),mw,product(a,b,mw,op)));
     if(op==0||sbe!=0)check(same2(w2::sproduct(sae,sbe,mw,op),mw,signed_product(resize(signed_resize(a,mw),mw),resize(signed_resize(b,mw),mw),mw,op)));
    }
    check(same2(w2::arith(ae,be,mw,false),mw,arithmetic(a,b,mw,false)));
    check(same2(w2::arith(ae,be,mw,true),mw,arithmetic(a,b,mw,true)));
    check(same2(w2::cond(1ULL,ae,be,mw),mw,conditional(parse("1"),a,b,mw)));
    check(same2(w2::cond(0ULL,ae,be,mw),mw,conditional(parse("0"),a,b,mw)));
    if(a.w+b.w<=64)check(same2(w2::concat(a2,b2,b.w),a.w+b.w,concat(a,b)));
    if(a.w==1&&b.w==1){
     check(w2::edge(a2,b2,true)==edge_event(a,b,true));
     check(w2::edge(a2,b2,false)==edge_event(a,b,false));
    }
   }
  }
  // Div/mod by zero returns 0 in two-state mode; INT64_MIN / -1 does not trap.
  check(w2::product(42ULL,0ULL,32,1)==0ULL&&w2::product(42ULL,0ULL,32,2)==0ULL);
  check(w2::sproduct(42ULL,0ULL,32,1)==0ULL&&w2::sproduct(42ULL,0ULL,32,2)==0ULL);
  check(w2::sproduct(uint64_t(INT64_MIN),~uint64_t(0),64,1)==uint64_t(INT64_MIN));
  check(w2::sproduct(uint64_t(INT64_MIN),~uint64_t(0),64,2)==0ULL);
  // Bit/part/indexed select and deposit.
  check(w2::selbit(0b1010ULL,4,1)==1ULL&&w2::selbit(0b1010ULL,4,5)==0ULL);
  check(w2::selpart(0xabcdULL,16,11,4)==0xbcULL);
  check(w2::selidx(0xabcdULL,16,4,8,false)==0xbcULL);
  check(w2::depidx(0x1234ULL,16,4,8,false,0xefULL)==0x1ef4ULL);
  // Flat memory log with inline + overflow.
  uint64_t m0[4]{};
  uint64_t* ptrs[1]{m0};
  w2::MemLog2<2> log2;
  log2.push(0,4,0,0xab,8);
  log2.push(0,4,1,0xcd,8);
  log2.push(0,4,2,0xef,8);
  log2.push(0,4,1,0x42,8);
  log2.push(0,4,99,0xff,8);
  log2.commit(ptrs);
  check(w2::read_flat(m0,4,0)==0xab&&w2::read_flat(m0,4,1)==0x42&&w2::read_flat(m0,4,2)==0xef&&w2::read_flat(m0,4,3)==0&&w2::read_flat(m0,4,99)==0);
 }
}


