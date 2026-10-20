#include "velosim.h"
#include <cstdint>
#include <iostream>
#include <limits>
#define CHECK(test) do { if(!(test)){std::cerr<<__LINE__<<": "<<#test<<"\n";return 1;} }while(false)
int main(){
  CHECK(vs_ir_signal(nullptr,1,0,0,0)<0);
  CHECK(vs_ir_read(nullptr,0,nullptr,nullptr,nullptr)<0);
  CHECK(vs_ir_drive(nullptr,nullptr,nullptr,nullptr,nullptr,0,1)<0);
  auto e=vs_ir_new();CHECK(e);
  CHECK(vs_ir_signal(e,0,0,0,0)<0);
  CHECK(vs_ir_signal(e,65,0,0,0)<0);
  CHECK(vs_ir_signal(e,1,0,1,1)<0);
  CHECK(vs_ir_signal(e,1,0,0,0)==0);
  CHECK(vs_ir_signal(e,64,UINT64_MAX,0,0)==1);
  CHECK(vs_ir_signal(e,1,1,0,0)==2);
  CHECK(vs_ir_signal(e,2,2,0,0)==3);
  CHECK(vs_ir_signal(e,64,0,0,0)==4);
  CHECK(vs_ir_process(e,0,1,1,3,0,5)<0);
  CHECK(vs_ir_process(e,0,1,1,2,0,5)==0);
  CHECK(vs_ir_process(e,0,1,1,2,0,5)==-2);
  CHECK(vs_ir_process(e,0,4,1,2,0,0)==0);
  uint32_t id=0;uint64_t val=1,x=0,z=0;
  CHECK(vs_ir_drive(e,&id,&val,&x,&z,1,4)==2);
  uint64_t read=99,rx=99,rz=99;
  CHECK(vs_ir_read(e,1,&read,&rx,&rz)==64 && read==0 && rx==0 && rz==0);
  CHECK(vs_ir_read(e,4,&read,&rx,&rz)==64 && read==UINT64_MAX);
  uint32_t duplicate[]={0,0};uint64_t values[]={0,1},zeros[]={0,0};
  CHECK(vs_ir_drive(e,duplicate,values,zeros,zeros,2,1)==-2);
  CHECK(vs_ir_read(e,0,&read,&rx,&rz)==1 && read==1);
  CHECK(vs_ir_drive(e,nullptr,nullptr,nullptr,nullptr,0,0)==-1);
  CHECK(vs_ir_drive(e,nullptr,nullptr,nullptr,nullptr,0,257)==-1);
  vs_ir_free(e);vs_ir_free(nullptr);
  CHECK(!vs_sched_new(0));
  auto s=vs_sched_new(4);CHECK(s);
  CHECK(vs_sched_add(s,0,2,1,1,0,0)==0);
  CHECK(vs_sched_add(s,0,2,1,1,0,0)==-2);
  CHECK(vs_sched_add(s,0,3,1,1,0,0)==0);
  uint32_t ids[]={0,1};uint8_t init[]={0,1};
  CHECK(vs_sched_drive(s,ids,init,2,4)==0);
  uint8_t high=1;CHECK(vs_sched_drive(s,&id,&high,1,4)==2);
  CHECK(vs_sched_get(s,2)==1 && vs_sched_get(s,3)==1 && vs_sched_ticks(s)==2);
  uint8_t bad=4;CHECK(vs_sched_drive(s,&id,&bad,1,1)==-2);
  CHECK(vs_sched_ticks(s)==2);
  vs_sched_free(s);vs_sched_free(nullptr);
  std::cout<<"PASS native ABI guards, atomic drives, 64-bit rollover, scheduler\n";
}
