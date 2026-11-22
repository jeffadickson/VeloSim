#include "../../native/rtl/runtime.h"
#include "../../native/rtl/vcd_trace.h"
#include "../../native/rtl/checkpoint.h"
#include "../../native/rtl/svdpi.h"
#include "../../native/rtl/thread_pool.h"
#include <cstdio>
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
 // Verify 16-byte IEEE 1800 aval/bval vsrtl::w4 matches 256-bit vsrtl::W across widths 1..64 and all 4-state cases.
 {
  static_assert(sizeof(w4::W4)==16);
  auto same4=[](w4::W4 a,int w,const W& b){return exact(w4::from(a,w),b)&&w==b.w;};
  const W samples[]={parse("0"),parse("1"),parse("x"),parse("z"),parse("1011"),parse("1x0z"),parse("1z0x"),parse("1z1x"),parse("1111111111111101"),parse("0000000000000111"),parse(std::string(64,'1')),parse("1"+std::string(62,'0')+"1"),parse("10xz"+std::string(60,'1'))};
  for(const W& a:samples){
   uint64_t am=mask64_unchecked(a.w);
   auto a4=w4::to(a,am);
   check(w4::truth(a4)==truth(a)&&w4::yes(a4)==yes(a));
   check(same4(w4::lnot(a4),1,logical_not(a)));
   check(same4(w4::two_state(a4,am),a.w,two_state(a)));
   for(int rw:{1,4,16,32,64}){
    uint64_t rm=mask64_unchecked(rw);
    check(same4(w4::resize(a4,rm),rw,resize(a,rw)));
    check(same4(w4::sresize(a4,a.w,am,rm),rw,signed_resize(a,rw)));
    check(same4(w4::bnot(a4,rm),rw,bit_not(a,rw)));
   }
   for(int op=0;op<6;++op)check(same4(w4::red(a4,am,op),1,reduce(a,op)));
   for(const W& b:samples){
    uint64_t bm=mask64_unchecked(b.w);
    auto b4=w4::to(b,bm);
    check(same4(w4::land(a4,b4),1,logical_and(a,b)));
    check(same4(w4::lor(a4,b4),1,logical_or(a,b)));
    check(same4(w4::eq(a4,b4),1,equal(a,b)));
    check(w4::exact(a4,b4)==exact(a,b));
    int mw=std::max(a.w,b.w);
    uint64_t mm=mask64_unchecked(mw);
    check(w4::casez_match(a4,b4,mm)==casez_match(a,b));
    check(w4::casex_match(a4,b4,mm)==casex_match(a,b));
    check(same4(w4::wildeq(a4,b4,mm),1,wildeq(a,b)));
    check(same4(w4::wildneq(a4,b4,mm),1,wildneq(a,b)));
    if(a.w==b.w){
     for(int op=0;op<4;++op){
      check(same4(w4::cmp(a4,b4,op),1,compare(a,b,op)));
      check(same4(w4::scmp(a4,b4,a.w,op),1,signed_compare(a,b,op)));
     }
    }
    auto sa4=w4::sresize(a4,a.w,am,mm),sb4=w4::sresize(b4,b.w,bm,mm);
    for(int op=0;op<3;++op){
     check(same4(w4::bbin(a4,b4,mm,op),mw,bit_binary(a,b,mw,op)));
     check(same4(w4::product(a4,b4,mm,op),mw,product(a,b,mw,op)));
     check(same4(w4::sproduct(sa4,sb4,mw,mm,op),mw,signed_product(a,b,mw,op)));
    }
    check(same4(w4::arith(a4,b4,mm,false),mw,arithmetic(a,b,mw,false)));
    check(same4(w4::arith(a4,b4,mm,true),mw,arithmetic(a,b,mw,true)));
    check(same4(w4::cond(w4::lit(1ULL,1ULL),a4,b4,mm),mw,conditional(parse("x"),a,b,mw)));
    if(a.w+b.w<=64)check(same4(w4::concat(a4,b4,b.w),a.w+b.w,concat(a,b)));
    if(a.w==1&&b.w==1){
     check(w4::edge(a4,b4,true)==edge_event(a,b,true));
     check(w4::edge(a4,b4,false)==edge_event(a,b,false));
    }
    for(int nk=0;nk<=4;++nk){
     auto ra=w4::resize(a4,mm),rb=w4::resize(b4,mm);
     check(same4(w4::resolve_net2(nk,ra,rb,mm),mw,resolve_net(nk,{resize(a,mw),resize(b,mw)},mw)));
    }
   }
  }
  w4::W4 m0[4]{w4::unknown(0xffULL),w4::unknown(0xffULL),w4::unknown(0xffULL),w4::unknown(0xffULL)};
  w4::W4* ptrs[1]{m0};
  w4::MemLog4<2> log4;
  log4.push(0,4,w4::lit(0ULL),w4::lit(0xabULL),0xffULL);
  log4.push(0,4,w4::lit(1ULL),w4::lit(0xcdULL),0xffULL);
  log4.push(0,4,w4::lit(2ULL),w4::lit(0xefULL),0xffULL);
  log4.push(0,4,w4::lit(1ULL),w4::lit(0x42ULL),0xffULL);
  log4.push(0,4,w4::lit(1ULL,1ULL),w4::lit(0xffULL),0xffULL);
  log4.commit_flat(ptrs);
  check(m0[0].a==0xabULL&&m0[0].b==0ULL&&m0[1].a==0x42ULL&&m0[1].b==0ULL&&m0[2].a==0xefULL&&m0[2].b==0ULL&&m0[3].a==0xffULL&&m0[3].b==0xffULL);
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
 // Verify VCD tracing, persistent checkpoints, $readmemh/$readmemb, $display, and check_assert.
 {
  struct SigDesc { const char* name; int width, direction; bool is_signed, two_state; };
  std::vector<SigDesc> sigs{
   {"clk",1,1,false,false},
   {"sub.data",8,2,false,false}
  };
  std::string vcd_path="/tmp/velosim_native_test.vcd";
  {
   VcdWriter vcd;
   vcd.open(vcd_path,"top",sigs,"1ns");
   std::vector<W> st{word(0,0,0,1),parse("00010101")};
   vcd.dump(0,st);
   vcd.dump(5,st); // unchanged -> should not emit #5
   st[0]=word(1,0,0,1); st[1]=parse("11xz0010");
   vcd.dump(10,st);
   vcd.close();
  }
  std::ifstream vin(vcd_path);
  std::string vcd_text((std::istreambuf_iterator<char>(vin)),std::istreambuf_iterator<char>());
  check(vcd_text.find("$scope module top $end")!=std::string::npos);
  check(vcd_text.find("$scope module sub $end")!=std::string::npos);
  check(vcd_text.find("$var wire 8 \" data [7:0] $end")!=std::string::npos);
  check(vcd_text.find("$dumpvars")!=std::string::npos);
  check(vcd_text.find("#5\n")==std::string::npos);
  check(vcd_text.find("#10\n1!\nb11xz0010 \"\n")!=std::string::npos);
  std::remove(vcd_path.c_str());

  // Persistent binary checkpoint save/restore + corruption & schema-mismatch rejection.
  std::string ckpt_path="/tmp/velosim_native_test.vscp";
  std::vector<W> s_orig{word(1,0,0,1),parse("10xz0101")};
  std::vector<std::vector<W>> m_orig{std::vector<W>{word(0xaa,0,0,8),word(0x55,0,0,8)}};
  std::vector<uint8_t> extra_orig{1,2,3,4,5};
  checkpoint::save(ckpt_path,"top",sigs,1234ULL,s_orig,m_orig,extra_orig);

  uint64_t r_now=0;
  std::vector<W> r_s{word(0,0,0,1),word(0,0,0,8)};
  std::vector<std::vector<W>> r_m{std::vector<W>(2,unknown(8))};
  std::vector<uint8_t> r_extra;
  checkpoint::load(ckpt_path,"top",sigs,r_now,r_s,r_m,r_extra);
  check(r_now==1234ULL&&exact(r_s[0],s_orig[0])&&exact(r_s[1],s_orig[1])&&exact(r_m[0][0],m_orig[0][0])&&exact(r_m[0][1],m_orig[0][1])&&r_extra==extra_orig);
  bool schema_rejected=false;
  try{checkpoint::load(ckpt_path,"wrong_top",sigs,r_now,r_s,r_m,r_extra);}catch(const std::runtime_error&){schema_rejected=true;}
  check(schema_rejected);
  // Flip one payload byte -> FNV-1a mismatch must fail closed.
  {
   std::fstream f(ckpt_path,std::ios::binary|std::ios::in|std::ios::out);
   f.seekp(-1,std::ios::end); char b=0; f.read(&b,1); f.seekp(-1,std::ios::end); b^=0x55; f.write(&b,1);
  }
  bool cs_rejected=false;
  try{checkpoint::load(ckpt_path,"top",sigs,r_now,r_s,r_m,r_extra);}catch(const std::runtime_error&){cs_rejected=true;}
  check(cs_rejected);
  std::remove(ckpt_path.c_str());

  // $readmemh & $readmemb parser with @addr, comments, '_' separators, and 4-state x/z.
  std::string hex_path="/tmp/velosim_native_test.hex";
  {
   std::ofstream out(hex_path);
   out<<"// header comment\n12_34 /* inline */ ab_cd\n@4\n00_ff xz_10\n";
  }
  std::vector<W> mem16(8,unknown(16));
  size_t loaded=load_mem_file(mem16,16,hex_path,true);
  check(loaded==4&&mem16[0].v==0x1234&&mem16[1].v==0xabcd&&mem16[4].v==0x00ff&&mem16[5].x==0xf000&&mem16[5].z==0x0f00&&mem16[5].v==0x0010);
  uint64_t flat16[8]{};
  check(load_mem_file_flat(flat16,8,16,hex_path,true)==4&&flat16[0]==0x1234&&flat16[1]==0xabcd&&flat16[4]==0x00ff&&flat16[5]==0x0010);
  std::remove(hex_path.c_str());

  // $display formatting and immediate assertions.
  check(format_display("v=%0x d=%d b=%0b %%",std::vector<W>{word(0x2a,0,0,16),word(42,0,0,8),word(5,0,0,8)},true)=="v=2a d=42 b=101 %\n");
  check_assert(word(1,0,0,1),"ok");
  w64::check_assert(w64::lit(1,1),"ok");
  w2::check_assert(1ULL,"ok");
  bool asserted=false;
  try{w64::check_assert(w64::lit(0,1,0,1),"boom");}catch(const std::runtime_error& e){asserted=std::string(e.what()).find("boom")!=std::string::npos;}
  check(asserted);

  // Wave 2: IEEE 1800 ==? / !=?, casez / casex, and svdpi.h bit helpers.
  check(text(wildeq(parse("1010"),parse("10zz")))=="1");
  check(text(wildeq(parse("10x1"),parse("10xx")))=="1");
  check(text(wildeq(parse("1110"),parse("10zz")))=="0");
  check(text(wildeq(parse("1x10"),parse("10zz")))=="x");
  check(text(wildeq(parse("1x10"),parse("00zz")))=="0");
  check(text(wildneq(parse("1010"),parse("10zz")))=="0");
  check(text(wildneq(parse("1110"),parse("10zz")))=="1");
  check(text(wildneq(parse("1x10"),parse("10zz")))=="x");
  check(casez_match(parse("1010"),parse("10zz")));
  check(casez_match(parse("10z0"),parse("1010")));
  check(!casez_match(parse("10x0"),parse("1010")));
  check(casez_match(parse("10x0"),parse("10x0")));
  check(casex_match(parse("10x0"),parse("1010")));
  check(casex_match(parse("1010"),parse("10xz")));
  check(!casex_match(parse("1110"),parse("10xz")));

  // w64 fast-path wildcard equivalence with W oracle.
  check(w64::exact(w64::wildeq(w64::to(parse("1010")),w64::to(parse("10zz"))),w64::lit(1,1)));
  check(w64::exact(w64::wildeq(w64::to(parse("1x10")),w64::to(parse("10zz"))),w64::unknown(1)));
  check(w64::exact(w64::wildneq(w64::to(parse("1110")),w64::to(parse("10zz"))),w64::lit(1,1)));
  check(w64::casez_match(w64::to(parse("1010")),w64::to(parse("10zz"))));
  check(!w64::casez_match(w64::to(parse("10x0")),w64::to(parse("1010"))));
  check(w64::casex_match(w64::to(parse("10x0")),w64::to(parse("1010"))));

  // svdpi.h bit and logic vector helpers.
  svBitVecVal bv[2]{0,0};
  svPutBitselBit(bv,37,sv_1);
  check(svGetBitselBit(bv,37)==sv_1&&svGetBitselBit(bv,36)==sv_0);
  svPutBitselBit(bv,37,sv_0);
  check(svGetBitselBit(bv,37)==sv_0);
  svLogicVecVal lv[2]{{0,0},{0,0}};
  svPutBitselLogic(lv,5,sv_x);
  svPutBitselLogic(lv,33,sv_z);
  svPutBitselLogic(lv,34,sv_1);
  check(svGetBitselLogic(lv,5)==sv_x&&svGetBitselLogic(lv,33)==sv_z&&svGetBitselLogic(lv,34)==sv_1&&svGetBitselLogic(lv,0)==sv_0);

  // Wave 3: bit/count/state system queries across W, W64, and W2.
  W mix=parse("10x1z010");
  check(countones(mix).v==3&&onehot(mix).v==0&&onehot0(mix).v==0&&isunknown(mix).v==1);
  W oh=parse("00x10z00");
  check(countones(oh).v==1&&onehot(oh).v==1&&onehot0(oh).v==1&&isunknown(oh).v==1);
  W zero=parse("00000000");
  check(countones(zero).v==0&&onehot(zero).v==0&&onehot0(zero).v==1&&isunknown(zero).v==0);
  check(clog2(word(0,0,0,16)).v==0&&clog2(word(1,0,0,16)).v==0&&clog2(word(2,0,0,16)).v==1&&clog2(word(5,0,0,16)).v==3&&clog2(word(256,0,0,16)).v==8);
  check(clog2(parse("00x1")).v==0);

  check(w64::countones(w64::to(mix)).v==3&&w64::onehot(w64::to(oh)).v==1&&w64::onehot0(w64::to(zero)).v==1&&w64::isunknown(w64::to(mix)).v==1&&w64::isunknown(w64::to(zero)).v==0);
  check(w64::clog2(w64::lit(0,16)).v==0&&w64::clog2(w64::lit(1,16)).v==0&&w64::clog2(w64::lit(5,16)).v==3&&w64::clog2(w64::lit(256,16)).v==8);
  check(w2::countones(0b10100101ULL,8)==4&&w2::onehot(0x08ULL,8)==1&&w2::onehot(0x09ULL,8)==0&&w2::onehot0(0ULL,8)==1&&w2::isunknown(0xFFULL,8)==0);
  check(w2::clog2(0ULL,16)==0&&w2::clog2(1ULL,16)==0&&w2::clog2(2ULL,16)==1&&w2::clog2(5ULL,16)==3&&w2::clog2(256ULL,16)==8);

  // Wave 4: IEEE 1800 Tables 6-1/6-2/6-3 4-state net resolution across W, W64, and W2.
  const char* vals[4]={"0","1","x","z"};
  const char wireTbl[4][4]={{'0','x','x','0'},{'x','1','x','1'},{'x','x','x','x'},{'0','1','x','z'}};
  const char wandTbl[4][4]={{'0','0','0','0'},{'0','1','x','1'},{'0','x','x','x'},{'0','1','x','z'}};
  const char worTbl [4][4]={{'0','1','x','0'},{'1','1','1','1'},{'x','1','x','x'},{'0','1','x','z'}};
  for(int i=0;i<4;++i){
   for(int j=0;j<4;++j){
    W a=parse(vals[i]), b=parse(vals[j]);
    check(text(resolve_net(0,{a,b},1))==std::string(1,wireTbl[i][j]));
    check(text(resolve_net(1,{a,b},1))==std::string(1,wandTbl[i][j]));
    check(text(resolve_net(2,{a,b},1))==std::string(1,worTbl[i][j]));
    char exp0=wireTbl[i][j]=='z'?'0':wireTbl[i][j];
    char exp1=wireTbl[i][j]=='z'?'1':wireTbl[i][j];
    check(text(resolve_net(3,{a,b},1))==std::string(1,exp0));
    check(text(resolve_net(4,{a,b},1))==std::string(1,exp1));
    // Parity with w64
    check(exact(w64::from(w64::resolve_net(0,{w64::to(a),w64::to(b)},1)),resolve_net(0,{a,b},1)));
    check(exact(w64::from(w64::resolve_net(1,{w64::to(a),w64::to(b)},1)),resolve_net(1,{a,b},1)));
    check(exact(w64::from(w64::resolve_net(2,{w64::to(a),w64::to(b)},1)),resolve_net(2,{a,b},1)));
    check(exact(w64::from(w64::resolve_net(3,{w64::to(a),w64::to(b)},1)),resolve_net(3,{a,b},1)));
    check(exact(w64::from(w64::resolve_net(4,{w64::to(a),w64::to(b)},1)),resolve_net(4,{a,b},1)));
   }
  }
  // Multi-bit 3-driver resolution: Z yields, 0 vs 1 -> X, pull-up/down on undriven Z bits.
  W d1=parse("01zz"), d2=parse("z1xz"), d3=parse("1zzz");
  check(text(resolve_net(0,{d1,d2,d3},4))=="x1xz");
  check(text(resolve_net(3,{d1,d2,d3},4))=="x1x0");
  check(text(resolve_net(4,{d1,d2,d3},4))=="x1x1");
  check(w2::resolve_net(1,{0xF0ULL,0x3CULL},8)==0x30ULL);
  check(w2::resolve_net(2,{0xF0ULL,0x0FULL},8)==0xFFULL);

  // Wave 4: deterministic ThreadPool::parallel_for + exception propagation.
  {
   ThreadPool pool(4);
   check(pool.thread_count()==4);
   std::vector<uint64_t> out(64,0);
   pool.parallel_for(out.size(),[&](size_t idx){out[idx]=uint64_t(idx)*uint64_t(idx)+7ULL;});
   for(size_t idx=0;idx<out.size();++idx)check(out[idx]==uint64_t(idx)*uint64_t(idx)+7ULL);
   bool threw=false;
   try{ pool.parallel_for(8,[](size_t idx){if(idx==5)throw std::runtime_error("worker boom");}); }
   catch(const std::runtime_error&){ threw=true; }
   check(threw);
  }
 }
}


