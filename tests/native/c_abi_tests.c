#include "velosim.h"
#include <stdio.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "C ABI check failed at %d\n", __LINE__); return 1; } } while (0)
int main(void) {
  CHECK(vs_ir_abi_version()==2 && vs_sched_abi_version()==1);
  void* engine=vs_ir_new(); CHECK(engine);
  CHECK(vs_ir_ticks(engine)==0);
  CHECK(vs_ir_drive(engine,NULL,NULL,NULL,NULL,0,1)==0);
  CHECK(vs_ir_ticks(engine)==1);
  CHECK(vs_ir_build_profile()<=1);
  CHECK(vs_ir_signal(engine,1,0,0,0)==0);
  CHECK(vs_ir_signal(engine,8,2,0,0)==1);
  CHECK(vs_ir_signal(engine,8,3,0,0)==2);
  CHECK(vs_ir_signal(engine,8,0,0,0)==3);
  CHECK(vs_ir_process_v2(engine,0,3,1,2,0,0,10)==0);
  uint32_t clock_id=0;uint64_t one=1,zero=0,value=0,x=0,z=0;
  CHECK(vs_ir_drive(engine,&clock_id,&one,&zero,&zero,1,2)==1);
  CHECK(vs_ir_read(engine,3,&value,&x,&z)==8 && value==5 && x==0 && z==0);
  CHECK(vs_ir_ticks(engine)==2);
  CHECK(vs_ir_drive(engine,NULL,NULL,NULL,NULL,0,0)==-1);
  CHECK(vs_ir_ticks(engine)==2);
  vs_ir_free(engine);
  void* scheduler=vs_sched_new(1); CHECK(scheduler);
  CHECK(vs_sched_drive(scheduler,NULL,NULL,0,1)==0);
  CHECK(vs_sched_ticks(scheduler)==1);
  vs_sched_free(scheduler);
  return 0;
}
