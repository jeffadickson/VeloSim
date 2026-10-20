#include "velosim.h"
#include <cstdint>
#include <iostream>
#define CHECK(x) do { if(!(x)){std::cerr<<"word ABI line "<<__LINE__<<"\n";return 1;} } while(false)
struct Word { uint32_t width; uint64_t value=0,x=0,z=0; };
uint64_t mask(uint32_t width){return width==64?UINT64_MAX:(uint64_t{1}<<width)-1;}
int bit(Word w,uint32_t n){if(n>=w.width)return 0;auto b=uint64_t{1}<<n;return w.z&b?3:w.x&b?2:w.value&b?1:0;}
Word truth(uint8_t op,Word a,Word b,uint32_t width){
  static constexpr int gates[3][4][4]={
    {{0,0,0,0},{0,1,2,2},{0,2,2,2},{0,2,2,2}},
    {{0,1,2,2},{1,1,1,1},{2,1,2,2},{2,1,2,2}},
    {{0,1,2,2},{1,0,2,2},{2,2,2,2},{2,2,2,2}}};
  static constexpr int inverted[]={1,0,2,2};Word result{width};
  for(uint32_t i=0;i<width;i++){int out=op==9?inverted[bit(a,i)]:gates[op-6][bit(a,i)][bit(b,i)];if(out==1)result.value|=uint64_t{1}<<i;if(out==2)result.x|=uint64_t{1}<<i;}
  return result;
}
int run(uint8_t op,Word a,Word b,Word expected,uint32_t workers){
  void* e=vs_ir_new();CHECK(e);
  CHECK(vs_ir_signal(e,1,0,0,0)==0);CHECK(vs_ir_signal(e,a.width,a.value,a.x,a.z)==1);CHECK(vs_ir_signal(e,b.width,b.value,b.x,b.z)==2);
  CHECK(vs_ir_signal(e,1,1,0,0)==3);CHECK(vs_ir_signal(e,expected.width,7,0,0)==4);CHECK(vs_ir_signal(e,expected.width,0,0,0)==5);
  CHECK(vs_ir_process_v2(e,0,4,1,2,3,0,op)==0);CHECK(vs_ir_process(e,0,5,4,3,0,0)==0);
  uint32_t id=0;uint64_t one=1,zero=0;CHECK(vs_ir_drive(e,&id,&one,&zero,&zero,1,workers)==2);
  uint64_t value=0,x=0,z=0;CHECK(vs_ir_read(e,4,&value,&x,&z)==int(expected.width));CHECK(value==expected.value && x==expected.x && z==expected.z);
  CHECK(vs_ir_read(e,5,&value,&x,&z)==int(expected.width));CHECK(value==(7&mask(expected.width)) && x==0 && z==0);
  CHECK(vs_ir_ticks(e)==1);vs_ir_free(e);return 0;
}
int main(){
  CHECK(vs_ir_abi_version()==2);CHECK(vs_ir_process_v2(nullptr,0,0,0,0,0,0,6)<0);
  for(uint8_t op=6;op<=9;op++)for(int a=0;a<4;a++)for(int b=0;b<4;b++){
    Word lhs{1,uint64_t(a==1),uint64_t(a==2),uint64_t(a==3)},rhs{1,uint64_t(b==1),uint64_t(b==2),uint64_t(b==3)};
    CHECK(run(op,lhs,rhs,truth(op,lhs,rhs,1),4)==0);
  }
  for(uint8_t op=6;op<=9;op++)for(uint32_t width:{8U,64U}){
    Word lhs{2,0,1,2},rhs{64,0xaaaaaaaaaaaaaaaaULL,0x4444444444444444ULL,0x1111111111111111ULL};
    CHECK(run(op,lhs,rhs,truth(op,lhs,rhs,width),4)==0);
  }
  for(uint32_t workers:{1U,4U}){
    CHECK(run(10,{64,UINT64_MAX},{1,1},{64,0},workers)==0);
    CHECK(run(11,{64,0},{1,1},{64,UINT64_MAX},workers)==0);
    CHECK(run(12,{8,255},{8,2},{8,254},workers)==0);
    for(uint8_t op=10;op<=12;op++){
      CHECK(run(op,{64,0,uint64_t{1}<<50},{1,0},{8,0,255},workers)==0);
      CHECK(run(op,{1,0},{64,0,0,uint64_t{1}<<63},{8,0,255},workers)==0);
    }
  }
  auto e=vs_ir_new();CHECK(e);CHECK(vs_ir_signal(e,1,0,0,0)==0);
  CHECK(vs_ir_process_v2(e,0,0,0,1,0,0,6)<0);CHECK(vs_ir_process_v2(e,0,0,0,0,0,0,13)<0);CHECK(vs_ir_process(e,0,0,0,0,0,6)<0);vs_ir_free(e);
  return 0;
}
